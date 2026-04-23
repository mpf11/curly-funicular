use std::collections::HashMap;
use windows::Win32::Foundation::{BOOL, HWND, LPARAM};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetShellWindow, GetWindow, GetWindowLongW, GetWindowTextLengthW,
    GetWindowTextW, IsWindowVisible, GW_OWNER, GWL_EXSTYLE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW,
};

pub const MAX_SLOTS: usize = 8;

#[derive(Clone, Debug)]
pub struct TrackedWindow {
    pub hwnd: HWND,
    pub title: String,
    pub slot: usize,
}

pub struct WindowTracker {
    slots: [Option<TrackedWindow>; MAX_SLOTS],
    // HashMap keyed by HWND as usize (HWND.0 is *mut c_void in windows-rs 0.58)
    handle_to_slot: HashMap<usize, usize>,
    overflow: Vec<TrackedWindow>,
    self_hwnd: HWND,
    pub previous_hwnd: HWND,
}

impl WindowTracker {
    pub fn new(self_hwnd: HWND) -> Self {
        Self {
            slots: Default::default(),
            handle_to_slot: HashMap::new(),
            overflow: Vec::new(),
            self_hwnd,
            previous_hwnd: HWND(std::ptr::null_mut()),
        }
    }

    pub fn slots(&self) -> &[Option<TrackedWindow>; MAX_SLOTS] {
        &self.slots
    }

    pub fn overflow(&self) -> &[TrackedWindow] {
        &self.overflow
    }

    /// Enumerate current alt-tab windows, prune closed ones, fill empty slots.
    pub fn refresh(&mut self) {
        let live = self.enumerate_alt_tab_windows();

        // Z-order: live[0] = current foreground, live[1] = previously active.
        self.previous_hwnd = live.get(1).map(|w| w.hwnd).unwrap_or(HWND(std::ptr::null_mut()));

        // Drop slots whose window is no longer alive.
        for i in 0..MAX_SLOTS {
            if let Some(ref w) = self.slots[i] {
                if !live.iter().any(|lw| lw.hwnd == w.hwnd) {
                    self.handle_to_slot.remove(&(w.hwnd.0 as usize));
                    self.slots[i] = None;
                }
            }
        }

        // Update titles for windows that survived.
        for lw in &live {
            if let Some(&slot) = self.handle_to_slot.get(&(lw.hwnd.0 as usize)) {
                if let Some(ref mut tw) = self.slots[slot] {
                    tw.title = lw.title.clone();
                }
            }
        }

        // Assign new windows to free slots; overflow the rest.
        self.overflow.clear();
        for lw in live {
            if self.handle_to_slot.contains_key(&(lw.hwnd.0 as usize)) {
                continue;
            }
            match self.find_free_slot() {
                Some(slot) => {
                    self.handle_to_slot.insert(lw.hwnd.0 as usize, slot);
                    self.slots[slot] = Some(TrackedWindow { slot, ..lw });
                }
                None => {
                    self.overflow.push(lw);
                }
            }
        }
    }

    fn find_free_slot(&self) -> Option<usize> {
        (0..MAX_SLOTS).find(|&i| self.slots[i].is_none())
    }

    /// Swap two wheel slots.
    pub fn swap_slots(&mut self, a: usize, b: usize) {
        if a == b || a >= MAX_SLOTS || b >= MAX_SLOTS {
            return;
        }
        self.slots.swap(a, b);
        if let Some(ref mut w) = self.slots[a] {
            w.slot = a;
            self.handle_to_slot.insert(w.hwnd.0 as usize, a);
        }
        if let Some(ref mut w) = self.slots[b] {
            w.slot = b;
            self.handle_to_slot.insert(w.hwnd.0 as usize, b);
        }
    }

    /// Move an overflow entry onto a wheel slot (evicting the current occupant to overflow).
    pub fn swap_slot_with_overflow(&mut self, slot: usize, overflow_idx: usize) {
        if slot >= MAX_SLOTS || overflow_idx >= self.overflow.len() {
            return;
        }
        let mut ov_win = self.overflow.remove(overflow_idx);
        ov_win.slot = slot;
        self.handle_to_slot.insert(ov_win.hwnd.0 as usize, slot);

        if let Some(mut evicted) = self.slots[slot].take() {
            self.handle_to_slot.remove(&(evicted.hwnd.0 as usize));
            evicted.slot = usize::MAX;
            self.overflow.insert(overflow_idx, evicted);
        }
        self.slots[slot] = Some(ov_win);
    }

    /// Reorder two overflow entries.
    pub fn swap_overflow(&mut self, a: usize, b: usize) {
        if a != b && a < self.overflow.len() && b < self.overflow.len() {
            self.overflow.swap(a, b);
        }
    }

    // ---- Enumeration ----

    fn enumerate_alt_tab_windows(&self) -> Vec<TrackedWindow> {
        struct State {
            result: Vec<TrackedWindow>,
            shell: HWND,
            self_hwnd: HWND,
        }

        unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
            let state = &mut *(lparam.0 as *mut State);
            if hwnd == state.shell || hwnd == state.self_hwnd {
                return BOOL(1);
            }
            if !is_alt_tab_eligible(hwnd) {
                return BOOL(1);
            }
            let title = get_window_title(hwnd);
            if title.is_empty() {
                return BOOL(1);
            }
            state.result.push(TrackedWindow { hwnd, title, slot: 0 });
            BOOL(1)
        }

        let mut state = State {
            result: Vec::new(),
            shell: unsafe { GetShellWindow() },
            self_hwnd: self.self_hwnd,
        };
        unsafe {
            let _ = EnumWindows(Some(enum_proc), LPARAM(&mut state as *mut _ as isize));
        }
        state.result
    }
}

fn is_alt_tab_eligible(hwnd: HWND) -> bool {
    unsafe {
        if !IsWindowVisible(hwnd).as_bool() {
            return false;
        }

        let owner = GetWindow(hwnd, GW_OWNER).unwrap_or_default();
        let ex_style = GetWindowLongW(hwnd, GWL_EXSTYLE) as u32;
        let is_tool = (ex_style & WS_EX_TOOLWINDOW.0) != 0;
        let is_app = (ex_style & WS_EX_APPWINDOW.0) != 0;

        if !owner.0.is_null() && !is_app {
            return false;
        }
        if is_tool && !is_app {
            return false;
        }

        // Cloaked check: suspended UWP apps and windows on other virtual desktops.
        let mut cloaked: u32 = 0;
        if DwmGetWindowAttribute(
            hwnd,
            DWMWA_CLOAKED,
            &mut cloaked as *mut u32 as *mut std::ffi::c_void,
            std::mem::size_of::<u32>() as u32,
        )
        .is_ok()
            && cloaked != 0
        {
            return false;
        }

        true
    }
}

fn get_window_title(hwnd: HWND) -> String {
    unsafe {
        let len = GetWindowTextLengthW(hwnd);
        if len <= 0 {
            return String::new();
        }
        let mut buf = vec![0u16; (len + 1) as usize];
        let copied = GetWindowTextW(hwnd, &mut buf);
        if copied == 0 {
            return String::new();
        }
        String::from_utf16_lossy(&buf[..copied as usize])
    }
}
