# wheel_switcher

A Windows Alt+Tab replacement built in Rust. Instead of a flat taskbar strip, it shows a circular wheel of up to 8 live window thumbnails. Windows beyond 8 slots appear in an overflow panel to the left of the wheel.

## Features

- **Circular wheel** — up to 8 slots arranged radially, each showing a DWM live thumbnail
- **Overflow panel** — a scrollable sidebar for windows beyond the 8 slots
- **Drag-and-drop reordering** — drag any wheel slot to another slot, to the overflow panel, or drag any overflow row onto a wheel slot
- **Keyboard navigation** — Tab / Shift+Tab to cycle through slots; Esc to cancel; releasing Alt commits the selection
- **Hover selection** — hovering over a slot or overflow row highlights it for immediate commit on Alt release
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

`window_tracker.rs` enumerates all Alt+Tab-eligible windows (visible, non-tool, non-cloaked) each time the wheel opens. The first 8 go into fixed slots; any extras go into an `overflow` vector. Windows that close are removed; newly opened windows fill free slots. The tracker also caches titles and supports the three swap operations: slot↔slot, slot↔overflow, and overflow↔overflow.

### Rendering

The UI is a transparent, click-through layered window that covers the entire virtual desktop. All drawing goes through a DIB section (GDI device-independent bitmap) backed by a Direct2D DC render target.

Each frame (`draw_frame` in `wheel_window.rs`) draws in order:

1. Outer glow halo
2. Main disc background
3. DWM thumbnail placeholder rectangles (8 slots)
4. Selection highlight wedge
5. Slot divider lines
6. Central hub circle
7. Window icons (32×32 px, centered in the hub per slot)
8. Window title text
9. Overflow panel (if any overflow exists)

DWM thumbnails are registered separately via `DwmRegisterThumbnail` and positioned/sized to match each slot's rectangle. They render behind the D2D overlay at the system level, which is why the placeholder rectangles need to be punched out (left transparent) so thumbnails show through.

The completed DIB is pushed to the screen with `UpdateLayeredWindow`, giving per-pixel alpha blending with no window chrome.

### Drag Ghost

Dragging shows a small semi-transparent preview image that follows the cursor. This uses a separate topmost layered window (`ghost_hwnd`) backed by its own 240×100 DIB. On every `WM_MOUSEMOVE` during a drag, only the ghost is redrawn — the main wheel stays static — keeping the drag response at roughly 0.5 ms per frame.

### Slot Geometry

`wheel_geometry.rs` contains pure, unit-tested math. Slot 0 is at 12 o'clock and the remaining 7 proceed clockwise at 45° each. `point_to_slot` converts a cursor coordinate (relative to the wheel center) to a slot index by computing the polar angle and checking whether the radius falls between the inner hub and the outer edge.

## Code Layout

```
src/
  main.rs              Entry point, message loop, tray icon, single-instance guard
  wheel_window.rs      All UI state, rendering, and input handling
  wheel_geometry.rs    Pure slot math (angle/radius → slot index)
  window_tracker.rs    Window enumeration, slot and overflow management
  window_activator.rs  Foreground activation (attaches to target thread's input queue)
  keyboard_hook.rs     Global low-level keyboard hook for Alt+Tab interception
```

## Dependencies

[windows 0.58](https://crates.io/crates/windows) — Rust bindings for the Win32 APIs used:

| Feature area | Used for |
|---|---|
| Direct2D | Shape and text rendering |
| DirectWrite | Font layout and text formatting |
| DWM | Live window thumbnails |
| GDI | DIB sections, DC management |
| Win32 UI / Shell | Window messages, tray icon, icon extraction |
| Win32 Input | Keyboard hook, mouse capture |
| Win32 Threading | Single-instance mutex, thread input attachment |
