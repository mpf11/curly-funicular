using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace WheelSwitcher;

/// <summary>
/// Describes a window slotted onto the wheel.
/// </summary>
public sealed class TrackedWindow
{
    public IntPtr Handle { get; init; }
    public string Title { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public int Slot { get; set; }         // 0..7
    public DateTime LastSeen { get; set; }

    public override string ToString() => $"[{Slot}] {Title}";
}

public sealed class WindowTracker
{
    public const int MaxSlots = 8;

    // Slot -> Window (or null if empty)
    private readonly TrackedWindow?[] _slots = new TrackedWindow?[MaxSlots];

    // Handle -> slot index, so we keep assignments stable across refreshes.
    private readonly Dictionary<IntPtr, int> _handleToSlot = new();

    private readonly IntPtr _selfWindow;

    public WindowTracker(IntPtr selfWindow)
    {
        _selfWindow = selfWindow;
    }

    public IReadOnlyList<TrackedWindow?> Slots => _slots;

    /// <summary>
    /// Enumerates current top-level windows, prunes closed ones, and fills empty slots
    /// in MRU-ish (Z-order) order up to MaxSlots.
    /// </summary>
    public void Refresh()
    {
        var live = EnumerateAltTabWindows();

        // 1. Drop slots whose handle is gone.
        for (int i = 0; i < MaxSlots; i++)
        {
            var s = _slots[i];
            if (s is null) continue;
            if (!live.Any(w => w.Handle == s.Handle))
            {
                _handleToSlot.Remove(s.Handle);
                WindowActivator.EvictIcon(s.Handle);
                _slots[i] = null;
            }
        }

        // 2. Update titles for surviving windows.
        foreach (var w in live)
        {
            if (_handleToSlot.TryGetValue(w.Handle, out int slot))
            {
                var tracked = _slots[slot]!;
                tracked.Title = w.Title;
                tracked.LastSeen = DateTime.UtcNow;
            }
        }

        // 3. Add new windows into the lowest free slot. Z-order already comes MRU-first from EnumWindows.
        foreach (var w in live)
        {
            if (_handleToSlot.ContainsKey(w.Handle)) continue;

            int free = FindFreeSlot();
            if (free < 0) break;    // wheel full

            w.Slot = free;
            _slots[free] = w;
            _handleToSlot[w.Handle] = free;
        }
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < MaxSlots; i++)
            if (_slots[i] is null) return i;
        return -1;
    }

    /// <summary>Swap two slot contents (used for drag-to-reorder).</summary>
    public void SwapSlots(int a, int b)
    {
        if (a == b) return;
        if (a < 0 || a >= MaxSlots || b < 0 || b >= MaxSlots) return;

        (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
        if (_slots[a] is not null) { _slots[a]!.Slot = a; _handleToSlot[_slots[a]!.Handle] = a; }
        if (_slots[b] is not null) { _slots[b]!.Slot = b; _handleToSlot[_slots[b]!.Handle] = b; }
    }

    // --- Enumeration ---
    private List<TrackedWindow> EnumerateAltTabWindows()
    {
        var result = new List<TrackedWindow>();
        IntPtr shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell) return true;
            if (hWnd == _selfWindow) return true;
            if (!IsAltTabEligible(hWnd)) return true;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            result.Add(new TrackedWindow
            {
                Handle = hWnd,
                Title = title,
                ProcessPath = GetProcessPath(hWnd),
                LastSeen = DateTime.UtcNow
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Matches (roughly) the set of windows the real Alt+Tab switcher shows.
    /// Key filters: visible, not cloaked (UWP trick), not a tool window unless app-window,
    /// and - for UWP - skip the invisible ApplicationFrameHost wrapper duplicates.
    /// </summary>
    private static bool IsAltTabEligible(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        // Skip owned windows (like dialogs whose owner is already listed)... actually we DO want popups,
        // but only if they are top-level. Matching real Alt+Tab: show windows where the root owner is itself.
        IntPtr owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

        bool isTool = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;

        if (owner != IntPtr.Zero && !isAppWindow) return false;
        if (isTool && !isAppWindow) return false;

        // Cloaked check - suspended UWP apps and apps on other virtual desktops report cloaked != 0.
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
            return false;

        // Filter out empty-class shell helpers.
        var cls = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, cls, cls.Capacity);
        string className = cls.ToString();
        if (className is "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow" && NativeMethods.IsIconic(hWnd))
        {
            // Minimised UWP windows are fine; it's the invisible duplicates we drop via cloaked check above.
        }

        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int len = NativeMethods.GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessPath(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return "";
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int cap = sb.Capacity;
            if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref cap))
                return sb.ToString();
        }
        finally { NativeMethods.CloseHandle(h); }
        return "";
    }
}
