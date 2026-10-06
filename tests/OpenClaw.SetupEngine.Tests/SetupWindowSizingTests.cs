namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupWindowSizingTests
{
    [Theory]
    [InlineData(96u, 720, 560, 1120, 720)]
    [InlineData(144u, 1080, 840, 1680, 1080)]
    [InlineData(192u, 1440, 1120, 2240, 1440)]
    [InlineData(97u, 728, 566, 1132, 728)]
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
}
