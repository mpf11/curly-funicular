# Wheel Switcher — alternative Alt+Tab for Windows 11

A full-screen, glassy, translucent pie-wheel replacement for the built-in
Alt+Tab switcher. Up to eight windows occupy fixed sectors on the wheel;
windows beyond that limit appear in a scrollable overflow column to the left.
Move the mouse into a sector to select it, release Alt to switch, or
click-and-drag to rearrange.

## Build

```
# From the repo root, on Windows with .NET 10 SDK installed:
dotnet build WheelSwitcher.sln -c Release

# Run:
dotnet run --project WheelSwitcher\WheelSwitcher.csproj -c Release
```

No admin rights needed — low-level keyboard hooks work from a normal user
account. The app lives in the system tray; right-click the icon to exit.

## How it works

1. **KeyboardHook.cs** installs a `WH_KEYBOARD_LL` global hook. When Alt+Tab
   is seen (Tab keydown with `LLKHF_ALTDOWN` set), the hook returns `1`,
   which tells Windows to drop the keystroke entirely — the default Alt+Tab
   switcher never sees it. While the wheel is open, plain Tab / Shift+Tab /
   Esc are also swallowed so we can bind them to navigation without the
   foreground app reacting.

2. **WindowTracker.cs** walks `EnumWindows` and filters to the set of
   windows the real Alt+Tab would show: visible, not cloaked (handles
   suspended UWP apps and other-virtual-desktop windows), not a pure tool
   window, non-owned. Slot assignments are stable: a window keeps its
   sector until it's closed, and new windows fill the lowest free slot.
   Windows beyond the eight-slot limit are tracked in an overflow list
   (Z-order / MRU sorted) and can be swapped onto the wheel via drag.

3. **WheelWindow.xaml[.cs]** is a full-screen borderless WPF window with
   `AllowsTransparency="True"` spanning the entire virtual screen. On the
   monitor under the cursor, it:
   - centers the mouse via `SetCursorPos`
   - draws eight pie slices with soft, edge-fading dividers
   - hosts a live **DWM thumbnail** (`DwmRegisterThumbnail`) for each slot
     — the same GPU-composited preview the taskbar uses, always current
     with zero per-frame cost
   - places the app icon just outside the hub along each slice's center ray
   - hit-tests by angle-from-center, so moving toward a slice selects it
     even before the cursor reaches the thumbnail
   - shows an **overflow sidebar** (glass panel, left of the wheel) for any
     windows beyond eight; rows can be dragged onto wheel slots

4. **Pre-warm** — at startup and after every dismiss, `PreWarm()` builds the
   full visual tree and loads icons while the wheel is still invisible
   (`Opacity=0`, `WS_EX_TRANSPARENT`). The actual `Present()` call then only
   needs to flip visibility and register DWM thumbnails, keeping latency
   close to native Alt+Tab.

5. **WindowActivator.cs** does the standard `AttachThreadInput` dance to
   bring the chosen window to the foreground — necessary because Windows
   blocks `SetForegroundWindow` from processes that don't own the current
   foreground.

## Design notes worth knowing

- **DWM thumbnails can't be clipped to non-rectangular shapes.** They are
  rectangular by construction. The wheel geometry defines the *interaction*
  zones (pie slices), while each thumbnail sits as the largest 16:9 rect
  that fits inside its slice, centered on the slice's mid-ray. The pie
  overlay with soft dividers sits on top as the visual language.

- **Slot 0 is at 12 o'clock**, slots progress clockwise. On first Alt+Tab,
  pre-selection advances to slot 1 (the previously active window),
  matching the classic Alt+Tab muscle memory.

- **Per-monitor-v2 DPI awareness** is declared in the manifest, and the
  DWM destination rect is converted to device pixels from the visual's
  DPI transform — otherwise thumbnails draw at the wrong size on scaled
  displays.

- **Single-instance** is enforced via a named global mutex. Two copies
  would double-install the hook and fire every handler twice.

## Keyboard & mouse

| Action                                | Effect                                   |
|---------------------------------------|------------------------------------------|
| Alt+Tab                               | Open wheel / advance selection           |
| Alt+Shift+Tab                         | Reverse-advance selection                |
| Tab (while wheel open)                | Advance selection                        |
| Move mouse into a sector              | Select that sector                       |
| Click a sector                        | Switch to that window immediately        |
| Click+drag sector → sector            | Swap the two windows' positions          |
| Move mouse into overflow row          | Highlight that window                    |
| Click an overflow row                 | Switch to that window immediately        |
| Drag overflow row → wheel sector      | Move window from overflow onto the wheel |
| Release Alt                           | Commit selection and switch              |
| Esc                                   | Cancel, no switch                        |

## Known limitations

- UWP apps on other virtual desktops are filtered out by design (cloaked
  check). Cross-desktop switching would require the undocumented
  `IVirtualDesktopManager` COM interface.
- The wheel anchors to whichever monitor has the cursor. To fix it to a
  specific monitor, change `GetMonitorRectContaining` in `WheelWindow.xaml.cs`.
- Thumbnails do not rotate to follow the slice angle — DWM composites
  them axis-aligned. The alternative (bitmap captures rotated in software)
  would lose the live-preview quality.
