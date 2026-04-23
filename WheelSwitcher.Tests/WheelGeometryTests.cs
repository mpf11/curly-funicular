using System;
using WheelSwitcher;
using Xunit;

namespace WheelSwitcher.Tests;

public class WheelGeometryTests
{
    private const double Inner = 30.0;
    private const double Outer = 200.0;

    // ---- 8-direction mapping ----

    // Slot layout (12-o'clock = 0, clockwise):
    //   0 = N, 1 = NE, 2 = E, 3 = SE, 4 = S, 5 = SW, 6 = W, 7 = NW
    [Theory]
    [InlineData(0, -100, 0)] // straight up    → slot 0 (N)
    [InlineData(100, 0, 2)] // straight right  → slot 2 (E)
    [InlineData(0, 100, 4)] // straight down   → slot 4 (S)
    [InlineData(-100, 0, 6)] // straight left   → slot 6 (W)
    [InlineData(80, -80, 1)] // NE diagonal     → slot 1
    [InlineData(80, 80, 3)] // SE diagonal     → slot 3
    [InlineData(-80, 80, 5)] // SW diagonal     → slot 5
    [InlineData(-80, -80, 7)] // NW diagonal     → slot 7
    public void PointToSlot_CardinalAndDiagonal(double dx, double dy, int expected)
    {
        int slot = WheelGeometry.PointToSlot(dx, dy, Inner, Outer, requireInBounds: true);
        Assert.Equal(expected, slot);
    }

    [Fact]
    public void PointToSlot_InsideHub_ReturnsMinusOne()
    {
        int slot = WheelGeometry.PointToSlot(0, Inner * 0.5, Inner, Outer);
        Assert.Equal(-1, slot);
    }

    [Fact]
    public void PointToSlot_ExactlyAtInnerRadius_ReturnsSlot()
    {
        // At exactly innerRadius distance the point is on the boundary — should resolve to a slot.
        int slot = WheelGeometry.PointToSlot(0, -Inner, Inner, Outer);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void PointToSlot_OutsideOuter_StrictMode_ReturnsMinusOne()
    {
        int slot = WheelGeometry.PointToSlot(0, -(Outer + 50), Inner, Outer, requireInBounds: true);
        Assert.Equal(-1, slot);
    }

    [Fact]
    public void PointToSlot_OutsideOuter_LooseMode_ReturnsSlot()
    {
        // Loose mode (hover): even beyond the wheel edge we still get a valid slot.
        int slot = WheelGeometry.PointToSlot(
            0,
            -(Outer + 50),
            Inner,
            Outer,
            requireInBounds: false
        );
        Assert.Equal(0, slot);
    }

    // ---- Boundary angles ----

    [Fact]
    public void SliceBoundaryAngleDeg_Slot0_Is_Minus112_5()
    {
        Assert.Equal(-112.5, WheelGeometry.SliceBoundaryAngleDeg(0), precision: 6);
    }

    [Fact]
    public void SliceBoundaryAngleDeg_AdvancesBy45Each_Slot()
    {
        for (int i = 1; i < WheelGeometry.SlotCount; i++)
        {
            double prev = WheelGeometry.SliceBoundaryAngleDeg(i - 1);
            double curr = WheelGeometry.SliceBoundaryAngleDeg(i);
            Assert.Equal(WheelGeometry.SliceSpanDeg, curr - prev, precision: 6);
        }
    }

    [Fact]
    public void SliceCenterAngleDeg_Slot0_Is_Minus90()
    {
        Assert.Equal(-90.0, WheelGeometry.SliceCenterAngleDeg(0), precision: 6);
    }

    // ---- Slot count / span constants ----

    [Fact]
    public void SlotCount_Is8() => Assert.Equal(8, WheelGeometry.SlotCount);

    [Fact]
    public void SliceSpanDeg_Is45() => Assert.Equal(45.0, WheelGeometry.SliceSpanDeg, precision: 6);
}
