using System;

namespace WheelSwitcher;

/// <summary>
/// Pure math for the wheel layout — no WPF dependencies, making it unit-testable.
/// </summary>
internal static class WheelGeometry
{
    internal const int SlotCount = 8;
    internal const double SliceSpanDeg = 360.0 / SlotCount;

    /// <summary>Angle in degrees for the leading edge of slot i (the boundary BEFORE it).</summary>
    internal static double SliceBoundaryAngleDeg(int i) => -90.0 - SliceSpanDeg / 2.0 + i * SliceSpanDeg;

    /// <summary>Center angle (degrees) of slot i.</summary>
    internal static double SliceCenterAngleDeg(int i) => -90.0 + i * SliceSpanDeg;

    /// <summary>
    /// Map a point (expressed as dx, dy relative to wheel centre) to a slot index.
    /// Returns -1 if inside the hub (dist &lt; innerRadius).
    /// When requireInBounds is true (drag-drop), also returns -1 outside the outer radius.
    /// When requireInBounds is false (hover), the nearest slice is returned for any distance.
    /// </summary>
    internal static int PointToSlot(double dx, double dy, double innerRadius, double outerRadius, bool requireInBounds = true)
    {
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < innerRadius) return -1;
        if (requireInBounds && dist > outerRadius) return -1;

        double angDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double fromTop = (angDeg + 90.0 + 360.0) % 360.0;
        double shifted = (fromTop + SliceSpanDeg / 2.0) % 360.0;
        return (int)(shifted / SliceSpanDeg) % SlotCount;
    }
}
