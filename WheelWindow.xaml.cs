using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WheelSwitcher;

public partial class WheelWindow : Window
{
    private const int SlotCount = WheelGeometry.SlotCount;
    private const double SliceSpanDeg = WheelGeometry.SliceSpanDeg;

    // Geometry (computed on show, relative to window origin)
    private double _cx, _cy;
    private double _innerRadius;
    private double _outerRadius;

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
    private int _dragStartOverflow = -1;
    private Point _dragStartPoint;
    private bool _isDragging;
    private Border? _dragGhost;
    private TranslateTransform? _dragGhostTransform;

    // Overflow panel
    private int _overflowHoverIndex = -1;
    private readonly List<UIElement> _overflowCanvasChildren = new();
    private readonly List<Border> _overflowRows = new();
    private Rect _overflowPanelRect = Rect.Empty;

    public int HoverSlot => _hoverSlot >= 0 ? _hoverSlot : _defaultSlot;

    public TrackedWindow? SelectedOverflowWindow =>
        _overflowHoverIndex >= 0 && _tracker?.Overflow is { } ov && _overflowHoverIndex < ov.Count
            ? ov[_overflowHoverIndex]
            : null;

    public event Action<int>? OnSlotCommitted;
    public event Action<TrackedWindow>? OnOverflowCommitted;

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

    /// <summary>
    /// Build / refresh the visual tree while the wheel is still hidden.
    /// Call this at startup and after each dismiss so the next Present() is fast.
    /// </summary>
    public void PreWarm(WindowTracker tracker) => EnsureLayout(tracker);

    /// <summary>Present the wheel: make it interactive, warp cursor, and show thumbnails.</summary>
    public void Present(WindowTracker tracker)
    {
        EnsureLayout(tracker);

        int ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex & ~NativeMethods.WS_EX_TRANSPARENT);

