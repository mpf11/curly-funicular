#![windows_subsystem = "windows"]

mod keyboard_hook;
mod updater;
mod wheel_geometry;
mod wheel_window;
mod window_activator;
mod window_tracker;

use std::sync::atomic::Ordering;
use windows::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, BOOL};
use windows::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED};
use windows::Win32::System::Threading::CreateMutexW;
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetMessageW, TranslateMessage, MSG,
};
use windows::Win32::Foundation::GetLastError;
use windows::core::w;

fn main() -> windows::core::Result<()> {
    unsafe {
        // Single-instance guard: bail if another copy is already running.
        let mutex = CreateMutexW(None, BOOL(0), w!("Global\\WheelSwitcher_SingleInstance"))?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let _ = CloseHandle(mutex);
            return Ok(());
        }

        // COM initialisation — required by DirectWrite.
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);

        // Create the overlay window. Ownership of WheelState is held by GWLP_USERDATA
        // and freed in WM_NCDESTROY — main just keeps the HWND.
        let hwnd = wheel_window::create_wheel_window()?;

        // Point the keyboard hook at our window and install it.
        keyboard_hook::WHEEL_HWND.store(hwnd.0 as isize, Ordering::Relaxed);
        keyboard_hook::install()?;

        updater::spawn_background_check();

        // Standard Win32 message loop.
        let mut msg = MSG::default();
        loop {
            let ret = GetMessageW(&mut msg, None, 0, 0);
            if ret.0 == 0 || ret.0 == -1 {
                break;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        keyboard_hook::uninstall();
        CoUninitialize();
        let _ = CloseHandle(mutex);
    }
    Ok(())
}
