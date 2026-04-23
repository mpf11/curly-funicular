use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, VIRTUAL_KEY, VK_ESCAPE, VK_LMENU, VK_MENU, VK_RMENU, VK_SHIFT, VK_TAB,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, PostMessageW, SetWindowsHookExW, UnhookWindowsHookEx, HHOOK,
    KBDLLHOOKSTRUCT, WH_KEYBOARD_LL, WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP,
    LLKHF_ALTDOWN, LLKHF_INJECTED,
};

// Custom window messages posted to the wheel window.
pub const MSG_ALT_TAB: u32 = 0x8001;       // WM_APP + 1
pub const MSG_ALT_SHIFT_TAB: u32 = 0x8002; // WM_APP + 2
pub const MSG_ALT_RELEASED: u32 = 0x8003;  // WM_APP + 3
pub const MSG_ESCAPE: u32 = 0x8004;        // WM_APP + 4
pub const MSG_TRAY: u32 = 0x8005;          // WM_APP + 5

/// HWND of the wheel window, stored as isize (HWND.0 is *mut c_void in 0.58).
pub static WHEEL_HWND: AtomicIsize = AtomicIsize::new(0);

/// Set when the wheel is visible so the hook swallows Tab and Esc.
pub static WHEEL_ACTIVE: AtomicBool = AtomicBool::new(false);

static HOOK_HANDLE: AtomicIsize = AtomicIsize::new(0);

/// Install the WH_KEYBOARD_LL hook. The calling thread must have a message loop.
pub fn install() -> windows::core::Result<()> {
    unsafe {
        let hmod = GetModuleHandleW(windows::core::PCWSTR::null())?;
        let hhook = SetWindowsHookExW(WH_KEYBOARD_LL, Some(hook_proc), hmod, 0)?;
        HOOK_HANDLE.store(hhook.0 as isize, Ordering::Relaxed);
        Ok(())
    }
}

/// Remove the hook. Safe to call even if the hook was never installed.
pub fn uninstall() {
    let raw = HOOK_HANDLE.swap(0, Ordering::Relaxed);
    if raw != 0 {
        unsafe {
            let _ = UnhookWindowsHookEx(HHOOK(raw as *mut c_void));
        }
    }
}

unsafe extern "system" fn hook_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code < 0 {
        return CallNextHookEx(
            HHOOK(HOOK_HANDLE.load(Ordering::Relaxed) as *mut c_void),
            code, wparam, lparam,
        );
    }

    let data = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
    let wheel_active = WHEEL_ACTIVE.load(Ordering::Relaxed);
    let hwnd = HWND(WHEEL_HWND.load(Ordering::Relaxed) as *mut c_void);

    let msg = wparam.0 as u32;
    let is_key_down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
    let is_key_up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

    let flags = data.flags.0;
    let alt_down = (flags & LLKHF_ALTDOWN.0) != 0;
    let injected = (flags & LLKHF_INJECTED.0) != 0;

    // Alt+Tab (and Alt+Shift+Tab): swallow and post our custom message.
    if !injected && is_key_down && VIRTUAL_KEY(data.vkCode as u16) == VK_TAB && alt_down {
        let shift_held = (GetKeyState(VK_SHIFT.0 as i32) as u16 & 0x8000) != 0;
        let post_msg = if wheel_active && shift_held {
            MSG_ALT_SHIFT_TAB
        } else {
            MSG_ALT_TAB
        };
        let _ = PostMessageW(hwnd, post_msg, WPARAM(0), LPARAM(0));
        return LRESULT(1); // swallow — Windows' built-in Alt+Tab never sees this
    }

    // While the wheel is open, intercept plain Tab (advance) and Esc (cancel).
    if wheel_active && !injected && is_key_down {
        match VIRTUAL_KEY(data.vkCode as u16) {
            VK_TAB => {
                let _ = PostMessageW(hwnd, MSG_ALT_TAB, WPARAM(0), LPARAM(0));
                return LRESULT(1);
            }
            VK_ESCAPE => {
                let _ = PostMessageW(hwnd, MSG_ESCAPE, WPARAM(0), LPARAM(0));
                return LRESULT(1);
            }
            _ => {}
        }
    }

    // Alt key up while wheel is open → commit the selection.
    // Don't swallow the up event so the foreground app sees Alt released cleanly.
    if wheel_active && is_key_up {
        match VIRTUAL_KEY(data.vkCode as u16) {
            VK_MENU | VK_LMENU | VK_RMENU => {
                let _ = PostMessageW(hwnd, MSG_ALT_RELEASED, WPARAM(0), LPARAM(0));
            }
            _ => {}
        }
    }

    CallNextHookEx(
        HHOOK(HOOK_HANDLE.load(Ordering::Relaxed) as *mut c_void),
        code, wparam, lparam,
    )
}
