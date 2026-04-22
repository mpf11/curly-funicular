using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WheelSwitcher;

internal static class WindowActivator
{
    private static readonly Dictionary<IntPtr, ImageSource?> _iconByHwnd = new();
    private static readonly Dictionary<string, ImageSource?> _iconByPath = new();

    public static void EvictIcon(IntPtr hWnd) => _iconByHwnd.Remove(hWnd);
    /// <summary>
    /// Reliably bring hWnd to the foreground. Windows blocks SetForegroundWindow unless
    /// you "own" the foreground; the trick is to attach our input queue to the current
    /// foreground thread's queue, call BringWindowToTop, then detach. This is the same
    /// sequence used by every power-toy-style utility that has to switch windows.
    /// </summary>
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        IntPtr fg = NativeMethods.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0u : NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint ourThread = NativeMethods.GetCurrentThreadId();

        if (fgThread == 0 || fgThread == ourThread)
        {
            NativeMethods.SetForegroundWindow(hWnd);
            NativeMethods.BringWindowToTop(hWnd);
            return;
        }

        try
        {
            NativeMethods.AttachThreadInput(ourThread, fgThread, true);
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
        }
        finally
        {
            NativeMethods.AttachThreadInput(ourThread, fgThread, false);
        }
    }

    /// <summary>
    /// Pull an icon for a window. Try WM_GETICON (big, small2, small), then class icon,
    /// then fall back to ExtractAssociatedIcon on the EXE path.
    /// </summary>
    public static ImageSource? GetIconForWindow(IntPtr hWnd, string processPath)
    {
        if (_iconByHwnd.TryGetValue(hWnd, out var cached)) return cached;

        IntPtr hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL2, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero);

        if (hIcon == IntPtr.Zero)
        {
            IntPtr cls = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICON);
            if (cls == IntPtr.Zero) cls = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICONSM);
            hIcon = cls;
        }

        if (hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _iconByHwnd[hWnd] = src;
                return src;
            }
            catch { }
        }

        // Fallback: pull from the EXE (expensive — cached by path).
        if (!string.IsNullOrEmpty(processPath))
        {
            if (_iconByPath.TryGetValue(processPath, out var pathCached))
            {
                _iconByHwnd[hWnd] = pathCached;
                return pathCached;
            }

            if (File.Exists(processPath))
            {
                try
                {
                    using var ico = Icon.ExtractAssociatedIcon(processPath);
                    if (ico is not null)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            ico.Handle,
                            System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        _iconByPath[processPath] = src;
                        _iconByHwnd[hWnd] = src;
                        return src;
                    }
                }
                catch { }
            }
            _iconByPath[processPath] = null;
        }

        _iconByHwnd[hWnd] = null;
        return null;
    }
}
