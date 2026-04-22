using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WheelSwitcher;

/// <summary>
/// Global low-level keyboard hook.
/// - Swallows Alt+Tab so Windows' built-in switcher never sees it.
/// - Raises AltTabPressed when the chord fires.
/// - Raises AltReleased when the Alt key is released (used to commit the wheel selection).
/// - While the wheel is open, also swallows subsequent Tab / Esc to keep them out of the
///   foreground app and lets us use them for navigation.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    public event Action? AltTabPressed;
    public event Action? AltReleased;
    public event Action? EscapePressed;
    public event Action? ShiftTabPressed;

    // Set from outside while the wheel is visible. Changes the hook's swallowing behaviour.
    public bool WheelActive { get; set; }

    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _proc;   // kept as a field so the GC doesn't collect the delegate

    public void Install()
    {
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Ignore anything the system (or another app) synthesised - we only react to real user input.
        bool injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;

        int msg = wParam.ToInt32();
        bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp   = msg == NativeMethods.WM_KEYUP   || msg == NativeMethods.WM_SYSKEYUP;

        bool altHeld = (data.flags & NativeMethods.LLKHF_ALTDOWN) != 0;

        // Alt+Tab: Windows sends WM_SYSKEYDOWN for Tab when Alt is held. The LLKHF_ALTDOWN flag is our tell.
        if (!injected && isKeyDown && data.vkCode == NativeMethods.VK_TAB && altHeld)
        {
            // Is Shift also held? If so and wheel is already active, treat as reverse-navigate.
            bool shiftHeld = (short)NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) < 0;

            if (WheelActive && shiftHeld)
                ShiftTabPressed?.Invoke();
            else
                AltTabPressed?.Invoke();

            return (IntPtr)1;   // swallow - Windows never sees this Tab
        }

        // Once the wheel is up we also want to swallow Tab alone (navigate forward) and Esc (cancel).
        if (WheelActive && !injected && isKeyDown)
        {
            if (data.vkCode == NativeMethods.VK_TAB)
            {
                AltTabPressed?.Invoke();   // advance selection
                return (IntPtr)1;
            }
            if (data.vkCode == NativeMethods.VK_ESCAPE)
            {
                EscapePressed?.Invoke();
                return (IntPtr)1;
            }
        }

        // Alt release -> commit the current selection. Both LMENU and RMENU count.
        if (WheelActive && isKeyUp &&
            (data.vkCode == NativeMethods.VK_MENU ||
             data.vkCode == NativeMethods.VK_LMENU ||
             data.vkCode == NativeMethods.VK_RMENU))
        {
            AltReleased?.Invoke();
            // Don't swallow the up event - the foreground app may need to see Alt released cleanly.
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);
}
