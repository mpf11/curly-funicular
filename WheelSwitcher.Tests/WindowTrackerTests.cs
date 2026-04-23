using System;
using WheelSwitcher;
using Xunit;

namespace WheelSwitcher.Tests;

public class WindowTrackerTests
{
    private static TrackedWindow MakeWindow(int handle, string title = "w") =>
        new TrackedWindow { Handle = (IntPtr)handle, Title = title };

    private static WindowTracker MakeTracker(IntPtr selfHwnd = default)
    {
        var t = new WindowTracker(selfHwnd);
        return t;
    }

    // ---- SwapSlots ----

    [Fact]
    public void SwapSlots_ExchangesWindowsAndMappings()
    {
        var t = MakeTracker();
        var a = MakeWindow(1, "A");
        var b = MakeWindow(2, "B");
        t.ForceSlot(0, a);
        t.ForceSlot(3, b);

        t.SwapSlots(0, 3);

        Assert.Equal(b, t.Slots[0]);
        Assert.Equal(a, t.Slots[3]);
        Assert.Equal(0, b.Slot);
        Assert.Equal(3, a.Slot);
    }

    [Fact]
    public void SwapSlots_SameIndex_IsNoOp()
    {
        var t = MakeTracker();
        var a = MakeWindow(1, "A");
        t.ForceSlot(2, a);

        t.SwapSlots(2, 2);

        Assert.Equal(a, t.Slots[2]);
    }

    [Fact]
    public void SwapSlots_WithEmptySlot_MovesWindowAndLeavesEmpty()
    {
        var t = MakeTracker();
        var a = MakeWindow(1, "A");
        t.ForceSlot(1, a);

        t.SwapSlots(1, 5);

        Assert.Null(t.Slots[1]);
        Assert.Equal(a, t.Slots[5]);
        Assert.Equal(5, a.Slot);
    }

    // ---- SwapSlotWithOverflow ----

    [Fact]
    public void SwapSlotWithOverflow_ExchangesWindowsBetweenWheelAndOverflow()
    {
        var t = MakeTracker();
        var wheel = MakeWindow(10, "Wheel");
        var over = MakeWindow(20, "Over");
        t.ForceSlot(2, wheel);
        t.ForceOverflow(over);

        t.SwapSlotWithOverflow(2, 0);

        Assert.Equal(over, t.Slots[2]);
        Assert.Equal(2, over.Slot);
        // The old wheel window should now be in overflow at index 0
        Assert.Equal(wheel, t.Overflow[0]);
    }

    [Fact]
    public void SwapSlotWithOverflow_EmptySlot_PromotesOverflowAndShrinksList()
    {
        var t = MakeTracker();
        var over = MakeWindow(20, "Over");
        t.ForceOverflow(over);

        t.SwapSlotWithOverflow(4, 0);

        Assert.Equal(over, t.Slots[4]);
        Assert.Empty(t.Overflow);
    }

    // ---- SwapOverflowItems ----

    [Fact]
    public void SwapOverflowItems_ExchangesPositions()
    {
        var t = MakeTracker();
        var x = MakeWindow(1, "X");
        var y = MakeWindow(2, "Y");
        t.ForceOverflow(x);
        t.ForceOverflow(y);

        t.SwapOverflowItems(0, 1);

        Assert.Equal(y, t.Overflow[0]);
        Assert.Equal(x, t.Overflow[1]);
    }

    [Fact]
    public void SwapOverflowItems_SameIndex_IsNoOp()
    {
        var t = MakeTracker();
        var x = MakeWindow(1, "X");
        t.ForceOverflow(x);

        t.SwapOverflowItems(0, 0);

        Assert.Equal(x, t.Overflow[0]);
    }
}
