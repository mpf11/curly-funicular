# Wheel Switcher — alternative Alt+Tab for Windows 11

A full-screen, glassy, translucent pie-wheel replacement for the built-in
Alt+Tab switcher. Up to eight windows occupy fixed sectors; moving the mouse
into a sector selects it, releasing Alt switches to it, and you can
click-and-drag a sector onto another to rearrange.

## Build

```
# From the repo root, on Windows with .NET 8 SDK installed:
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

3. **WheelWindow.xaml[.cs]** is a full-screen borderless WPF window with
   `AllowsTransparency="True"` spanning the entire virtual screen. On the
   monitor under the cursor, it:
   - centers the mouse via `SetCursorPos`
   - draws eight pie slices with soft, edge-fading dividers (linear
     gradient brush, opaque at hub → transparent at rim)
   - hosts a live **DWM thumbnail** (`DwmRegisterThumbnail`) for each slot
     — this is the same GPU-composited preview the taskbar uses, so it's
     always current with zero per-frame cost on our side
   - places the app icon just outside the hub along each slice's center ray
   - hit-tests by angle-from-center, so moving toward a slice selects it
     even before the cursor reaches the thumbnail

4. **WindowActivator.cs** does the standard `AttachThreadInput` dance to
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

| Action                          | Effect                             |
|---------------------------------|------------------------------------|
| Alt+Tab                         | Open wheel / advance selection     |
| Alt+Shift+Tab                   | Reverse-advance selection          |
| Tab (while wheel open)          | Advance selection                  |
| Move mouse into a sector        | Select that sector                 |
| Click a sector                  | Switch to that window immediately  |
| Click+drag sector → sector      | Swap the two windows' positions    |
| Release Alt                     | Commit selection and switch        |
| Esc                             | Cancel, no switch                  |

## Known limitations

- UWP apps on other virtual desktops are filtered out by design (cloaked
  check). If you want cross-desktop switching you'd need to P/Invoke the
  undocumented `IVirtualDesktopManager` COM interface.
- The wheel anchors to whichever monitor has the cursor. Multi-monitor
  users who prefer a fixed monitor can change `GetMonitorRectContaining`
  in `WheelWindow.xaml.cs`.
- Thumbnails do not rotate to follow the slice angle — DWM composites
  them axis-aligned. Keeping them upright was the deliberate choice; the
  alternative (no thumbnails, just tiled bitmap captures we rotate
  ourselves) would lose the "live" quality.
