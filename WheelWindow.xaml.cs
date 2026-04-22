using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace WheelSwitcher;

public partial class WheelWindow : Window
{
    private const int SlotCount = 8;

    // Geometry (computed on show)
    private double _cx, _cy;           // center of wheel (virtual screen coords relative to window)
    private double _innerRadius;       // hub radius (dead zone / logo)
    private double _outerRadius;       // visual outer radius of the wheel
    private double _sliceSpanDeg = 360.0 / SlotCount;

    // For each slot:
    // - a Path that draws the glass slice overlay
    // - a Border that hosts the DWM thumbnail rectangle
    // - a Label with the window title
    // - an Image with the icon (near hub)
    private readonly SliceVisual?[] _visuals = new SliceVisual?[SlotCount];

    // DWM thumbnails indexed by slot
    private readonly IntPtr[] _thumbs = new IntPtr[SlotCount];

    private WindowTracker? _tracker;
    private IntPtr _hwnd;

    // Layout cache — rebuilt only when the active monitor changes
    private MonitorInfo _cachedMonitor;
    private bool _hasLayout;

    // Interaction state
    private int _hoverSlot = -1;
    private int _defaultSlot = -1;  // keyboard pre-selection; used when mouse is in hub
    private int _dragStartSlot = -1;
    private Point _dragStartPoint;
    private bool _isDragging;

    public int HoverSlot => _hoverSlot >= 0 ? _hoverSlot : _defaultSlot;

    public WheelWindow()
    {
        InitializeComponent();

        // Span the full virtual screen (all monitors). We can't just Maximize - that only covers one monitor.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Start as non-activating, tool window, and fully click-through (transparent to input).
        // WS_EX_TRANSPARENT is removed in Present() and re-added in Dismiss().
        int ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW
               | NativeMethods.WS_EX_TRANSPARENT);