        ClearThumbnails();   // flush any thumbnails left from a deferred dismiss
        NativeMethods.SetCursorPos((int)(_cx + Left), (int)(_cy + Top));
        RootGrid.Opacity = 1;
        RegisterThumbnails();            // registered invisible
        NextRenderFrame(ShowThumbnails); // made visible just before WPF renders opacity=1
    }

    // Shared setup for both PreWarm and Present: update tracker, recompute geometry for the
    // monitor under the cursor, and rebuild or refresh visuals as needed.
    private void EnsureLayout(WindowTracker tracker)
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
            ClearOverflowPanel();
            UpdateContent();
        }

        BuildOverflowPanel();
    }

    public void Dismiss()
    {
        // Defer thumbnail removal to the same render frame as opacity=0.
        NextRenderFrame(ClearThumbnails);

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

        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            if (_dragStartSlot >= 0 && _visuals[_dragStartSlot]?.ThumbHost is Border dh)
            { dh.RenderTransform = null; dh.Opacity = 1.0; }
            RemoveDragGhost();
            _dragStartSlot = -1;
            _dragStartOverflow = -1;
        }

        ClearOverflowPanel();

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
        // Canvas wipe also removed overflow children; reset tracking.
        _overflowCanvasChildren.Clear();
        _overflowPanelRect = Rect.Empty;
        _overflowHoverIndex = -1;

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
            Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x10, 0x16)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
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

    private static double SliceBoundaryAngleDeg(int i) => WheelGeometry.SliceBoundaryAngleDeg(i);
    private static double SliceCenterAngleDeg(int i)   => WheelGeometry.SliceCenterAngleDeg(i);

    public int PointToSlot(Point p, bool requireInBounds = true)
        => WheelGeometry.PointToSlot(p.X - _cx, p.Y - _cy, _innerRadius, _outerRadius, requireInBounds);

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
        double halfSpan = (SliceSpanDeg / 2.0) * Math.PI / 180.0;
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
            IsHitTestVisible = false
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
            IsHitTestVisible = false
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
        double endDeg = startDeg + SliceSpanDeg;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;

        var p0 = new Point(_cx + Math.Cos(s) * rIn, _cy + Math.Sin(s) * rIn);
        var p1 = new Point(_cx + Math.Cos(s) * rOut, _cy + Math.Sin(s) * rOut);
        var p2 = new Point(_cx + Math.Cos(e) * rOut, _cy + Math.Sin(e) * rOut);
        var p3 = new Point(_cx + Math.Cos(e) * rIn, _cy + Math.Sin(e) * rIn);

        bool isLarge = SliceSpanDeg > 180;

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
                fVisible = false,
                fSourceClientAreaOnly = false
            };
            NativeMethods.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private void ShowThumbnails()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_thumbs[i] == IntPtr.Zero) continue;
            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_VISIBLE,
                fVisible = true
            };
            NativeMethods.DwmUpdateThumbnailProperties(_thumbs[i], ref props);
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
            var host = _visuals[_dragStartSlot]?.ThumbHost;
            if (host is not null)
            {
                double dx = p.X - _dragStartPoint.X;
                double dy = p.Y - _dragStartPoint.Y;
                host.RenderTransform = new TranslateTransform(dx, dy);
                host.Opacity = 0.85;
            }
            return;
        }

        if (_isDragging && _dragStartOverflow >= 0)
        {
            UpdateDragGhost(p);
            return;
        }

        if (!_overflowPanelRect.IsEmpty && _overflowPanelRect.Contains(p))
        {
            if (_hoverSlot >= 0) UpdateHover(-1);
        }
        else
        {
            // requireInBounds=false: snap to the nearest slice even outside the outer radius.
            // Only the hub (inner radius) returns -1 to fall back to keyboard pre-selection.
            int slot = PointToSlot(p, requireInBounds: false);
            if (slot != _hoverSlot) UpdateHover(slot);
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

        // Overflow panel takes priority — its area overlaps wheel angle space.
        int ovIdx = HitTestOverflowRow(p);
        if (ovIdx >= 0)
        {
            _dragStartOverflow = ovIdx;
            _dragStartSlot = -1;
            _dragStartPoint = p;
            _isDragging = true;
            if (_tracker?.Overflow.Count > ovIdx)
                CreateDragGhost(_tracker.Overflow[ovIdx], p);
            CaptureMouse();
            return;
        }

        int slot = PointToSlot(p);
        if (slot < 0 || _tracker is null || _tracker.Slots[slot] is null) return;

        _dragStartSlot = slot;
        _dragStartOverflow = -1;
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
        bool moved = (p - _dragStartPoint).Length > 6;

        if (_dragStartSlot >= 0)
        {
            int start = _dragStartSlot;
            _dragStartSlot = -1;

            if (_visuals[start]?.ThumbHost is Border h) { h.RenderTransform = null; h.Opacity = 1.0; }

            if (moved && _tracker is not null)
            {
                int dropSlot = PointToSlot(p);

                if (dropSlot >= 0 && dropSlot != start)
                {
                    _tracker.SwapSlots(start, dropSlot);
                    RebuildSlotContent(start);
                    RebuildSlotContent(dropSlot);
                }
            }
            else if (!moved)
            {
                OnSlotCommitted?.Invoke(start);
            }
        }
        else if (_dragStartOverflow >= 0)
        {
            int startOv = _dragStartOverflow;
            _dragStartOverflow = -1;
            RemoveDragGhost();

            if (moved && _tracker is not null)
            {
                int dropSlot = PointToSlot(p);
                int dropOv   = HitTestOverflowRow(p);

                if (dropSlot >= 0)
                {
                    _tracker.SwapSlotWithOverflow(dropSlot, startOv);
                    RebuildSlotContent(dropSlot);
                    RebuildOverflowPanel();
                }
                else if (dropOv >= 0 && dropOv != startOv)
                {
                    _tracker.SwapOverflowItems(startOv, dropOv);
                    RebuildOverflowPanel();
                }
            }
            else if (!moved && _tracker?.Overflow.Count > startOv)
            {
                OnOverflowCommitted?.Invoke(_tracker.Overflow[startOv]);
            }
        }
    }

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

    private void NextRenderFrame(Action callback)
    {
        EventHandler? handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; callback(); };
        CompositionTarget.Rendering += handler;
    }

    // ---- Overflow panel ----

    private int HitTestOverflowRow(Point p)
    {
        if (_overflowPanelRect.IsEmpty || !_overflowPanelRect.Contains(p)) return -1;
        int idx = (int)((p.Y - _overflowPanelRect.Top) / 46);
        if (_tracker is null || idx < 0 || idx >= _tracker.Overflow.Count) return -1;
        return idx;
    }

    private void RebuildOverflowPanel()
    {
        ClearOverflowPanel();
        BuildOverflowPanel();
    }

    private void CreateDragGhost(TrackedWindow tw, Point p)
    {
        var icon = new Image
        {
            Width = 22, Height = 22,
            Source = WindowActivator.GetIconForWindow(tw.Handle, tw.ProcessPath),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            IsHitTestVisible = false
        };
        var title = new TextBlock
        {
            Text = tw.Title,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        sp.Children.Add(icon);
        sp.Children.Add(title);
        _dragGhost = new Border
        {
            Child = sp,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x14, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0, 5, 0, 5),
            IsHitTestVisible = false,
            Opacity = 0.9
        };
        _dragGhostTransform = new TranslateTransform(p.X + 14, p.Y - 18);
        _dragGhost.RenderTransform = _dragGhostTransform;
        WheelCanvas.Children.Add(_dragGhost);
    }

    private void UpdateDragGhost(Point p)
    {
        if (_dragGhostTransform is null) return;
        _dragGhostTransform.X = p.X + 14;
        _dragGhostTransform.Y = p.Y - 18;
    }

    private void RemoveDragGhost()
    {
        if (_dragGhost is not null)
        {
            WheelCanvas.Children.Remove(_dragGhost);
            _dragGhost = null;
            _dragGhostTransform = null;
        }
    }

    private void ClearOverflowPanel()
    {
        foreach (var el in _overflowCanvasChildren)
            WheelCanvas.Children.Remove(el);
        _overflowCanvasChildren.Clear();
        _overflowRows.Clear();
        _overflowPanelRect = Rect.Empty;
        _overflowHoverIndex = -1;
    }

    private void BuildOverflowPanel()
    {
        if (_tracker is null || _tracker.Overflow.Count == 0) return;

        const double rowH = 46;
        const double panelW = 220;
        const double gap = 16;

        double panelX = _cx - _outerRadius - gap - panelW;
        double totalH = _tracker.Overflow.Count * rowH;
        double panelY = Math.Max(4, Math.Min(_cy - totalH / 2.0, Height - totalH - 4));

        _overflowPanelRect = new Rect(panelX, panelY, panelW, totalH);

        // Glass-style background
        var bg = new Border
        {
            Width = panelW,
            Height = totalH,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x10, 0x16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(bg, panelX);
        Canvas.SetTop(bg, panelY);
        WheelCanvas.Children.Add(bg);
        _overflowCanvasChildren.Add(bg);

        int count = _tracker.Overflow.Count;
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var tw = _tracker.Overflow[i];

            var icon = new Image
            {
                Width = 28, Height = 28,
                Source = WindowActivator.GetIconForWindow(tw.Handle, tw.ProcessPath),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
                IsHitTestVisible = false
            };

            var titleText = new TextBlock
            {
                Text = tw.Title,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                IsHitTestVisible = false
            };

            var dock = new DockPanel { LastChildFill = true, IsHitTestVisible = false };
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(titleText);

            var rowBg = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
            var cr = count == 1 ? new CornerRadius(10)
                   : i == 0     ? new CornerRadius(10, 10, 2, 2)
                   : i == count - 1 ? new CornerRadius(2, 2, 10, 10)
                   : new CornerRadius(2);

            var row = new Border
            {
                Width = panelW,
                Height = rowH,
                CornerRadius = cr,
                Background = rowBg,
                Child = dock,
                Cursor = Cursors.Hand
            };

            row.MouseEnter += (_, _) =>
            {
                if (_isDragging) return;
                if (_hoverSlot >= 0) UpdateHover(-1);
                rowBg.Color = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
                _overflowHoverIndex = idx;
            };
            row.MouseLeave += (_, _) =>
            {
                if (_isDragging) return;
                if (_overflowHoverIndex == idx)
                {
                    rowBg.Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
                    _overflowHoverIndex = -1;
                }
            };
            // Click/drag is handled by the window's OnMouseDown/OnMouseUp via HitTestOverflowRow.

            Canvas.SetLeft(row, panelX);
            Canvas.SetTop(row, panelY + i * rowH);
            WheelCanvas.Children.Add(row);
            _overflowCanvasChildren.Add(row);
            _overflowRows.Add(row);
        }
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

    /// <summary>
    /// Highlight the wheel slot or overflow row that owns the given window handle.
    /// Used to pre-select the previously active window when the wheel first opens.
    /// Falls back to AdvanceSelection(1) if the handle is not found.
    /// </summary>
    public void PreSelectHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero || _tracker is null) { AdvanceSelection(1); return; }

        for (int i = 0; i < SlotCount; i++)
        {
            if (_tracker.Slots[i]?.Handle == handle)
            {
                _defaultSlot = i;
                if (_visuals[i]?.Highlight is { } h)
                    AnimateHighlight(h, 0x28);
                return;
            }
        }

        var overflow = _tracker.Overflow;
        for (int i = 0; i < overflow.Count; i++)
        {
            if (overflow[i].Handle == handle)
            {
                _overflowHoverIndex = i;
                if (i < _overflowRows.Count && _overflowRows[i].Background is SolidColorBrush brush)
                    brush.Color = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
                return;
            }
        }

        AdvanceSelection(1);
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
