# wheel_switcher

A Windows Alt+Tab replacement built in Rust. Instead of a flat taskbar strip, it shows a circular wheel of up to 8 live window thumbnails. Windows beyond 8 slots appear in an overflow panel to the left of the wheel.

## Features

- **Circular wheel** — up to 8 slots arranged radially, each showing a DWM live thumbnail
- **Overflow panel** — a sidebar for windows beyond the 8 slots
- **Drag-and-drop reordering** — drag any wheel slot to another slot or to the overflow panel; drag any overflow row onto a wheel slot; reorder within the overflow panel
- **Sector selection by angle** — hovering anywhere outside the inner hub (including outside the outer ring) highlights the nearest sector; the hub is a dead zone that falls back to the default selection
- **Keyboard navigation** — Tab / Shift+Tab to cycle through slots; Esc to cancel; releasing Alt commits the selection
- **Tray icon** — right-click to exit

## Building and Running

```
cargo build --release
```

The binary is `target/release/wheel_switcher.exe`. Run it once; it enforces a single-instance guard via a named mutex. Once running, press Alt+Tab as normal.

## How It Works

### Triggering the Wheel

A global low-level keyboard hook (`keyboard_hook.rs`) intercepts Alt+Tab before the system can act on it. While the wheel is open, the hook also swallows Tab (advance selection), Shift+Tab (go back), and Esc (cancel), posting custom window messages to the main window. Releasing Alt posts a commit message.

### Window Tracking

`window_tracker.rs` enumerates all Alt+Tab-eligible windows (visible, non-tool, non-cloaked) each time the wheel opens. The first 8 fill fixed slots; any extras go into an `overflow` vector. Windows that close are removed; newly opened windows fill free slots. The tracker supports three swap operations: slot↔slot, slot↔overflow, and overflow↔overflow.

### Rendering

The UI is a transparent layered window that covers the entire virtual desktop. All drawing goes through a DIB section (GDI device-independent bitmap) backed by a Direct2D DC render target.

Each frame (`draw_frame` in `wheel_window.rs`) draws in order:

1. Main disc background
2. DWM thumbnail placeholder rectangles (8 slots)
3. Selection highlight wedge
4. Slot divider lines
5. Window icons (32×32 px, placed just outside the hub on the radial axis)
6. Window title text (single line, ellipsis-truncated to thumbnail width)
7. Overflow panel (if any overflow exists)

DWM thumbnails are registered separately via `DwmRegisterThumbnail` and positioned to match each slot's rectangle. They render behind the D2D overlay, which is why placeholder rectangles are left transparent so thumbnails show through.

The completed DIB is pushed to the screen with `UpdateLayeredWindow`, giving per-pixel alpha blending with no window chrome.

### Slot Geometry

`wheel_geometry.rs` contains pure, unit-tested math. Slot 0 is at 12 o'clock; the remaining 7 proceed clockwise at 45° each. Thumbnail rectangles are sized so their inner corners stay within their slice's dividers (width is solved from the chord at the inner edge of each thumbnail, not the centre).

`point_to_slot` maps a cursor coordinate to a slot index by polar angle. The inner hub returns `None` (dead zone); any distance beyond the hub — including outside the outer ring — returns the nearest sector.

### Hover Tracking

Because the layered window uses per-pixel alpha for hit-testing, `WM_MOUSEMOVE` is not delivered over the transparent areas outside the wheel disc. A 16 ms `WM_TIMER` fires while the wheel is open, reads `GetCursorPos`, and feeds the position into `on_mouse_move`, bypassing the hit-test limitation entirely.

### Drag Ghost

Dragging shows a small semi-transparent preview image that follows the cursor. This uses a separate topmost layered window (`ghost_hwnd`) backed by its own 240×100 DIB. On every `WM_MOUSEMOVE` during a drag, only the ghost is redrawn — the main wheel stays static — keeping the drag response at roughly 0.5 ms per frame.

## Code Layout

```
src/
  main.rs              Entry point, message loop, single-instance guard
  wheel_window.rs      All UI state, rendering, and input handling
  wheel_geometry.rs    Pure slot math (angle → slot index)
  window_tracker.rs    Window enumeration, slot and overflow management
  window_activator.rs  Foreground activation (attaches to target thread's input queue)
  keyboard_hook.rs     Global low-level keyboard hook for Alt+Tab interception
```

## Dependencies

[windows 0.58](https://crates.io/crates/windows) — Rust bindings for the Win32 APIs used:

| Feature area | Used for |
|---|---|
| Direct2D | Shape and text rendering |
| DirectWrite | Font layout, single-line text with ellipsis trimming |
| DWM | Live window thumbnails |
| GDI | DIB sections, DC management |
| Win32 UI / Shell | Window messages, tray icon, icon extraction |
| Win32 Input | Keyboard hook, mouse capture |
| Win32 Threading | Single-instance mutex, thread input attachment |
