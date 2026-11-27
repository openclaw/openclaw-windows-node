namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupWindowSizingTests
{
    [Theory]
    [InlineData(96u, 960, 640, 1120, 720)]
    [InlineData(144u, 1440, 960, 1680, 1080)]
    [InlineData(192u, 1920, 1280, 2240, 1440)]
    [InlineData(97u, 971, 647, 1132, 728)]
    public void DpiProjection_PreservesDipMinimumAndDefault(
        uint dpi, int width, int height, int initialWidth, int initialHeight)
    {
        Assert.Equal((width, height), SetupWindowSizing.MinimumPixels(dpi));
        Assert.Equal((initialWidth, initialHeight), SetupWindowSizing.InitialPixels(dpi));
        Assert.True(width >= SetupWindowSizing.MinimumWidth * dpi / 96d);
        Assert.True(height >= SetupWindowSizing.MinimumHeight * dpi / 96d);
    }

    [Fact]
    public void InvalidDpi_IsNotSilentlyTreatedAsUnscaled()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupWindowSizing.MinimumPixels(0));
        Assert.Throws<OverflowException>(() => SetupWindowSizing.MinimumPixels(uint.MaxValue));
    }

    [Theory]
    // 1080p at 150% leaves about 1032 physical pixels of work area under the taskbar.
    [InlineData(1680, 1080, 1920, 1032, 1440, 960, 1680, 1032)]
    [InlineData(1120, 720, 2560, 1400, 960, 640, 1120, 720)]
    // FitInitialPixels keeps the minimum it is given; the controller passes FitMinimumPixels first.
    [InlineData(1120, 720, 800, 600, 960, 640, 960, 640)]
    // An unknown work area keeps the requested size.
    [InlineData(1120, 720, 0, 0, 960, 640, 1120, 720)]
    public void InitialSize_FitsTheWorkAreaWithoutGoingBelowMinimum(
        int initialWidth, int initialHeight, int workWidth, int workHeight,
        int minimumWidth, int minimumHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SetupWindowSizing.FitInitialPixels(
            (initialWidth, initialHeight), (workWidth, workHeight), (minimumWidth, minimumHeight)));
    }

    [Theory]
    [InlineData(960, 640, 2560, 1400, 960, 640)]
    // 1366x768 at 125%: the 800-pixel minimum height would push the actions under the taskbar.
    [InlineData(1200, 800, 1366, 728, 1200, 728)]
    [InlineData(1680, 1120, 1280, 1032, 1280, 1032)]
    // An unknown work area keeps the design minimum.
    [InlineData(960, 640, 0, 0, 960, 640)]
    public void Minimum_IsCappedAtTheWorkArea(
        int minimumWidth, int minimumHeight, int workWidth, int workHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SetupWindowSizing.FitMinimumPixels(
            (minimumWidth, minimumHeight), (workWidth, workHeight)));
    }

    [Theory]
    [InlineData(1120, 472)]
    [InlineData(960, 405)]
    [InlineData(800, 380)]
    [InlineData(1920, 872)]
    public void CompanionPane_KeepsTheDesignProportionWithinBounds(double windowWidth, double expected)
    {
        Assert.Equal(expected, SetupWindowSizing.CompanionPaneWidth(windowWidth));
    }
}
