use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, GetForegroundWindow, GetWindowThreadProcessId,
    IsIconic, SetForegroundWindow, ShowWindow, SW_RESTORE, SW_SHOW,
};

/// Bring hwnd to the foreground reliably.
///
/// SetForegroundWindow is blocked by Windows when the caller doesn't own the input
/// queue. Temporarily attaching to the current foreground thread's input queue
/// grants the right to steal focus.
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

        if fg_tid != 0 && fg_tid != our_tid {
            let _ = AttachThreadInput(our_tid, fg_tid, true);
            let _ = BringWindowToTop(hwnd);
            let _ = SetForegroundWindow(hwnd);
            let _ = AttachThreadInput(our_tid, fg_tid, false);
        } else {
            let _ = BringWindowToTop(hwnd);
            let _ = SetForegroundWindow(hwnd);
        }

        let _ = ShowWindow(hwnd, SW_SHOW);
    }
}
