use std::ffi::c_void;
use std::mem::size_of;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, VK_MENU,
};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, GetForegroundWindow, GetWindowThreadProcessId,
    IsIconic, SetForegroundWindow, ShowWindow, SwitchToThisWindow,
    SystemParametersInfoW, SPI_GETFOREGROUNDLOCKTIMEOUT, SPI_SETFOREGROUNDLOCKTIMEOUT,
    SW_RESTORE, SW_SHOW, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
};

unsafe fn get_foreground_lock_timeout() -> u32 {
    let mut value: u32 = 0;
    let _ = SystemParametersInfoW(
        SPI_GETFOREGROUNDLOCKTIMEOUT, 0,
        Some(&mut value as *mut u32 as *mut c_void),
        SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
    );
    value
}

/// pvParam carries the value directly for SPI_SETFOREGROUNDLOCKTIMEOUT.
/// Flags=0 keeps it in-memory only — no registry write, no broadcast.
unsafe fn set_foreground_lock_timeout(ms: u32) {
    let _ = SystemParametersInfoW(
        SPI_SETFOREGROUNDLOCKTIMEOUT, 0,
        Some(ms as usize as *mut c_void),
        SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
    );
}

/// Inject a synthetic Alt key-up so the system keyboard state matches reality.
/// The real Alt-up was swallowed at the keyboard hook (see keyboard_hook.rs) to
/// keep the foreground-activation token with our process; without this injection
/// the system would believe Alt is still held.
pub fn replay_alt_up() {
    unsafe {
        let input = INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: VK_MENU,
                    wScan: 0,
                    dwFlags: KEYEVENTF_KEYUP,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        };
        SendInput(&[input], size_of::<INPUT>() as i32);
    }
}

/// Bring hwnd to the foreground reliably.
pub fn activate(hwnd: HWND) {
    if hwnd.0.is_null() {
        return;
    }
    unsafe {
        if IsIconic(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }

        let our_tid = GetCurrentThreadId();
        let fg_hwnd = GetForegroundWindow();
        let fg_tid = GetWindowThreadProcessId(fg_hwnd, None);

        // Belt-and-suspenders: zero the foreground-lock timeout. The hook swallowing
        // Alt-up already prevents the source app from arming this, but Alt-down
        // repeats (while the user held Alt) may have armed it too.
        let prev_lock = get_foreground_lock_timeout();
        set_foreground_lock_timeout(0);

        // Strategy 1: direct. The hook kept the last-input token on our thread
        // by swallowing Alt-up, so we have foreground rights here.
        let _ = BringWindowToTop(hwnd);
        let _ = SetForegroundWindow(hwnd);

        if GetForegroundWindow().0 != hwnd.0 {
            // Strategy 2: AttachThreadInput fallback.
            if fg_tid != 0 && fg_tid != our_tid {
                let _ = AttachThreadInput(our_tid, fg_tid, true);
                let _ = BringWindowToTop(hwnd);
                let _ = SetForegroundWindow(hwnd);
                let _ = AttachThreadInput(our_tid, fg_tid, false);
            }
        }

        if GetForegroundWindow().0 != hwnd.0 {
            // Strategy 3: last-ditch for restricted windows. Does not bypass UAC
            // integrity levels (elevated Task Manager still won't activate from a
            // non-elevated process).
            SwitchToThisWindow(hwnd, true);
        }

        set_foreground_lock_timeout(prev_lock);

        let _ = ShowWindow(hwnd, SW_SHOW);

        // Now that the target is foreground, replay Alt-up so the newly-activated
        // window sees the key-up (and no app sees a stuck Alt modifier on the next
        // keystroke). Injected events are filtered by the hook's !injected checks.
        replay_alt_up();
    }
}