        // Disable DWM transitions before the window is ever shown to prevent blink.
        int disabled = 1;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref disabled, sizeof(int));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ClearThumbnails();
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Present the wheel with the given windows and place the cursor at center.</summary>
    public void Present(WindowTracker tracker)
    {
        _tracker = tracker;

        var p = GetCursorScreenPoint();
        var primary = GetMonitorRectContaining(p);
        _cx = primary.cx - Left;
        _cy = primary.cy - Top;
        _outerRadius = Math.Min(primary.w, primary.h) * 0.40;
        _innerRadius = _outerRadius * 0.18;

        bool monitorChanged = !_hasLayout
            || _cachedMonitor.cx != primary.cx || _cachedMonitor.cy != primary.cy
            || _cachedMonitor.w  != primary.w  || _cachedMonitor.h  != primary.h;

        if (monitorChanged)
        {
            BuildVisuals();
            _cachedMonitor = primary;
            _hasLayout = true;
        }
        else
        {
            UpdateContent();
        }

        // Remove click-through so the window can receive mouse input.
        int ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex & ~NativeMethods.WS_EX_TRANSPARENT);

        NativeMethods.SetCursorPos((int)primary.cx, (int)primary.cy);
        AnimateIn();
        RegisterThumbnails();
    }

    public void Dismiss()
    {
        ClearThumbnails();

        // Reset all slice highlights instantly so they don't carry over to the next show.
        for (int i = 0; i < SlotCount; i++)
        {
            if (_visuals[i]?.Highlight is Path h && h.Fill is SolidColorBrush b)
            {
                if (b.IsFrozen) { b = b.Clone(); h.Fill = b; }
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            }
        }

        _hoverSlot = -1;
        _defaultSlot = -1;

        // Cancel animation and snap back to opacity 0.
        RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
        RootGrid.Opacity = 0;

        // Re-apply click-through so the invisible window doesn't block mouse input.
        int ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_TRANSPARENT);
    }

    // ---- Build the visual layout ----

    /// <summary>Refresh titles and icons on the cached element tree without touching geometry.</summary>
    private void UpdateContent()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            var v = _visuals[i];
            if (v is null) continue;

            if (_tracker?.Slots[i] is TrackedWindow tw)
            {
                if (v.Title is not null) v.Title.Text = tw.Title;
                if (v.Icon  is not null) v.Icon.Source = WindowActivator.GetIconForWindow(tw.Handle, tw.ProcessPath);
                if (v.ThumbHost is not null) v.ThumbHost.Opacity = 1.0;
            }
            else
            {
                if (v.Title is not null) v.Title.Text = "";
                if (v.Icon  is not null) v.Icon.Source = null;
                if (v.ThumbHost is not null) v.ThumbHost.Opacity = 0.35;
            }
        }
    }

    private void BuildVisuals()
    {
        WheelCanvas.Children.Clear();

        // Draw from back to front: backdrop glow, slice glass fills, dividers, thumbnails, hub, icons, labels.

        // 1. Soft wheel glow
        var glow = new Ellipse
        {
            Width = _outerRadius * 2 + 80,
            Height = _outerRadius * 2 + 80,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.0),
                    new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.55),
                    new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                })
        };
        Canvas.SetLeft(glow, _cx - glow.Width / 2);
        Canvas.SetTop(glow, _cy - glow.Height / 2);
        WheelCanvas.Children.Add(glow);

        // 2. Main glass disc (subtle, tints the whole wheel)
        var disc = new Ellipse
        {
            Width = _outerRadius * 2,
            Height = _outerRadius * 2,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0.0),
                    new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.7),
                    new(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 1.0),
                }),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.0
        };
        Canvas.SetLeft(disc, _cx - _outerRadius);
        Canvas.SetTop(disc, _cy - _outerRadius);
        WheelCanvas.Children.Add(disc);

        // 3. Per-slice visuals
        for (int i = 0; i < SlotCount; i++)
        {
            BuildSlice(i);
        }

        // 4. Dividers - drawn on top of slices so they sit above thumbnail rects too.
        for (int i = 0; i < SlotCount; i++)
        {
            double angleDeg = SliceBoundaryAngleDeg(i);
            WheelCanvas.Children.Add(BuildDivider(angleDeg));
        }

        // 5. Hub (dead zone) with subtle radial gradient
        var hub = new Ellipse
        {
            Width = _innerRadius * 2,
            Height = _innerRadius * 2,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x55, 0x20, 0x22, 0x28), 0.0),
                    new(Color.FromArgb(0x33, 0x10, 0x11, 0x14), 0.8),
                    new(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0),
                }),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.8
        };
        Canvas.SetLeft(hub, _cx - _innerRadius);
        Canvas.SetTop(hub, _cy - _innerRadius);
        WheelCanvas.Children.Add(hub);
    }

    /// <summary>Angle in degrees for the boundary BEFORE slot i (i.e. slot 0's leading edge).</summary>
    private double SliceBoundaryAngleDeg(int i)
    {
        // Slot 0 centred at -90° (12 o'clock). Leading edge is -90° - 22.5° = -112.5°.
        return -90.0 - _sliceSpanDeg / 2.0 + i * _sliceSpanDeg;
    }

    /// <summary>Center angle (degrees) of slot i.</summary>
    private double SliceCenterAngleDeg(int i)
    {
        return -90.0 + i * _sliceSpanDeg;
    }

    /// <summary>Convert a point around the wheel to a slot index, or -1 if inside the hub.</summary>
    public int PointToSlot(Point p)
    {
        double dx = p.X - _cx;
        double dy = p.Y - _cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < _innerRadius) return -1;

        double angDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;   // -180..180, 0 = +X (right)
        // We want 0 = top. Normalize so 0 is top and increases clockwise.
        double fromTop = (angDeg + 90.0 + 360.0) % 360.0;
        // Subtract half-slice so that slot 0 spans -22.5..+22.5 around top.
        double shifted = (fromTop + _sliceSpanDeg / 2.0) % 360.0;
        int slot = (int)(shifted / _sliceSpanDeg) % SlotCount;
        return slot;
    }

    // ---- Slice construction ----
    private void BuildSlice(int i)
    {
        var v = new SliceVisual();
        _visuals[i] = v;

        // Thumbnail host rect: the largest rectangle inscribed in the slice, positioned
        // with its center along the slice's center ray at ~60% of the outer radius.
        double centerAngle = SliceCenterAngleDeg(i) * Math.PI / 180.0;
        double thumbCenterR = _outerRadius * 0.65;

        double thumbDisplayAngle = centerAngle;
        if (i % 2 == 1)
        {
            double sign = (i == 1 || i == 5) ? 1.0 : -1.0;
            thumbDisplayAngle += sign * (5.0 * Math.PI / 180.0);
        }

        double thumbCx = _cx + Math.Cos(thumbDisplayAngle) * thumbCenterR;
        double thumbCy = _cy + Math.Sin(thumbDisplayAngle) * thumbCenterR;

        // Slice arc width at that radius:
        // chord = 2 * r * sin(halfAngle). At radius thumbCenterR with full slice span:
        double halfSpan = (_sliceSpanDeg / 2.0) * Math.PI / 180.0;
        double maxChord = 2 * thumbCenterR * Math.Sin(halfSpan);

        // Radial depth we can use: from 0.35*outer to 0.95*outer = 0.6*outer.
        double radialDepth = _outerRadius * 0.58;

        // Fit a 16:9 rect inside those bounds.
        double thumbW = Math.Min(maxChord * 0.92, radialDepth * 16.0 / 9.0);
        double thumbH = thumbW * 9.0 / 16.0;
        if (thumbH > radialDepth) { thumbH = radialDepth; thumbW = thumbH * 16.0 / 9.0; }

        // We rotate the thumbnail so its "up" points towards the wheel's outer rim.
        // For DWM thumbnails, we can't actually rotate (the compositor paints axis-aligned).
        // So we keep it axis-aligned but position it along the slice ray — simple, predictable.
        var thumbHost = new Border
        {
            Width = thumbW,
            Height = thumbH,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x0A, 0x0D, 0x12)),   // placeholder under the thumbnail
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22, ShadowDepth = 0, Color = Colors.Black, Opacity = 0.55
            },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(thumbHost, thumbCx - thumbW / 2);
        Canvas.SetTop(thumbHost, thumbCy - thumbH / 2);
        WheelCanvas.Children.Add(thumbHost);
        v.ThumbHost = thumbHost;
        v.ThumbRect = new Rect(thumbCx - thumbW / 2, thumbCy - thumbH / 2, thumbW, thumbH);

        // Highlight path - a pie slice that tints when hovered. We build the geometry from scratch.
        var highlight = new Path
        {
            Data = BuildSliceGeometry(i, _innerRadius, _outerRadius),
            Fill = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),   // transparent by default
            IsHitTestVisible = false
        };
        WheelCanvas.Children.Add(highlight);
        v.Highlight = highlight;

        // Icon near the hub
        var iconImage = new Image
        {
            Width = 34,
            Height = 34,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = Colors.Black, Opacity = 0.8 }
        };
        // Place just outside the hub along the slice's center ray.
        double iconR = _innerRadius + 26;
        double ix = _cx + Math.Cos(centerAngle) * iconR - iconImage.Width / 2;
        double iy = _cy + Math.Sin(centerAngle) * iconR - iconImage.Height / 2;
        Canvas.SetLeft(iconImage, ix);
        Canvas.SetTop(iconImage, iy);
        WheelCanvas.Children.Add(iconImage);
        v.Icon = iconImage;

        // Title label, placed below the thumbnail along the same ray, outside the wheel
        var title = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            Width = thumbW,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Color = Colors.Black, Opacity = 0.9 }
        };
        bool textAbove = (i == 0 || i == 1 || i == 7);
        double ty = textAbove
            ? thumbCy - thumbH / 2 - 32
            : thumbCy + thumbH / 2 + 12;
        Canvas.SetLeft(title, thumbCx - thumbW / 2);
        Canvas.SetTop(title, ty);
        WheelCanvas.Children.Add(title);
        v.Title = title;

        // Populate
        if (_tracker is not null && _tracker.Slots[i] is TrackedWindow tw)
        {
            title.Text = tw.Title;
            iconImage.Source = WindowActivator.GetIconForWindow(tw.Handle, tw.ProcessPath);
        }
        else
        {
            // Empty slot: dim the border, clear title/icon
            thumbHost.Opacity = 0.35;
            title.Text = "";
        }
    }

    /// <summary>
    /// Build a pie-wedge geometry between the given slot's angles and the two radii.
    /// </summary>
    private Geometry BuildSliceGeometry(int slot, double rIn, double rOut)
    {
        double startDeg = SliceBoundaryAngleDeg(slot);
        double endDeg = startDeg + _sliceSpanDeg;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;

        var p0 = new Point(_cx + Math.Cos(s) * rIn, _cy + Math.Sin(s) * rIn);
        var p1 = new Point(_cx + Math.Cos(s) * rOut, _cy + Math.Sin(s) * rOut);
        var p2 = new Point(_cx + Math.Cos(e) * rOut, _cy + Math.Sin(e) * rOut);
        var p3 = new Point(_cx + Math.Cos(e) * rIn, _cy + Math.Sin(e) * rIn);

        bool isLarge = _sliceSpanDeg > 180;

        var fig = new PathFigure { StartPoint = p0, IsClosed = true };
        fig.Segments.Add(new LineSegment(p1, false));
        fig.Segments.Add(new ArcSegment(p2, new Size(rOut, rOut), 0, isLarge, SweepDirection.Clockwise, false));
        fig.Segments.Add(new LineSegment(p3, false));
        fig.Segments.Add(new ArcSegment(p0, new Size(rIn, rIn), 0, isLarge, SweepDirection.Counterclockwise, false));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Build a divider line that fades to transparent toward the outer rim and toward the hub.
    /// </summary>
    private UIElement BuildDivider(double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        var start = new Point(_cx + Math.Cos(a) * _innerRadius, _cy + Math.Sin(a) * _innerRadius);
        var end   = new Point(_cx + Math.Cos(a) * _outerRadius, _cy + Math.Sin(a) * _outerRadius);

        var line = new Line
        {
            X1 = start.X, Y1 = start.Y,
            X2 = end.X,   Y2 = end.Y,
            StrokeThickness = 1.0,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        // Gradient along the divider: opaque near hub, fading to zero at the rim.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.35));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        brush.Freeze();
        line.Stroke = brush;

        return line;
    }

    // ---- DWM Thumbnails ----
    private void RegisterThumbnails()
    {
        if (_tracker is null) return;

        for (int i = 0; i < SlotCount; i++)
        {
            var slot = _tracker.Slots[i];
            var host = _visuals[i]?.ThumbHost;
            var rect = _visuals[i]?.ThumbRect ?? Rect.Empty;
            if (slot is null || host is null || rect.IsEmpty) continue;

            // DwmRegisterThumbnail returns HRESULT; 0 == S_OK (success).
            if (NativeMethods.DwmRegisterThumbnail(_hwnd, slot.Handle, out IntPtr thumb) != 0)
                continue;     // non-zero = failure
            _thumbs[i] = thumb;

            // Convert logical rect to device pixels.
            var src = PresentationSource.FromVisual(this);
            double scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                        | NativeMethods.DWM_TNP_VISIBLE
                        | NativeMethods.DWM_TNP_OPACITY
                        | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new NativeMethods.RECT
                {
                    // Inset by 2 device pixels so the host border shows a clean rim.
                    Left   = (int)Math.Round((rect.X + 2) * scaleX),
                    Top    = (int)Math.Round((rect.Y + 2) * scaleY),
                    Right  = (int)Math.Round((rect.X + rect.Width - 2) * scaleX),
                    Bottom = (int)Math.Round((rect.Y + rect.Height - 2) * scaleY),
                },
                opacity = 235,
                fVisible = true,
                fSourceClientAreaOnly = false
            };
            NativeMethods.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private void ClearThumbnails()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_thumbs[i] != IntPtr.Zero)
            {
                NativeMethods.DwmUnregisterThumbnail(_thumbs[i]);
                _thumbs[i] = IntPtr.Zero;
            }
        }
    }

    // ---- Interaction ----
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);

        if (_isDragging && _dragStartSlot >= 0)
        {
            // Offset the start slot's thumb host with the cursor so it feels picked up.
            var host = _visuals[_dragStartSlot]?.ThumbHost;
            if (host is not null)
            {
                double dx = p.X - _dragStartPoint.X;
                double dy = p.Y - _dragStartPoint.Y;
                host.RenderTransform = new TranslateTransform(dx, dy);
                host.Opacity = 0.85;
            }
        }

        int slot = PointToSlot(p);
        if (slot != _hoverSlot)
        {
            UpdateHover(slot);
        }
    }

    private void UpdateHover(int slot)
    {
        // Which slot was visually active before, and which should be after.
        int prevActive = _hoverSlot >= 0 ? _hoverSlot : _defaultSlot;
        _hoverSlot = slot;
        int newActive = _hoverSlot >= 0 ? _hoverSlot : _defaultSlot;

        if (prevActive >= 0 && prevActive != newActive)
        {
            if (_visuals[prevActive] is SliceVisual old)
            {
                if (old.Highlight is not null) AnimateHighlight(old.Highlight, 0x00);
                if (old.ThumbHost is not null && prevActive != _dragStartSlot)
                    old.ThumbHost.RenderTransform = null;
            }
        }

        if (newActive >= 0 && newActive != prevActive && _visuals[newActive]?.Highlight is { } h)
            AnimateHighlight(h, 0x28);
    }

    private static void AnimateHighlight(Shape target, byte toAlpha)
    {
        if (target.Fill is not SolidColorBrush b) return;
        // Make sure the brush is writable.
        if (b.IsFrozen) { b = b.Clone(); target.Fill = b; }
        var anim = new ColorAnimation
        {
            To = Color.FromArgb(toAlpha, 0xFF, 0xFF, 0xFF),
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        b.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);
        int slot = PointToSlot(p);
        if (slot < 0 || _tracker is null || _tracker.Slots[slot] is null) return;

        _dragStartSlot = slot;
        _dragStartPoint = p;
        _isDragging = true;
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        var p = e.GetPosition(WheelCanvas);
        int drop = PointToSlot(p);

        int start = _dragStartSlot;
        _dragStartSlot = -1;

        // Reset any transform on the lifted thumb.
        if (start >= 0 && _visuals[start]?.ThumbHost is Border startHost)
        {
            startHost.RenderTransform = null;
            startHost.Opacity = 1.0;
        }

        bool moved = (p - _dragStartPoint).Length > 6;

        if (start >= 0 && drop >= 0 && drop != start && moved && _tracker is not null)
        {
            // Swap: rearrange slots, rebuild thumbnails in place.
            _tracker.SwapSlots(start, drop);
            RebuildSlotContent(start);
            RebuildSlotContent(drop);
        }
        else if (start >= 0 && !moved)
        {
            // Single click on a slot = switch to it and dismiss (same as release-on-slot).
            OnSlotCommitted?.Invoke(start);
        }
    }

    /// <summary>Raised when the user commits a selection by clicking on a slot.</summary>
    public event Action<int>? OnSlotCommitted;

    /// <summary>Re-register the thumbnail + refresh icon/title for one slot (after a swap).</summary>
    private void RebuildSlotContent(int i)
    {
        // Tear down old thumbnail
        if (_thumbs[i] != IntPtr.Zero)
        {
            NativeMethods.DwmUnregisterThumbnail(_thumbs[i]);
            _thumbs[i] = IntPtr.Zero;
        }

        var v = _visuals[i];
        if (v is null || _tracker is null) return;
        var tw = _tracker.Slots[i];

        if (tw is null)
        {
            if (v.Title is not null) v.Title.Text = "";
            if (v.Icon is not null) v.Icon.Source = null;
            if (v.ThumbHost is not null) v.ThumbHost.Opacity = 0.35;
            return;
        }

        if (v.Title is not null) v.Title.Text = tw.Title;
        if (v.Icon is not null) v.Icon.Source = WindowActivator.GetIconForWindow(tw.Handle, tw.ProcessPath);
        if (v.ThumbHost is not null) v.ThumbHost.Opacity = 1.0;

        // Re-register thumbnail in the same rect.
        if (NativeMethods.DwmRegisterThumbnail(_hwnd, tw.Handle, out IntPtr thumb) != 0) return;
        _thumbs[i] = thumb;

        var rect = v.ThumbRect;
        var src = PresentationSource.FromVisual(this);
        double scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE
                    | NativeMethods.DWM_TNP_OPACITY | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new NativeMethods.RECT
            {
                Left   = (int)Math.Round((rect.X + 2) * scaleX),
                Top    = (int)Math.Round((rect.Y + 2) * scaleY),
                Right  = (int)Math.Round((rect.X + rect.Width - 2) * scaleX),
                Bottom = (int)Math.Round((rect.Y + rect.Height - 2) * scaleY)
            },
            opacity = 235,
            fVisible = true,
            fSourceClientAreaOnly = false
        };
        NativeMethods.DwmUpdateThumbnailProperties(thumb, ref props);
    }

    // ---- Entry animation ----
    private void AnimateIn()
    {
        // Pure opacity fade — no scale. DWM thumbnails are composited at absolute pixel
        // coordinates and don't participate in WPF transforms, so a scale animation would
        // cause thumbnails to drift relative to their WPF slot containers.
        RootGrid.RenderTransform = null;
        RootGrid.Opacity = 0;

        RootGrid.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // ---- Monitor math ----
    private readonly record struct MonitorInfo(double cx, double cy, double w, double h, double left, double top);

    private static Point GetCursorScreenPoint()
    {
        NativeMethods.GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>Return the center and size of whichever screen contains the given point.</summary>
    private static MonitorInfo GetMonitorRectContaining(Point p)
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            if (p.X >= b.Left && p.X < b.Right && p.Y >= b.Top && p.Y < b.Bottom)
                return new MonitorInfo(b.Left + b.Width / 2.0, b.Top + b.Height / 2.0, b.Width, b.Height, b.Left, b.Top);
        }
        var pri = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return new MonitorInfo(pri.Left + pri.Width / 2.0, pri.Top + pri.Height / 2.0, pri.Width, pri.Height, pri.Left, pri.Top);
    }

    /// <summary>Advance the keyboard pre-selection to the next occupied slot (Tab / Alt+Tab while wheel open).</summary>
    public void AdvanceSelection(int dir)
    {
        if (_tracker is null) return;
        int start = _defaultSlot < 0 ? 0 : _defaultSlot;
        for (int step = 1; step <= SlotCount; step++)
        {
            int next = ((start + dir * step) % SlotCount + SlotCount) % SlotCount;
            if (_tracker.Slots[next] is not null)
            {
                int prevDefault = _defaultSlot;
                _defaultSlot = next;

                // Only update visual if the mouse isn't already hovering a slice.
                if (_hoverSlot < 0)
                {
                    if (prevDefault >= 0 && prevDefault != next && _visuals[prevDefault]?.Highlight is { } oldH)
                        AnimateHighlight(oldH, 0x00);
                    if (_visuals[next]?.Highlight is { } newH)
                        AnimateHighlight(newH, 0x28);
                }
                return;
            }
        }
    }

    private sealed class SliceVisual
    {
        public Path? Highlight;
        public Border? ThumbHost;
        public Rect ThumbRect;
        public Image? Icon;
        public TextBlock? Title;
    }
}
