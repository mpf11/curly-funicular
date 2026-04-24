use std::collections::HashMap;
use std::ffi::c_void;
use std::mem::size_of;
use windows::core::{w, Interface, PCWSTR};
use windows::Win32::Foundation::{
    BOOL, COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM,
};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_FIGURE_BEGIN_FILLED,
    D2D1_FIGURE_END_CLOSED, D2D1_FILL_MODE_ALTERNATE, D2D1_GRADIENT_STOP, D2D1_PIXEL_FORMAT,
    D2D_POINT_2F, D2D_RECT_F, D2D_SIZE_F, D2D_SIZE_U,
};
use windows::Win32::Graphics::Direct2D::{
    D2D1CreateFactory, ID2D1Bitmap, ID2D1Brush, ID2D1DCRenderTarget, ID2D1Factory,
    ID2D1GradientStopCollection, ID2D1RadialGradientBrush, ID2D1RenderTarget, ID2D1StrokeStyle,
    D2D1_ARC_SEGMENT, D2D1_ARC_SIZE_SMALL, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
    D2D1_BITMAP_PROPERTIES, D2D1_DRAW_TEXT_OPTIONS_CLIP, D2D1_EXTEND_MODE_CLAMP,
    D2D1_FACTORY_TYPE_SINGLE_THREADED, D2D1_FEATURE_LEVEL_DEFAULT, D2D1_GAMMA_2_2,
    D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES,
    D2D1_RENDER_TARGET_PROPERTIES, D2D1_RENDER_TARGET_TYPE_DEFAULT,
    D2D1_ANTIALIAS_MODE_ALIASED, D2D1_RENDER_TARGET_USAGE_NONE, D2D1_ROUNDED_RECT, D2D1_SWEEP_DIRECTION_CLOCKWISE,
    D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
};
use windows::Win32::Graphics::DirectWrite::{
    DWriteCreateFactory, IDWriteFactory, IDWriteTextFormat,
    DWRITE_FACTORY_TYPE_SHARED, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL,
    DWRITE_FONT_WEIGHT_MEDIUM, DWRITE_PARAGRAPH_ALIGNMENT_CENTER,
    DWRITE_TEXT_ALIGNMENT_CENTER, DWRITE_TRIMMING, DWRITE_TRIMMING_GRANULARITY_CHARACTER,
    DWRITE_WORD_WRAPPING_NO_WRAP,
};
use windows::Win32::Graphics::Dwm::{
    DwmRegisterThumbnail, DwmUnregisterThumbnail, DwmUpdateThumbnailProperties,
    DWM_THUMBNAIL_PROPERTIES, DWM_TNP_OPACITY, DWM_TNP_RECTDESTINATION,
    DWM_TNP_SOURCECLIENTAREAONLY, DWM_TNP_VISIBLE,
};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Gdi::{
    BITMAPINFO, BITMAPINFOHEADER, BI_RGB, BLENDFUNCTION, HBITMAP, HBRUSH, HDC, HGDIOBJ,
    AC_SRC_ALPHA, AC_SRC_OVER, CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject,
    DIB_RGB_COLORS, GetDC, GetMonitorInfoW, MonitorFromPoint, ReleaseDC, SelectObject,
    MONITORINFO, MONITOR_DEFAULTTOPRIMARY,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture};
use windows::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFYICONDATAW, Shell_NotifyIconW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DestroyWindow,
    DrawIconEx, GetClassLongPtrW, GetCursorPos, GetSystemMetrics, GetWindowLongPtrW,
    KillTimer, LoadCursorW, LoadIconW, MSG, PeekMessageW, PostQuitMessage, RegisterClassExW,
    SendMessageW, SetCursorPos, SetForegroundWindow, SetTimer, SetWindowLongPtrW, SetWindowPos,
    ShowWindow, TrackPopupMenu, UpdateLayeredWindow, CREATESTRUCTW, DI_NORMAL, GCL_HICON, WS_EX_TOPMOST,
    GWLP_USERDATA, GWL_EXSTYLE, HICON, HWND_TOPMOST, ICON_BIG, ICON_SMALL2, IDC_ARROW,
    IDI_APPLICATION, MF_STRING, PM_NOREMOVE, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN,
    SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
    SW_SHOWNOACTIVATE, TPM_RETURNCMD, TPM_RIGHTBUTTON, ULW_ALPHA, WM_CONTEXTMENU, WM_GETICON,
    WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_NCDESTROY, WM_RBUTTONUP,
    WM_TIMER, WNDCLASSEXW, WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TRANSPARENT,
    WS_POPUP,
};

use crate::keyboard_hook::{
    MSG_ALT_RELEASED, MSG_ALT_SHIFT_TAB, MSG_ALT_TAB, MSG_ESCAPE, MSG_TRAY, WHEEL_ACTIVE,
};
use crate::wheel_geometry::{self, SLOT_COUNT};
use crate::window_activator;
use crate::window_tracker::{WindowTracker, MAX_SLOTS};

const ICON_SIZE: u32 = 32;
const OVERFLOW_ROW_H: f32 = 46.0;
const OVERFLOW_PANEL_W: f32 = 220.0;
const OVERFLOW_GAP: f32 = 32.0;
const OVERFLOW_SCROLLBAR_W: f32 = 5.0;
const TRAY_ID: u32 = 1;

// Ghost overlay DIB dimensions — large enough for both drag ghost shapes.
// Keeping it small makes clear+blit ~0.1 ms instead of ~5 ms for a full-screen DIB.
const GHOST_DIB_W: i32 = 240; // covers 220 (overflow panel) + margin
const GHOST_DIB_H: i32 = 100; // covers 90 (slot ghost) + margin

// ---- Small geometry / colour helpers ----

fn rgba(r: f32, g: f32, b: f32, a: f32) -> D2D1_COLOR_F {
    D2D1_COLOR_F { r, g, b, a }
}

fn pt(x: f32, y: f32) -> D2D_POINT_2F {
    D2D_POINT_2F { x, y }
}

fn frect(l: f32, t: f32, r: f32, b: f32) -> D2D_RECT_F {
    D2D_RECT_F { left: l, top: t, right: r, bottom: b }
}

// ---- Per-slot snapshot used for one draw call ----

struct SlotSnapshot {
    hwnd: HWND,
    title: String,
    thumb_rect: D2D_RECT_F,
}

// ---- Main state ----

pub struct WheelState {
    d2d_factory: ID2D1Factory,
    dwrite_factory: IDWriteFactory,
    render_target: Option<ID2D1DCRenderTarget>,
    text_format: Option<IDWriteTextFormat>,
    text_format_sm: Option<IDWriteTextFormat>,
    // Keyed by HWND as usize
    icon_cache: HashMap<usize, Option<ID2D1Bitmap>>,

    mem_dc: HDC,
    dib: HBITMAP,
    dib_bits: *mut c_void,

    // Separate topmost overlay window for the drag ghost so it renders above DWM thumbnails.
    ghost_hwnd: HWND,
    ghost_mem_dc: HDC,
    ghost_dib: HBITMAP,
    ghost_dib_bits: *mut c_void,

    pub virt_x: i32,
    pub virt_y: i32,
    pub virt_w: i32,
    pub virt_h: i32,

    cx: f32,
    cy: f32,
    inner_r: f32,
    outer_r: f32,
    thumb_rects: [Option<D2D_RECT_F>; MAX_SLOTS],

    pub tracker: WindowTracker,
    // DWM thumbnail handles stored as isize (windows-rs 0.58 returns isize from DwmRegisterThumbnail)
    thumbs: [isize; MAX_SLOTS],

    pub wheel_open: bool,
    hover_slot: i32,
    default_slot: i32,
    drag_start_slot: i32,
    drag_start_overflow: i32,
    drag_start_x: f32,
    drag_start_y: f32,
    drag_cur_x: f32,
    drag_cur_y: f32,
    is_dragging: bool,

    // Premultiplied BGRA pixel cache for icon rendering in the drag ghost.
    icon_pixel_cache: HashMap<usize, Vec<u8>>,

    overflow_panel_rect: Option<D2D_RECT_F>,
    overflow_hover_idx: i32,
    overflow_scroll: usize,
}

impl WheelState {
    // ---- Overflow helpers ----

