using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class MenuPositionerTests
{
    // Standard 1920x1080 work area with taskbar at bottom (~40px)
    private const int WorkLeft = 0;
    private const int WorkTop = 0;
    private const int WorkRight = 1920;
    private const int WorkBottom = 1040;

    private const int MenuWidth = 280;
    private const int MenuHeight = 400;

    [Fact]
    public void CursorNearBottomRight_ShowsAboveAndLeft()
    {
        // Cursor near bottom-right (typical tray icon click)
        var (x, y) = MenuPositioner.CalculatePosition(
            1900, 1030, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);

        // X should be clamped so menu fits within work area
        Assert.True(x + MenuWidth <= WorkRight, $"Menu right edge {x + MenuWidth} exceeds work area {WorkRight}");
        // Y should be above cursor
        Assert.True(y < 1030, $"Menu Y {y} should be above cursor 1030");
        Assert.True(y >= WorkTop, $"Menu Y {y} below work area top {WorkTop}");
    }

    [Fact]
    public void CursorNearTopLeft_ShowsBelow()
    {
        // Cursor near top-left: can't show above, should show below
        var (x, y) = MenuPositioner.CalculatePosition(
            50, 10, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);

        // Y should be below cursor
        Assert.True(y > 10, $"Menu Y {y} should be below cursor 10");
        Assert.True(y + MenuHeight <= WorkBottom, $"Menu bottom {y + MenuHeight} exceeds work bottom {WorkBottom}");
        Assert.True(x >= WorkLeft);
    }

    [Fact]
    public void CursorInCenter_ShowsAboveByDefault()
    {
        // Cursor in center of screen: enough space above
        var (x, y) = MenuPositioner.CalculatePosition(
            960, 600, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);

        // Prefers above cursor
        Assert.True(y < 600, $"Menu Y {y} should be above cursor 600");
        Assert.True(y >= WorkTop);
    }

    [Fact]
    public void WorkAreaSmallerThanMenu_ClampsToWorkArea()
    {
        // Tiny work area
        var (x, y) = MenuPositioner.CalculatePosition(
            100, 100, 500, 500,
            0, 0, 200, 200);

        Assert.True(x >= 0, "X must be >= 0");
        Assert.True(y >= 0, "Y must be >= 0");
    }

    [Fact]
    public void TaskbarAtBottom_TypicalScenario()
    {
        // Cursor in taskbar area (below work area bottom)
        // work bottom = 1040, cursor at 1060 (in taskbar)
        var (x, y) = MenuPositioner.CalculatePosition(
            1800, 1060, MenuWidth, MenuHeight,
            0, 0, 1920, 1040);

        // Menu should be fully within work area
        Assert.True(y >= 0);
        Assert.True(y + MenuHeight <= 1040,
            $"Menu bottom edge {y + MenuHeight} should not exceed work area bottom 1040");
    }

    [Fact]
    public void OversizedMenuHeight_IsClampedToWorkAreaHeight()
    {
        const int oversizedMenuHeight = 1200;
        var visibleHeight = MenuSizingHelper.CalculateWindowHeight(
            oversizedMenuHeight,
            WorkBottom - WorkTop);

        Assert.Equal(WorkBottom - WorkTop, visibleHeight);
    }

    [Fact]
    public void PixelHeight_IsConvertedToViewUnits_UsingDpi()
    {
        var viewHeight = MenuSizingHelper.ConvertPixelsToViewUnits(1200, 192);
        Assert.Equal(600, viewHeight);
    }

    [Fact]
    public void OversizedMenuNearTray_WithClampedHeight_RemainsFullyVisibleWithinWorkArea()
    {
        // Regression test for the tray popup overflow bug:
        // the popup height must be constrained before positioning so the
        // ScrollViewer can handle overflow within the visible work area.
        const int oversizedMenuHeight = 1200;
        var visibleHeight = MenuSizingHelper.CalculateWindowHeight(
            oversizedMenuHeight,
            WorkBottom - WorkTop);

        var (_, y) = MenuPositioner.CalculatePosition(
            1800, 1060, MenuWidth, visibleHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);

        Assert.True(y >= WorkTop, $"Menu Y {y} should not be above the work area top {WorkTop}");
        Assert.True(
            y + visibleHeight <= WorkBottom,
            $"Menu bottom edge {y + visibleHeight} should not exceed work area bottom {WorkBottom}");
    }

    [Fact]
    public void TaskbarAtRight_Scenario()
    {
        // Taskbar on right side: work area is narrower
        int workRight = 1880;
        var (x, y) = MenuPositioner.CalculatePosition(
            1870, 500, MenuWidth, MenuHeight,
            0, 0, workRight, 1080);

        Assert.True(x + MenuWidth <= workRight,
            $"Menu right edge {x + MenuWidth} should not exceed work area {workRight}");
    }

    [Fact]
    public void MarginIsApplied()
    {
        // With a large margin
        var (_, y1) = MenuPositioner.CalculatePosition(
            500, 600, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom, margin: 0);
        var (_, y2) = MenuPositioner.CalculatePosition(
            500, 600, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom, margin: 50);

        // Larger margin -> menu is higher (further from cursor)
        Assert.True(y2 < y1, "Larger margin should place menu further above cursor");
    }

    [Fact]
    public void DefaultMarginIs8()
    {
        var (_, yDefault) = MenuPositioner.CalculatePosition(
            500, 600, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);
        var (_, yExplicit) = MenuPositioner.CalculatePosition(
            500, 600, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom, margin: 8);

        Assert.Equal(yExplicit, yDefault);
    }

    [Fact]
    public void XClampedWithinWorkArea()
    {
        // Cursor far to the right
        var (x, _) = MenuPositioner.CalculatePosition(
            2000, 500, MenuWidth, MenuHeight,
            WorkLeft, WorkTop, WorkRight, WorkBottom);

        Assert.True(x >= WorkLeft);
        Assert.True(x + MenuWidth <= WorkRight);
    }

    [Fact]
    public void XClampedToLeftBound()
    {
        // Cursor far to the left (negative-ish via offset work area)
        var (x, _) = MenuPositioner.CalculatePosition(
            50, 500, MenuWidth, MenuHeight,
            100, WorkTop, WorkRight, WorkBottom);

        Assert.True(x >= 100, "X should not be less than work area left");
    }
}