    /// Number of overflow rows that fit on screen (1..=overflow.len()).
    fn ov_visible_count(&self) -> usize {
        let max = ((self.virt_h as f32 - 8.0) / OVERFLOW_ROW_H).floor() as usize;
        self.tracker.overflow().len().min(max.max(1))
    }

    // ---- D2D resource management ----

    fn ensure_render_target(&mut self) -> windows::core::Result<()> {
        if self.render_target.is_some() {
            return Ok(());
        }
        let rt_props = D2D1_RENDER_TARGET_PROPERTIES {
            r#type: D2D1_RENDER_TARGET_TYPE_DEFAULT,
            pixelFormat: D2D1_PIXEL_FORMAT {
                format: DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX: 0.0,
            dpiY: 0.0,
            usage: D2D1_RENDER_TARGET_USAGE_NONE,
            minLevel: D2D1_FEATURE_LEVEL_DEFAULT,
        };
        let rt = unsafe { self.d2d_factory.CreateDCRenderTarget(&rt_props)? };
        let bind_rect = RECT { left: 0, top: 0, right: self.virt_w, bottom: self.virt_h };
        unsafe { rt.BindDC(self.mem_dc, &bind_rect)? };
        self.render_target = Some(rt);
        self.icon_cache.clear();
        self.icon_pixel_cache.clear();

        let tf = unsafe {
            let t = self.dwrite_factory.CreateTextFormat(
                w!("Segoe UI"), None, DWRITE_FONT_WEIGHT_MEDIUM,
                DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 13.0, w!("en-us"),
            )?;
            t.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER)?;
            t.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER)?;
            t.SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP)?;
            t
        };
        let tf_sm = unsafe {
            let t = self.dwrite_factory.CreateTextFormat(
                w!("Segoe UI"), None, DWRITE_FONT_WEIGHT_MEDIUM,
                DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 11.0, w!("en-us"),
            )?;
            t.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER)?;
            t.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER)?;
            t.SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP)?;
            t
        };
        self.text_format = Some(tf);
        self.text_format_sm = Some(tf_sm);
        Ok(())
    }

    // ---- Geometry ----

    fn compute_geometry(&mut self) {
        unsafe {
            let mut cursor = POINT::default();
            let _ = GetCursorPos(&mut cursor);
            let hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);
            let mut mi = MONITORINFO {
                cbSize: size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            let _ = GetMonitorInfoW(hmon, &mut mi);
            let b = &mi.rcMonitor;
            let mon_w = (b.right - b.left) as f32;
            let mon_h = (b.bottom - b.top) as f32;
            self.cx = (b.left + (b.right - b.left) / 2) as f32 - self.virt_x as f32;
            self.cy = (b.top + (b.bottom - b.top) / 2) as f32 - self.virt_y as f32;
            self.outer_r = mon_w.min(mon_h) * 0.40;
            self.inner_r = self.outer_r * 0.18;
        }
        self.compute_thumb_rects();
    }

    fn compute_thumb_rects(&mut self) {
        let (cx, cy, outer_r) = (self.cx, self.cy, self.outer_r);
        for i in 0..MAX_SLOTS {
            let center_rad = wheel_geometry::slice_center_angle_deg(i).to_radians() as f32;
            let thumb_r = outer_r * 0.68;
            let display_rad = if i % 2 == 1 {
                let sign = if i == 1 || i == 5 { 1.0f32 } else { -1.0 };
                center_rad + sign * 3.0f32.to_radians()
            } else {
                center_rad
            };
            let tcx = cx + display_rad.cos() * thumb_r;
            let tcy = cy + display_rad.sin() * thumb_r;
            let half_span = (wheel_geometry::SLICE_SPAN_DEG / 2.0).to_radians() as f32;
            let radial_depth = outer_r * 0.58;
            // Width is constrained by the chord at the thumbnail's inner edge (not its centre),
            // so the inner corners don't cross the slice dividers.
            // With h = w*9/16 and inner_edge = thumb_r - h/2, solving
            // w = 0.92 * 2*(thumb_r - h/2)*sin(half_span) yields this closed form:
            let k = 0.92 * 2.0 * half_span.sin();
            let mut w = (k * thumb_r / (1.0 + k * 9.0 / 32.0)).min(radial_depth * 16.0 / 9.0);
            let mut h = w * 9.0 / 16.0;
            if h > radial_depth {
                h = radial_depth;
                w = h * 16.0 / 9.0;
            }
            self.thumb_rects[i] = Some(frect(tcx - w / 2.0, tcy - h / 2.0, tcx + w / 2.0, tcy + h / 2.0));
        }
    }

    // ---- DWM thumbnails ----

    fn register_thumbnails(&mut self, hwnd: HWND) {
        for i in 0..MAX_SLOTS {
            let src_hwnd = match self.tracker.slots()[i].as_ref() {
                Some(w) => w.hwnd,
                None => continue,
            };
            let rect = match self.thumb_rects[i] {
                Some(r) => r,
                None => continue,
            };
            let thumb = match unsafe { DwmRegisterThumbnail(hwnd, src_hwnd) } {
                Ok(h) => h,
                Err(_) => continue,
            };
            self.thumbs[i] = thumb;
            let props = DWM_THUMBNAIL_PROPERTIES {
                dwFlags: DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY
                    | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination: RECT {
                    left: (rect.left + 4.0) as i32,
                    top: (rect.top + 4.0) as i32,
                    right: (rect.right - 4.0) as i32,
                    bottom: (rect.bottom - 4.0) as i32,
                },
                rcSource: RECT::default(),
                opacity: 235,
                fVisible: BOOL(0),
                fSourceClientAreaOnly: BOOL(0),
            };
            unsafe { let _ = DwmUpdateThumbnailProperties(thumb, &props); }
        }
    }

    fn show_thumbnails(&self) {
        for &thumb in &self.thumbs {
            if thumb == 0 { continue; }
            let props = DWM_THUMBNAIL_PROPERTIES {
                dwFlags: DWM_TNP_VISIBLE,
                fVisible: BOOL(1),
                ..Default::default()
            };
            unsafe { let _ = DwmUpdateThumbnailProperties(thumb, &props); }
        }
    }

    fn clear_thumbnails(&mut self) {
        for thumb in &mut self.thumbs {
            if *thumb != 0 {
                unsafe { let _ = DwmUnregisterThumbnail(*thumb); }
                *thumb = 0;
            }
        }
    }

    fn re_register_thumbnail(&mut self, hwnd: HWND, slot: usize) {
        if self.thumbs[slot] != 0 {
            unsafe { let _ = DwmUnregisterThumbnail(self.thumbs[slot]); }
            self.thumbs[slot] = 0;
        }
        let src_hwnd = match self.tracker.slots()[slot].as_ref() {
            Some(w) => w.hwnd,
            None => return,
        };
        let rect = match self.thumb_rects[slot] {
            Some(r) => r,
            None => return,
        };
        let thumb = match unsafe { DwmRegisterThumbnail(hwnd, src_hwnd) } {
            Ok(h) => h,
            Err(_) => return,
        };
        self.thumbs[slot] = thumb;
        let props = DWM_THUMBNAIL_PROPERTIES {
            dwFlags: DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY
                | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination: RECT {
                left: (rect.left + 2.0) as i32,
                top: (rect.top + 2.0) as i32,
                right: (rect.right - 2.0) as i32,
                bottom: (rect.bottom - 2.0) as i32,
            },
            rcSource: RECT::default(),
            opacity: 235,
            fVisible: BOOL(1),
            fSourceClientAreaOnly: BOOL(0),
        };
        unsafe { let _ = DwmUpdateThumbnailProperties(thumb, &props); }
    }

    // ---- Present / Dismiss ----

    pub fn present(&mut self, hwnd: HWND) {
        let _ = self.ensure_render_target();
        self.tracker.refresh();
        self.compute_geometry();

        unsafe {
            let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            SetWindowLongPtrW(
                hwnd,
                GWL_EXSTYLE,
                ex & !(WS_EX_TRANSPARENT.0 as isize),
            );
            let _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            let _ = SetCursorPos(
                self.cx as i32 + self.virt_x,
                self.cy as i32 + self.virt_y,
            );
        }

        self.clear_thumbnails();
        self.register_thumbnails(hwnd);
        self.wheel_open = true;
        WHEEL_ACTIVE.store(true, std::sync::atomic::Ordering::Relaxed);
        unsafe { SetTimer(hwnd, 1, 16, None); }

        let prev = self.tracker.previous_hwnd;
        self.pre_select_handle(prev);
        let _ = self.draw_frame(hwnd);
        self.show_thumbnails();
    }

    pub fn dismiss(&mut self, hwnd: HWND, switch_to: Option<HWND>) {
        self.clear_thumbnails();
        self.wheel_open = false;
        WHEEL_ACTIVE.store(false, std::sync::atomic::Ordering::Relaxed);

        self.hover_slot = -1;
        self.default_slot = -1;
        if self.is_dragging {
            unsafe { let _ = ReleaseCapture(); }
        }
        self.is_dragging = false;
        self.drag_start_slot = -1;
        self.drag_start_overflow = -1;
        self.overflow_hover_idx = -1;
        self.overflow_scroll = 0;
        self.overflow_panel_rect = None;

        unsafe {
            let _ = KillTimer(hwnd, 1);
            let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            SetWindowLongPtrW(
                hwnd,
                GWL_EXSTYLE,
                ex | WS_EX_TRANSPARENT.0 as isize,
            );
        }
        self.paint_transparent(hwnd);
        self.clear_ghost();

        if let Some(target) = switch_to {
            window_activator::activate(target);
        }
    }

    pub fn commit(&mut self, hwnd: HWND) {
        // Overflow hover takes priority over the main-circle selection.
        if self.overflow_hover_idx >= 0 {
            let idx = self.overflow_hover_idx as usize;
            if idx < self.tracker.overflow().len() {
                let target = self.tracker.overflow()[idx].hwnd;
                self.dismiss(hwnd, Some(target));
                return;
            }
        }

        let slot = if self.hover_slot >= 0 {
            self.hover_slot as usize
        } else if self.default_slot >= 0 {
            self.default_slot as usize
        } else {
            // No selection. dismiss() won't activate, so the hook-swallowed Alt-up
            // must be replayed here or the system will believe Alt is still held.
            self.dismiss(hwnd, None);
            window_activator::replay_alt_up();
            return;
        };
        let target = self.tracker.slots()[slot].as_ref().map(|w| w.hwnd);
        if target.is_none() {
            self.dismiss(hwnd, None);
            window_activator::replay_alt_up();
            return;
        }
        self.dismiss(hwnd, target);
    }

    pub fn advance_selection(&mut self, hwnd: HWND, dir: i32) {
        let start = if self.default_slot < 0 { 0 } else { self.default_slot as usize };
        for step in 1..=SLOT_COUNT {
            let next = ((start as i32 + dir * step as i32).rem_euclid(SLOT_COUNT as i32)) as usize;
            if self.tracker.slots()[next].is_some() {
                self.default_slot = next as i32;
                let _ = self.draw_frame(hwnd);
                return;
            }
        }
    }

    fn pre_select_handle(&mut self, handle: HWND) {
        if handle.0.is_null() {
            self.advance_selection(HWND(std::ptr::null_mut()), 1);
            return;
        }
        for i in 0..MAX_SLOTS {
            if self.tracker.slots()[i].as_ref().map(|w| w.hwnd) == Some(handle) {
                self.default_slot = i as i32;
                return;
            }
        }
        for (i, ow) in self.tracker.overflow().iter().enumerate() {
            if ow.hwnd == handle {
                self.overflow_hover_idx = i as i32;
                return;
            }
        }
        self.advance_selection(HWND(std::ptr::null_mut()), 1);
    }

    // ---- Rendering ----

    fn paint_transparent(&self, hwnd: HWND) {
        unsafe {
            std::ptr::write_bytes(self.dib_bits as *mut u8, 0, (self.virt_w * self.virt_h * 4) as usize);
            let sdc = GetDC(None);
            let blend = BLENDFUNCTION {
                BlendOp: AC_SRC_OVER as u8,
                BlendFlags: 0,
                SourceConstantAlpha: 255,
                AlphaFormat: AC_SRC_ALPHA as u8,
            };
            let _ = UpdateLayeredWindow(
                hwnd, sdc,
                Some(&POINT { x: self.virt_x, y: self.virt_y }),
                Some(&SIZE { cx: self.virt_w, cy: self.virt_h }),
                self.mem_dc,
                Some(&POINT { x: 0, y: 0 }),
                COLORREF(0), Some(&blend), ULW_ALPHA,
            );
            ReleaseDC(None, sdc);
        }
    }

    fn draw_frame(&mut self, hwnd: HWND) -> windows::core::Result<()> {
        // BindDC must be called before every BeginDraw (D2D spec requirement).
        if let Some(ref dc_rt) = self.render_target {
            let bind_rect = RECT { left: 0, top: 0, right: self.virt_w, bottom: self.virt_h };
            unsafe { dc_rt.BindDC(self.mem_dc, &bind_rect)?; }
        }

        // Cast to the base render-target interface so all drawing methods are accessible.
        let rt: ID2D1RenderTarget = match &self.render_target {
            Some(r) => r.cast()?,
            None => return Ok(()),
        };

        // Snapshot slot data up front to avoid borrow conflicts with icon_cache mutations.
        let active_slot = if self.overflow_hover_idx >= 0 {
            -1
        } else if self.hover_slot >= 0 {
            self.hover_slot
        } else {
            self.default_slot
        };
        let slots: Vec<Option<SlotSnapshot>> = (0..MAX_SLOTS)
            .map(|i| {
                self.tracker.slots()[i].as_ref().and_then(|w| {
                    self.thumb_rects[i].map(|tr| SlotSnapshot {
                        hwnd: w.hwnd,
                        title: w.title.clone(),
                        thumb_rect: tr,
                    })
                })
            })
            .collect();

        // Load icons (may update self.icon_cache).
        let icons: Vec<Option<ID2D1Bitmap>> = slots.iter()
            .map(|s| s.as_ref().and_then(|info| unsafe { self.load_icon_cached(&rt, info.hwnd) }))
            .collect();

        // Overflow snapshot.
        let overflow: Vec<(HWND, String)> = self.tracker.overflow().iter()
            .map(|w| (w.hwnd, w.title.clone()))
            .collect();
        let overflow_icons: Vec<Option<ID2D1Bitmap>> = overflow.iter()
            .map(|(h, _)| unsafe { self.load_icon_cached(&rt, *h) })
            .collect();

        // Compute how many rows fit and clamp the scroll offset.
        let ov_visible = self.ov_visible_count();
        let ov_max_scroll = overflow.len().saturating_sub(ov_visible);
        if self.overflow_scroll > ov_max_scroll { self.overflow_scroll = ov_max_scroll; }
        let ov_scroll = self.overflow_scroll;
        let ov_needs_scrollbar = overflow.len() > ov_visible;

        // Compute overflow panel rect before D2D calls (height capped to visible rows).
        let ov_rect = if overflow.is_empty() {
            None
        } else {
            let total_h = ov_visible as f32 * OVERFLOW_ROW_H;
            let px = self.cx - self.outer_r - OVERFLOW_GAP - OVERFLOW_PANEL_W;
            let py = (self.cy - total_h / 2.0)
                .max(4.0)
                .min(self.virt_h as f32 - total_h - 4.0);
            Some(frect(px, py, px + OVERFLOW_PANEL_W, py + total_h))
        };
        self.overflow_panel_rect = ov_rect;

        // Capture locals for the unsafe D2D block.
        let (cx, cy, inner_r, outer_r) = (self.cx, self.cy, self.inner_r, self.outer_r);
        let hover_ov = self.overflow_hover_idx;
        let tf = self.text_format.clone();
        let tf_sm = self.text_format_sm.clone();
        let dwrite = self.dwrite_factory.clone();

        unsafe {
            rt.BeginDraw();
            rt.Clear(Some(&rgba(0.0, 0.0, 0.0, 0.0)));


            // 2. Main disc — donut shape (center is transparent).
            let disc = rt.CreateSolidColorBrush(&rgba(0.055, 0.063, 0.086, 0.87), None)?;
            if let Ok(donut) = build_donut_geo(&self.d2d_factory, cx, cy, inner_r, outer_r) {
                rt.FillGeometry(&donut, &*disc, None::<&ID2D1Brush>);
            }

            // 3. Thumbnail placeholder rectangles.
            let thumb_rim = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.27), None)?;
            for i in 0..MAX_SLOTS {
                let opacity = if slots[i].is_some() { 0.33f32 } else { 0.12f32 };
                let thumb_bg = rt.CreateSolidColorBrush(&rgba(0.04, 0.051, 0.071, opacity), None)?;
                if let Some(tr) = self.thumb_rects[i] {
                    // Expand placeholder outward by 2px to give visible gap between hard-cornered thumbnail and rounded border.
                    let expanded = frect(tr.left - 2.0, tr.top - 2.0, tr.right + 2.0, tr.bottom + 2.0);
                    let rr = D2D1_ROUNDED_RECT { rect: expanded, radiusX: 10.0, radiusY: 10.0 };
                    rt.FillRoundedRectangle(&rr, &*thumb_bg);
                    rt.DrawRoundedRectangle(&rr, &*thumb_rim, 1.0, None::<&ID2D1StrokeStyle>);
                }
            }

            // 4. Selection highlight — radial gradient fading outward.
            if active_slot >= 0 {
                let stops = [
                    D2D1_GRADIENT_STOP { position: 0.0, color: rgba(1.0, 1.0, 1.0, 0.28) },
                    D2D1_GRADIENT_STOP { position: 1.0, color: rgba(1.0, 1.0, 1.0, 0.0) },
                ];
                let stop_coll: ID2D1GradientStopCollection =
                    rt.CreateGradientStopCollection(&stops, D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP)?;
                let grad_props = D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES {
                    center: pt(cx, cy),
                    gradientOriginOffset: pt(0.0, 0.0),
                    radiusX: outer_r,
                    radiusY: outer_r,
                };
                let hl: ID2D1RadialGradientBrush =
                    rt.CreateRadialGradientBrush(&grad_props, None, &stop_coll)?;
                if let Ok(geo) = build_slice_geo(&self.d2d_factory, cx, cy, inner_r, outer_r, active_slot as usize) {
                    rt.FillGeometry(&geo, &hl.cast::<ID2D1Brush>()?, None::<&ID2D1Brush>);
                }
            }

            // 5. Divider lines — inset 15% from each edge of the ring.
            let ring = outer_r - inner_r;
            let spoke_inner = inner_r + ring * 0.15;
            let spoke_outer = outer_r - ring * 0.15;
            let div = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.34), None)?;
            for i in 0..SLOT_COUNT {
                let a = wheel_geometry::slice_boundary_angle_deg(i).to_radians() as f32;
                rt.DrawLine(
                    pt(cx + a.cos() * spoke_inner, cy + a.sin() * spoke_inner),
                    pt(cx + a.cos() * spoke_outer, cy + a.sin() * spoke_outer),
                    &*div, 1.0, None::<&ID2D1StrokeStyle>,
                );
            }

            // 7. Icons and titles.
            let text_brush = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.87), None)?;
            for i in 0..MAX_SLOTS {
                let info = match &slots[i] { Some(s) => s, None => continue };
                let center_rad = wheel_geometry::slice_center_angle_deg(i).to_radians() as f32;
                let icon_r = inner_r + ICON_SIZE as f32 * 1.1;
                let ix = cx + center_rad.cos() * icon_r;
                let iy = cy + center_rad.sin() * icon_r;
                let half = ICON_SIZE as f32 / 2.0;

                if let Some(bmp) = &icons[i] {
                    let dest = frect(ix - half, iy - half, ix + half, iy + half);
                    rt.DrawBitmap(
                        bmp,
                        Some(&dest as *const D2D_RECT_F),
                        1.0,
                        D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                        None,
                    );
                }

                if let Some(ref tf_fmt) = tf {
                    let tr = info.thumb_rect;
                    let tw = tr.right - tr.left;
                    let tcx = (tr.left + tr.right) / 2.0;
                    let text_above = i == 0 || i == 1 || i == 7;
                    let ty = if text_above { tr.top - 28.0 } else { tr.bottom + 6.0 };
                    let wide: Vec<u16> = info.title.encode_utf16().collect();
                    if let Ok(layout) = dwrite.CreateTextLayout(&wide, tf_fmt, tw, 22.0) {
                        let trimming = DWRITE_TRIMMING {
                            granularity: DWRITE_TRIMMING_GRANULARITY_CHARACTER,
                            delimiter: 0, delimiterCount: 0,
                        };
                        if let Ok(sign) = dwrite.CreateEllipsisTrimmingSign(tf_fmt) {
                            let _ = layout.SetTrimming(&trimming, &sign);
                        }
                        rt.DrawTextLayout(
                            pt(tcx - tw / 2.0, ty), &layout,
                            &*text_brush, D2D1_DRAW_TEXT_OPTIONS_CLIP,
                        );
                    }
                }
            }

            // 8. Overflow panel.
            if let Some(pr) = ov_rect {
                let bg = rt.CreateSolidColorBrush(&rgba(0.055, 0.063, 0.086, 0.87), None)?;
                let pr_rim = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.33), None)?;
                rt.FillRoundedRectangle(
                    &D2D1_ROUNDED_RECT { rect: pr, radiusX: 12.0, radiusY: 12.0 },
                    &*bg,
                );
                rt.DrawRoundedRectangle(
                    &D2D1_ROUNDED_RECT { rect: pr, radiusX: 12.0, radiusY: 12.0 },
                    &*pr_rim, 1.0, None::<&ID2D1StrokeStyle>,
                );

                rt.PushAxisAlignedClip(&pr, D2D1_ANTIALIAS_MODE_ALIASED);
                let row_hl = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.16), None)?;
                let text_right_margin = if ov_needs_scrollbar { OVERFLOW_SCROLLBAR_W + 6.0 } else { 8.0 };
                for vi in 0..ov_visible {
                    let actual_idx = ov_scroll + vi;
                    let (_, title) = &overflow[actual_idx];
                    let row = frect(
                        pr.left, pr.top + vi as f32 * OVERFLOW_ROW_H,
                        pr.right, pr.top + (vi + 1) as f32 * OVERFLOW_ROW_H,
                    );
                    if hover_ov == actual_idx as i32 {
                        let cr = if ov_visible == 1 || vi == 0 || vi == ov_visible - 1 { 10.0f32 } else { 2.0 };
                        rt.FillRoundedRectangle(
                            &D2D1_ROUNDED_RECT { rect: row, radiusX: cr, radiusY: cr },
                            &*row_hl,
                        );
                    }
                    if let Some(bmp) = &overflow_icons[actual_idx] {
                        let iy2 = (row.top + row.bottom) / 2.0;
                        let dest = frect(pr.left + 8.0, iy2 - 14.0, pr.left + 36.0, iy2 + 14.0);
                        rt.DrawBitmap(
                            bmp,
                            Some(&dest as *const D2D_RECT_F),
                            1.0,
                            D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                            None,
                        );
                    }
                    if let Some(ref tf_fmt) = tf_sm {
                        let text_x = pr.left + 42.0;
                        let text_w = pr.right - text_right_margin - text_x;
                        let wide: Vec<u16> = title.encode_utf16().collect();
                        if let Ok(layout) = dwrite.CreateTextLayout(&wide, tf_fmt, text_w, OVERFLOW_ROW_H) {
                            let trimming = DWRITE_TRIMMING {
                                granularity: DWRITE_TRIMMING_GRANULARITY_CHARACTER,
                                delimiter: 0, delimiterCount: 0,
                            };
                            if let Ok(sign) = dwrite.CreateEllipsisTrimmingSign(tf_fmt) {
                                let _ = layout.SetTrimming(&trimming, &sign);
                            }
                            rt.DrawTextLayout(
                                pt(text_x, row.top), &layout,
                                &*text_brush, D2D1_DRAW_TEXT_OPTIONS_CLIP,
                            );
                        }
                    }
                }
                rt.PopAxisAlignedClip();

                if ov_needs_scrollbar {
                    let track_top = pr.top + 4.0;
                    let track_bottom = pr.bottom - 4.0;
                    let track_h = track_bottom - track_top;
                    let total_count = overflow.len();
                    let thumb_frac = ov_visible as f32 / total_count as f32;
                    let thumb_h = (thumb_frac * track_h).max(20.0);
                    let max_scroll_f = (total_count - ov_visible) as f32;
                    let thumb_top = track_top + (ov_scroll as f32 / max_scroll_f) * (track_h - thumb_h);
                    let sb_right = pr.right - 3.0;
                    let sb_left = sb_right - OVERFLOW_SCROLLBAR_W;
                    let track_brush = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.1), None)?;
                    let thumb_brush = rt.CreateSolidColorBrush(&rgba(1.0, 1.0, 1.0, 0.4), None)?;
                    rt.FillRoundedRectangle(
                        &D2D1_ROUNDED_RECT {
                            rect: frect(sb_left, track_top, sb_right, track_bottom),
                            radiusX: OVERFLOW_SCROLLBAR_W / 2.0, radiusY: OVERFLOW_SCROLLBAR_W / 2.0,
                        },
                        &*track_brush,
                    );
                    rt.FillRoundedRectangle(
                        &D2D1_ROUNDED_RECT {
                            rect: frect(sb_left, thumb_top, sb_right, thumb_top + thumb_h),
                            radiusX: OVERFLOW_SCROLLBAR_W / 2.0, radiusY: OVERFLOW_SCROLLBAR_W / 2.0,
                        },
                        &*thumb_brush,
                    );
                }
            }

            rt.EndDraw(None, None)?;
        }

        // Push the updated frame to the layered window.
        unsafe {
            let sdc = GetDC(None);
            let blend = BLENDFUNCTION {
                BlendOp: AC_SRC_OVER as u8,
                BlendFlags: 0,
                SourceConstantAlpha: 255,
                AlphaFormat: AC_SRC_ALPHA as u8,
            };
            let _ = UpdateLayeredWindow(
                hwnd, sdc,
                Some(&POINT { x: self.virt_x, y: self.virt_y }),
                Some(&SIZE { cx: self.virt_w, cy: self.virt_h }),
                self.mem_dc,
                Some(&POINT { x: 0, y: 0 }),
                COLORREF(0), Some(&blend), ULW_ALPHA,
            );
            ReleaseDC(None, sdc);
        }
        Ok(())
    }

    // ---- Icon loading (cached by HWND as usize) ----

    unsafe fn load_icon_cached(
        &mut self,
        rt: &ID2D1RenderTarget,
        hwnd: HWND,
    ) -> Option<ID2D1Bitmap> {
        let key = hwnd.0 as usize;
        if let Some(cached) = self.icon_cache.get(&key) {
            return cached.clone();
        }
        let hicon = get_window_icon(hwnd);
        let (bmp, pixels) = if let Some(ic) = hicon {
            hicon_to_d2d_bitmap(rt, ic)
        } else {
            (None, Vec::new())
        };
        if !pixels.is_empty() {
            self.icon_pixel_cache.insert(key, pixels);
        }
        self.icon_cache.insert(key, bmp.clone());
        bmp
    }

    // ---- Mouse interaction ----

    pub fn on_mouse_move(&mut self, hwnd: HWND, x: f32, y: f32) {
        if let Some(pr) = self.overflow_panel_rect {
            if x >= pr.left && x < pr.right && y >= pr.top && y < pr.bottom {
                let ov_visible = self.ov_visible_count();
                let vi = ((y - pr.top) / OVERFLOW_ROW_H) as usize;
                let new_idx = if vi < ov_visible {
                    (self.overflow_scroll + vi) as i32
                } else {
                    -1
                };
                if new_idx != self.overflow_hover_idx {
                    self.overflow_hover_idx = new_idx;
                    self.hover_slot = -1;
                    let _ = self.draw_frame(hwnd);
                }
                return;
            }
        }

        let new_slot = wheel_geometry::point_to_slot(
            (x - self.cx) as f64, (y - self.cy) as f64,
            self.inner_r as f64, self.outer_r as f64, false,
        ).map(|s| s as i32).unwrap_or(-1);

        if new_slot != self.hover_slot {
            self.hover_slot = new_slot;
            self.overflow_hover_idx = -1;
            let _ = self.draw_frame(hwnd);
        }
    }

    pub fn on_mouse_down(&mut self, hwnd: HWND, x: f32, y: f32) {
        let mut drag_started = false;

        if let Some(pr) = self.overflow_panel_rect {
            if x >= pr.left && x < pr.right && y >= pr.top && y < pr.bottom {
                let ov_visible = self.ov_visible_count();
                let vi = ((y - pr.top) / OVERFLOW_ROW_H) as usize;
                let actual_idx = (self.overflow_scroll + vi) as i32;
                if vi < ov_visible && actual_idx < self.tracker.overflow().len() as i32 {
                    self.drag_start_overflow = actual_idx;
                    self.drag_start_slot = -1;
                    self.drag_start_x = x;
                    self.drag_start_y = y;
                    self.drag_cur_x = x;
                    self.drag_cur_y = y;
                    self.is_dragging = true;
                    drag_started = true;
                }
            }
        }

        if !drag_started {
            if let Some(s) = wheel_geometry::point_to_slot(
                (x - self.cx) as f64, (y - self.cy) as f64,
                self.inner_r as f64, self.outer_r as f64, true,
            ) {
                self.drag_start_slot = s as i32;
                self.drag_start_overflow = -1;
                self.drag_start_x = x;
                self.drag_start_y = y;
                self.drag_cur_x = x;
                self.drag_cur_y = y;
                self.is_dragging = true;
                drag_started = true;
            }
        }

        if drag_started {
            // Capture all mouse input so WM_MOUSEMOVE arrives even over transparent pixels.
            unsafe { SetCapture(hwnd); }
            // Raise ghost overlay above everything (above main wheel + its DWM thumbnails).
            unsafe {
                let _ = SetWindowPos(self.ghost_hwnd, HWND_TOPMOST,
                    0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            self.draw_drag_ghost();
        }
    }

    pub fn on_mouse_up(&mut self, hwnd: HWND, x: f32, y: f32) {
        if !self.is_dragging { return; }
        unsafe { let _ = ReleaseCapture(); }
        self.is_dragging = false;
        let moved = {
            let dx = x - self.drag_start_x;
            let dy = y - self.drag_start_y;
            (dx * dx + dy * dy).sqrt() > 6.0
        };

        let mut dismissed = false;

        if self.drag_start_slot >= 0 {
            let start = self.drag_start_slot as usize;
            self.drag_start_slot = -1;
            if moved {
                if let Some(drop) = wheel_geometry::point_to_slot(
                    (x - self.cx) as f64, (y - self.cy) as f64,
                    self.inner_r as f64, self.outer_r as f64, true,
                ) {
                    if drop != start {
                        self.tracker.swap_slots(start, drop);
                        self.re_register_thumbnail(hwnd, start);
                        self.re_register_thumbnail(hwnd, drop);
                    }
                } else if let Some(pr) = self.overflow_panel_rect {
                    if x >= pr.left && x < pr.right && y >= pr.top && y < pr.bottom {
                        let ov_visible = self.ov_visible_count();
                        let vi = ((y - pr.top) / OVERFLOW_ROW_H) as usize;
                        let drop_ov = self.overflow_scroll + vi;
                        if vi < ov_visible && drop_ov < self.tracker.overflow().len() {
                            self.tracker.swap_slot_with_overflow(start, drop_ov);
                            self.re_register_thumbnail(hwnd, start);
                        }
                    }
                }
            } else {
                let target = self.tracker.slots()[start].as_ref().map(|w| w.hwnd);
                if target.is_some() {
                    self.dismiss(hwnd, target);
                    dismissed = true;
                }
            }
        } else if self.drag_start_overflow >= 0 {
            let start_ov = self.drag_start_overflow as usize;
            self.drag_start_overflow = -1;
            if moved {
                if let Some(drop) = wheel_geometry::point_to_slot(
                    (x - self.cx) as f64, (y - self.cy) as f64,
                    self.inner_r as f64, self.outer_r as f64, true,
                ) {
                    self.tracker.swap_slot_with_overflow(drop, start_ov);
                    self.re_register_thumbnail(hwnd, drop);
                } else if let Some(pr) = self.overflow_panel_rect {
                    if x >= pr.left && x < pr.right && y >= pr.top && y < pr.bottom {
                        let ov_visible = self.ov_visible_count();
                        let vi = ((y - pr.top) / OVERFLOW_ROW_H) as usize;
                        let drop_ov = self.overflow_scroll + vi;
                        if vi < ov_visible && drop_ov != start_ov && drop_ov < self.tracker.overflow().len() {
                            self.tracker.swap_overflow(start_ov, drop_ov);
                        }
                    }
                }
            } else if start_ov < self.tracker.overflow().len() {
                let target = self.tracker.overflow()[start_ov].hwnd;
                self.dismiss(hwnd, Some(target));
                dismissed = true;
            }
        }

        // Always redraw after drag ends to erase the ghost, unless we already dismissed.
        if !dismissed {
            self.clear_ghost();
            let _ = self.draw_frame(hwnd);
        }
    }

    pub fn on_scroll(&mut self, hwnd: HWND, x: f32, y: f32, delta: i32) {
        let in_panel = self.overflow_panel_rect
            .map(|pr| x >= pr.left && x < pr.right && y >= pr.top && y < pr.bottom)
            .unwrap_or(false);
        if !in_panel { return; }
        let count = self.tracker.overflow().len();
        let ov_visible = self.ov_visible_count();
        if count <= ov_visible { return; }
        let max_scroll = (count - ov_visible) as i32;
        // Positive delta = wheel up = show earlier items = decrease offset.
        let new_scroll = self.overflow_scroll as i32 + if delta > 0 { -3 } else { 3 };
        self.overflow_scroll = new_scroll.clamp(0, max_scroll) as usize;
        let _ = self.draw_frame(hwnd);
    }

    // ---- Pixel-level drag ghost on separate overlay window (above DWM thumbnails) ----

    fn clear_ghost(&self) {
        // Move the ghost window off-screen at 1×1. Content doesn't matter — it's invisible.
        unsafe {
            let sdc = GetDC(None);
            let blend = BLENDFUNCTION {
                BlendOp: AC_SRC_OVER as u8, BlendFlags: 0,
                SourceConstantAlpha: 255, AlphaFormat: AC_SRC_ALPHA as u8,
            };
            let _ = UpdateLayeredWindow(
                self.ghost_hwnd, sdc,
                Some(&POINT { x: -100, y: -100 }),
                Some(&SIZE { cx: 1, cy: 1 }),
                self.ghost_mem_dc, Some(&POINT { x: 0, y: 0 }),
                COLORREF(0), Some(&blend), ULW_ALPHA,
            );
            ReleaseDC(None, sdc);
        }
    }

    fn draw_drag_ghost(&self) {
        let n = (GHOST_DIB_W * GHOST_DIB_H * 4) as usize;
        let stride = GHOST_DIB_W as usize * 4;

        // Zero the small ghost DIB (~140 KB, sub-millisecond).
        let dib = unsafe { std::slice::from_raw_parts_mut(self.ghost_dib_bits as *mut u8, n) };
        unsafe { std::ptr::write_bytes(dib.as_mut_ptr(), 0, n); }

        let cx = self.drag_cur_x as i32;
        let cy = self.drag_cur_y as i32;

        let (gw, gh, icon_key, icon_dst_size) = if self.drag_start_slot >= 0 {
            let key = self.tracker.slots()[self.drag_start_slot as usize]
                .as_ref().map(|w| w.hwnd.0 as usize);
            (160i32, 90i32, key, 32i32)
        } else if self.drag_start_overflow >= 0 {
            let oi = self.drag_start_overflow as usize;
            let key = self.tracker.overflow().get(oi).map(|w| w.hwnd.0 as usize);
            (OVERFLOW_PANEL_W as i32, OVERFLOW_ROW_H as i32, key, 28i32)
        } else {
            return;
        };

        // Draw the ghost at (0, 0) in the small DIB.
        composite_rect(dib, stride, 0, 0, gw, gh, 10, 13, 20, 230);
        draw_rect_border(dib, stride, 0, 0, gw, gh, 255, 255, 255, 180);

        // Blit icon.
        if let Some(key) = icon_key {
            if let Some(pixels) = self.icon_pixel_cache.get(&key) {
                let icon_x = if self.drag_start_overflow >= 0 { 8 } else { gw / 2 - icon_dst_size / 2 };
                let icon_y = gh / 2 - icon_dst_size / 2;
                blit_icon(dib, stride, pixels, ICON_SIZE as usize, icon_x, icon_y, icon_dst_size);
            }
        }

        // Position the ghost window centered on the cursor, content and position in one ULW call.
        let screen_x = self.virt_x + cx - gw / 2;
        let screen_y = self.virt_y + cy - gh / 2;
        unsafe {
            let sdc = GetDC(None);
            let blend = BLENDFUNCTION {
                BlendOp: AC_SRC_OVER as u8,
                BlendFlags: 0,
                SourceConstantAlpha: 255,
                AlphaFormat: AC_SRC_ALPHA as u8,
            };
            let _ = UpdateLayeredWindow(
                self.ghost_hwnd, sdc,
                Some(&POINT { x: screen_x, y: screen_y }),
                Some(&SIZE { cx: gw, cy: gh }),
                self.ghost_mem_dc,
                Some(&POINT { x: 0, y: 0 }),
                COLORREF(0), Some(&blend), ULW_ALPHA,
            );
            ReleaseDC(None, sdc);
        }
    }
}

impl Drop for WheelState {
    fn drop(&mut self) {
        self.clear_thumbnails();
        unsafe {
            if !self.mem_dc.0.is_null() { let _ = DeleteDC(self.mem_dc); }
            if !self.dib.0.is_null() { let _ = DeleteObject(HGDIOBJ(self.dib.0)); }
            if !self.ghost_mem_dc.0.is_null() { let _ = DeleteDC(self.ghost_mem_dc); }
            if !self.ghost_dib.0.is_null() { let _ = DeleteObject(HGDIOBJ(self.ghost_dib.0)); }
            if !self.ghost_hwnd.0.is_null() { let _ = DestroyWindow(self.ghost_hwnd); }
        }
    }
}

// ---- Geometry helper (free function to avoid borrow issues) ----

fn build_donut_geo(
    factory: &ID2D1Factory,
    cx: f32, cy: f32, inner_r: f32, outer_r: f32,
) -> windows::core::Result<windows::Win32::Graphics::Direct2D::ID2D1PathGeometry> {
    unsafe {
        let geo = factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.SetFillMode(D2D1_FILL_MODE_ALTERNATE);
        // Outer circle — two half-arcs, clockwise.
        sink.BeginFigure(pt(cx + outer_r, cy), D2D1_FIGURE_BEGIN_FILLED);
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: pt(cx - outer_r, cy),
            size: D2D_SIZE_F { width: outer_r, height: outer_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: pt(cx + outer_r, cy),
            size: D2D_SIZE_F { width: outer_r, height: outer_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.EndFigure(D2D1_FIGURE_END_CLOSED);
        // Inner circle — same direction; even-odd rule punches a hole.
        sink.BeginFigure(pt(cx + inner_r, cy), D2D1_FIGURE_BEGIN_FILLED);
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: pt(cx - inner_r, cy),
            size: D2D_SIZE_F { width: inner_r, height: inner_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: pt(cx + inner_r, cy),
            size: D2D_SIZE_F { width: inner_r, height: inner_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.EndFigure(D2D1_FIGURE_END_CLOSED);
        sink.Close()?;
        Ok(geo)
    }
}

fn build_slice_geo(
    factory: &ID2D1Factory,
    cx: f32, cy: f32, inner_r: f32, outer_r: f32,
    slot: usize,
) -> windows::core::Result<windows::Win32::Graphics::Direct2D::ID2D1PathGeometry> {
    let start_deg = wheel_geometry::slice_boundary_angle_deg(slot) as f32;
    let end_deg = start_deg + wheel_geometry::SLICE_SPAN_DEG as f32;
    let s = start_deg.to_radians();
    let e = end_deg.to_radians();
    let p0 = pt(cx + s.cos() * inner_r, cy + s.sin() * inner_r);
    let p1 = pt(cx + s.cos() * outer_r, cy + s.sin() * outer_r);
    let p2 = pt(cx + e.cos() * outer_r, cy + e.sin() * outer_r);
    let p3 = pt(cx + e.cos() * inner_r, cy + e.sin() * inner_r);

    unsafe {
        let geo = factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.BeginFigure(p0, D2D1_FIGURE_BEGIN_FILLED);
        sink.AddLine(p1);
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: p2,
            size: D2D_SIZE_F { width: outer_r, height: outer_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.AddLine(p3);
        sink.AddArc(&D2D1_ARC_SEGMENT {
            point: p0,
            size: D2D_SIZE_F { width: inner_r, height: inner_r },
            rotationAngle: 0.0,
            sweepDirection: D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
            arcSize: D2D1_ARC_SIZE_SMALL,
        });
        sink.EndFigure(D2D1_FIGURE_END_CLOSED);
        sink.Close()?;
        Ok(geo)
    }
}

// ---- Pixel compositing helpers for drag ghost ----

// Alpha-over composite a solid BGRA color onto a premultiplied-BGRA buffer region.
fn composite_rect(buf: &mut [u8], stride: usize, x0: i32, y0: i32, x1: i32, y1: i32, b: u8, g: u8, r: u8, a: u8) {
    let buf_w = (stride / 4) as i32;
    let buf_h = (buf.len() / stride) as i32;
    let xa = x0.max(0) as usize;
    let ya = y0.max(0) as usize;
    let xb = x1.min(buf_w) as usize;
    let yb = y1.min(buf_h) as usize;
    if xa >= xb || ya >= yb { return; }

    let pa = a as u32;
    let pb = (b as u32 * pa + 127) / 255;
    let pg = (g as u32 * pa + 127) / 255;
    let pr = (r as u32 * pa + 127) / 255;
    let inv = 255 - pa;

    for y in ya..yb {
        let row = &mut buf[y * stride + xa * 4..y * stride + xb * 4];
        for px in row.chunks_mut(4) {
            px[0] = (pb + (px[0] as u32 * inv + 127) / 255) as u8;
            px[1] = (pg + (px[1] as u32 * inv + 127) / 255) as u8;
            px[2] = (pr + (px[2] as u32 * inv + 127) / 255) as u8;
            px[3] = (pa + (px[3] as u32 * inv + 127) / 255) as u8;
        }
    }
}

fn draw_rect_border(buf: &mut [u8], stride: usize, x0: i32, y0: i32, x1: i32, y1: i32, b: u8, g: u8, r: u8, a: u8) {
    composite_rect(buf, stride, x0,     y0,     x1,     y0 + 1, b, g, r, a);
    composite_rect(buf, stride, x0,     y1 - 1, x1,     y1,     b, g, r, a);
    composite_rect(buf, stride, x0,     y0,     x0 + 1, y1,     b, g, r, a);
    composite_rect(buf, stride, x1 - 1, y0,     x1,     y1,     b, g, r, a);
}

// Blit a premultiplied-BGRA icon (src_size×src_size) onto the buffer, scaling to dst_size×dst_size.
fn blit_icon(buf: &mut [u8], stride: usize, pixels: &[u8], src_size: usize, dst_x: i32, dst_y: i32, dst_size: i32) {
    let buf_w = (stride / 4) as i32;
    let buf_h = (buf.len() / stride) as i32;
    for dy in 0..dst_size {
        let sy = (dy as usize * src_size) / dst_size as usize;
        let fy = dst_y + dy;
        if fy < 0 || fy >= buf_h { continue; }
        for dx in 0..dst_size {
            let sx = (dx as usize * src_size) / dst_size as usize;
            let fx = dst_x + dx;
            if fx < 0 || fx >= buf_w { continue; }
            let src_off = (sy * src_size + sx) * 4;
            if src_off + 3 >= pixels.len() { continue; }
            let sa = pixels[src_off + 3] as u32;
            if sa == 0 { continue; }
            let dst_off = fy as usize * stride + fx as usize * 4;
            let inv = 255 - sa;
            buf[dst_off    ] = (pixels[src_off    ] as u32 + (buf[dst_off    ] as u32 * inv + 127) / 255) as u8;
            buf[dst_off + 1] = (pixels[src_off + 1] as u32 + (buf[dst_off + 1] as u32 * inv + 127) / 255) as u8;
            buf[dst_off + 2] = (pixels[src_off + 2] as u32 + (buf[dst_off + 2] as u32 * inv + 127) / 255) as u8;
            buf[dst_off + 3] = (sa + (buf[dst_off + 3] as u32 * inv + 127) / 255) as u8;
        }
    }
}

// ---- Icon loading helpers ----

unsafe fn get_window_icon(hwnd: HWND) -> Option<HICON> {
    let r = SendMessageW(hwnd, WM_GETICON, WPARAM(ICON_BIG as usize), LPARAM(0));
    if r.0 != 0 { return Some(HICON(r.0 as *mut c_void)); }
    let r = SendMessageW(hwnd, WM_GETICON, WPARAM(ICON_SMALL2 as usize), LPARAM(0));
    if r.0 != 0 { return Some(HICON(r.0 as *mut c_void)); }
    let cls = GetClassLongPtrW(hwnd, GCL_HICON);
    if cls != 0 { return Some(HICON(cls as *mut c_void)); }
    None
}

unsafe fn hicon_to_d2d_bitmap(rt: &ID2D1RenderTarget, hicon: HICON) -> (Option<ID2D1Bitmap>, Vec<u8>) {
    let size = ICON_SIZE;
    let dc = CreateCompatibleDC(None);
    if dc.0.is_null() { return (None, Vec::new()); }

    let bmi = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: size as i32,
            biHeight: -(size as i32),
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut bits: *mut c_void = std::ptr::null_mut();
    let dib = match CreateDIBSection(None, &bmi, DIB_RGB_COLORS, &mut bits, None, 0) {
        Ok(h) => h,
        Err(_) => { let _ = DeleteDC(dc); return (None, Vec::new()); }
    };
    let old_obj = SelectObject(dc, HGDIOBJ(dib.0));
    std::ptr::write_bytes(bits as *mut u8, 0, (size * size * 4) as usize);
    let _ = DrawIconEx(dc, 0, 0, hicon, size as i32, size as i32, 0, None, DI_NORMAL);

    let pixels = std::slice::from_raw_parts_mut(bits as *mut u8, (size * size * 4) as usize);
    let all_alpha_zero = pixels.chunks(4).all(|c| c[3] == 0);
    for c in pixels.chunks_mut(4) {
        if all_alpha_zero {
            if c[0] != 0 || c[1] != 0 || c[2] != 0 { c[3] = 255; }
        } else if c[3] < 255 {
            let a = c[3] as u32;
            c[0] = ((c[0] as u32 * a + 127) / 255) as u8;
            c[1] = ((c[1] as u32 * a + 127) / 255) as u8;
            c[2] = ((c[2] as u32 * a + 127) / 255) as u8;
        }
    }

    // Clone premultiplied pixels for software drag-ghost compositing.
    let pixels_vec = pixels.to_vec();

    let bmp_props = D2D1_BITMAP_PROPERTIES {
        pixelFormat: D2D1_PIXEL_FORMAT {
            format: DXGI_FORMAT_B8G8R8A8_UNORM,
            alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
        },
        dpiX: 96.0, dpiY: 96.0,
    };
    let result = rt.CreateBitmap(
        D2D_SIZE_U { width: size, height: size },
        Some(bits),
        size * 4,
        &bmp_props,
    ).ok();

    SelectObject(dc, old_obj);
    let _ = DeleteObject(HGDIOBJ(dib.0));
    let _ = DeleteDC(dc);
    (result, pixels_vec)
}

// ---- Window class registration and creation ----

/// Create and show the overlay wheel window.
/// Ownership of WheelState is transferred into GWLP_USERDATA and freed in WM_NCDESTROY.
pub fn create_wheel_window() -> windows::core::Result<HWND> {
    unsafe {
        let hmod = GetModuleHandleW(PCWSTR::null())?;
        let hinstance = HINSTANCE(hmod.0);

        let virt_x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        let virt_y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        let virt_w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        let virt_h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        let d2d_factory: ID2D1Factory =
            D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
        let dwrite_factory: IDWriteFactory =
            DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;

        let mem_dc = CreateCompatibleDC(None);
        if mem_dc.0.is_null() {
            return Err(windows::core::Error::from_win32());
        }
        let bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: virt_w,
                biHeight: -virt_h,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut dib_bits: *mut c_void = std::ptr::null_mut();
        let dib = CreateDIBSection(None, &bmi, DIB_RGB_COLORS, &mut dib_bits, None, 0)?;
        SelectObject(mem_dc, HGDIOBJ(dib.0));

        // Ghost overlay DIB — small fixed size, repositioned per-frame via UpdateLayeredWindow.
        let ghost_mem_dc = CreateCompatibleDC(None);
        if ghost_mem_dc.0.is_null() {
            return Err(windows::core::Error::from_win32());
        }
        let ghost_bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: GHOST_DIB_W,
                biHeight: -GHOST_DIB_H,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut ghost_dib_bits: *mut c_void = std::ptr::null_mut();
        let ghost_dib = CreateDIBSection(None, &ghost_bmi, DIB_RGB_COLORS, &mut ghost_dib_bits, None, 0)?;
        SelectObject(ghost_mem_dc, HGDIOBJ(ghost_dib.0));

        // Register window class and create main wheel window.
        let class_pcwstr = w!("WheelSwitcher");
        let wcex = WNDCLASSEXW {
            cbSize: size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(wnd_proc),
            hInstance: hinstance,
            lpszClassName: class_pcwstr,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH(std::ptr::null_mut()),
            ..Default::default()
        };
        let _ = RegisterClassExW(&wcex);

        // Pass null for lpCreateParams — state_ptr is set via SetWindowLongPtrW below,
        // after both windows are created and WheelState (which needs ghost_hwnd) is built.
        let hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST,
            class_pcwstr,
            PCWSTR::null(),
            WS_POPUP,
            virt_x, virt_y, virt_w, virt_h,
            None, None, hinstance,
            None,
        )?;

        // Ghost overlay window — purely visual, always WS_EX_TRANSPARENT so input passes through.
        // Uses the same wnd_proc; passing no state pointer keeps it in DefWindowProcW territory.
        let ghost_hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST,
            class_pcwstr,
            PCWSTR::null(),
            WS_POPUP,
            virt_x, virt_y, virt_w, virt_h,
            None, None, hinstance,
            None, // no WheelState — wnd_proc falls through to DefWindowProcW
        )?;

        // Build WheelState on the heap. tracker.self_hwnd is patched after CreateWindowExW.
        let state = Box::new(WheelState {
            d2d_factory,
            dwrite_factory,
            render_target: None,
            text_format: None,
            text_format_sm: None,
            icon_cache: HashMap::new(),
            mem_dc,
            dib,
            dib_bits,
            ghost_hwnd,
            ghost_mem_dc,
            ghost_dib,
            ghost_dib_bits,
            virt_x, virt_y, virt_w, virt_h,
            cx: 0.0, cy: 0.0, inner_r: 0.0, outer_r: 0.0,
            thumb_rects: Default::default(),
            tracker: WindowTracker::new(HWND(std::ptr::null_mut())),
            thumbs: [0isize; MAX_SLOTS],
            wheel_open: false,
            hover_slot: -1, default_slot: -1,
            drag_start_slot: -1, drag_start_overflow: -1,
            drag_start_x: 0.0, drag_start_y: 0.0,
            drag_cur_x: 0.0, drag_cur_y: 0.0,
            is_dragging: false,
            icon_pixel_cache: HashMap::new(),
            overflow_panel_rect: None,
            overflow_hover_idx: -1,
            overflow_scroll: 0,
        });
        // Transfer ownership to raw pointer. Freed in WM_NCDESTROY.
        let state_ptr = Box::into_raw(state);

        // Patch the GWLP_USERDATA we couldn't set at CreateWindowExW time (state was on stack).
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, state_ptr as isize);

        // Patch the tracker's self-HWND filter now that we have a real HWND.
        (*state_ptr).tracker = WindowTracker::new(hwnd);

        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        let _ = ShowWindow(ghost_hwnd, SW_SHOWNOACTIVATE);
        (*state_ptr).paint_transparent(hwnd);
        (*state_ptr).clear_ghost();

        // Add tray icon.
        let hicon = LoadIconW(None, IDI_APPLICATION).unwrap_or_default();
        let mut nid = NOTIFYICONDATAW {
            cbSize: size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: TRAY_ID,
            uFlags: NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage: MSG_TRAY,
            hIcon: hicon,
            ..Default::default()
        };
        let tip: Vec<u16> = "Wheel Switcher\0".encode_utf16().collect();
        nid.szTip[..tip.len()].copy_from_slice(&tip);
        let _ = Shell_NotifyIconW(NIM_ADD, &nid);

        Ok(hwnd)
    }
}

// ---- WndProc ----

unsafe extern "system" fn wnd_proc(
    hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM,
) -> LRESULT {
    if msg == windows::Win32::UI::WindowsAndMessaging::WM_CREATE {
        let cs = &*(lparam.0 as *const CREATESTRUCTW);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, cs.lpCreateParams as isize);
        return LRESULT(0);
    }

    if msg == WM_NCDESTROY {
        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut WheelState;
        if !ptr.is_null() {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            let nid = NOTIFYICONDATAW {
                cbSize: size_of::<NOTIFYICONDATAW>() as u32,
                hWnd: hwnd,
                uID: TRAY_ID,
                ..Default::default()
            };
            let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
            drop(Box::from_raw(ptr));
            PostQuitMessage(0); // only quit when the main wheel window is destroyed
        }
        return LRESULT(0);
    }

    let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut WheelState;
    if ptr.is_null() {
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }
    let state = &mut *ptr;

    match msg {
        m if m == MSG_ALT_TAB => {
            if state.wheel_open { state.advance_selection(hwnd, 1); }
            else { state.present(hwnd); }
            LRESULT(0)
        }
        m if m == MSG_ALT_SHIFT_TAB => {
            if state.wheel_open { state.advance_selection(hwnd, -1); }
            LRESULT(0)
        }
        m if m == MSG_ALT_RELEASED => {
            if state.wheel_open { state.commit(hwnd); }
            LRESULT(0)
        }
        m if m == MSG_ESCAPE => {
            if state.wheel_open { state.dismiss(hwnd, None); }
            LRESULT(0)
        }
        m if m == MSG_TRAY => {
            let notif = lparam.0 as u32;
            if notif == WM_RBUTTONUP || notif == WM_CONTEXTMENU {
                let menu = CreatePopupMenu().unwrap_or_default();
                let _ = AppendMenuW(menu, MF_STRING, 1, w!("Exit"));
                let mut cp = POINT::default();
                let _ = GetCursorPos(&mut cp);
                let _ = SetForegroundWindow(hwnd);
                let cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, cp.x, cp.y, 0, hwnd, None);
                let _ = DestroyMenu(menu);
                if cmd.0 == 1 {
                    let _ = DestroyWindow(hwnd);
                }
            }
            LRESULT(0)
        }
        WM_TIMER => {
            if state.wheel_open && !state.is_dragging {
                let mut pt = POINT::default();
                if unsafe { GetCursorPos(&mut pt) }.is_ok() {
                    let x = (pt.x - state.virt_x) as f32;
                    let y = (pt.y - state.virt_y) as f32;
                    state.on_mouse_move(hwnd, x, y);
                }
            }
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            if state.wheel_open {
                let x = (lparam.0 & 0xFFFF) as i16 as f32;
                let y = ((lparam.0 >> 16) & 0xFFFF) as i16 as f32;
                if state.is_dragging {
                    // Pixel-level ghost: fast enough (~0.5 ms) to process every message.
                    state.drag_cur_x = x;
                    state.drag_cur_y = y;
                    state.draw_drag_ghost();
                } else {
                    // Coalesce D2D redraws: skip if a newer move is already queued.
                    let mut peek = MSG::default();
                    if !PeekMessageW(&mut peek, hwnd, WM_MOUSEMOVE, WM_MOUSEMOVE, PM_NOREMOVE).as_bool() {
                        state.on_mouse_move(hwnd, x, y);
                    }
                }
            }
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            if state.wheel_open {
                let x = (lparam.0 & 0xFFFF) as i16 as f32;
                let y = ((lparam.0 >> 16) & 0xFFFF) as i16 as f32;
                state.on_mouse_down(hwnd, x, y);
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if state.wheel_open {
                let x = (lparam.0 & 0xFFFF) as i16 as f32;
                let y = ((lparam.0 >> 16) & 0xFFFF) as i16 as f32;
                state.on_mouse_up(hwnd, x, y);
            }
            LRESULT(0)
        }
        WM_MOUSEWHEEL => {
            if state.wheel_open {
                // lparam for WM_MOUSEWHEEL contains screen coordinates.
                let x = (lparam.0 & 0xFFFF) as i16 as f32 - state.virt_x as f32;
                let y = ((lparam.0 >> 16) & 0xFFFF) as i16 as f32 - state.virt_y as f32;
                let delta = ((wparam.0 >> 16) as i16) as i32;
                state.on_scroll(hwnd, x, y, delta);
            }
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}
