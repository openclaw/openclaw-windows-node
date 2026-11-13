using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PageResponsiveLayoutTests
{
    [Theory]
    [InlineData(0, PageWidthTier.Narrow, 0)]
    [InlineData(480, PageWidthTier.Narrow, 440)]
    [InlineData(719.99, PageWidthTier.Narrow, 679.99)]
    [InlineData(719.99994, PageWidthTier.Medium, 656)]
    [InlineData(720, PageWidthTier.Medium, 656)]
    [InlineData(999.99, PageWidthTier.Medium, 935.99)]
    [InlineData(999.99994, PageWidthTier.Wide, 936)]
    [InlineData(1000, PageWidthTier.Wide, 936)]
    [InlineData(1600, PageWidthTier.Wide, 1200)]
    public void HostWidth_SelectsTierAndBoundedContent(double width, PageWidthTier tier, double content)
    {
        Assert.Equal(tier, PageResponsiveLayout.TierFor(width));
        Assert.Equal(content, PageResponsiveLayout.ContentWidth(width), 2);
    }

    [Theory]
    [InlineData(656, false)]
    [InlineData(935.99, false)]
    [InlineData(935.99994, true)]
    [InlineData(936, true)]
    [InlineData(1200, true)]
    public void BodyColumns_RequireWideContent(double contentWidth, bool expected) =>
        Assert.Equal(expected, PageResponsiveLayout.UseTwoColumns(contentWidth));

    [Theory]
    [InlineData(320, 1)]
    [InlineData(650.5, 1)]
    [InlineData(651.99, 1)]
    [InlineData(651.99994, 2)]
    [InlineData(652, 2)]
    [InlineData(983.99, 2)]
    [InlineData(984, 3)]
    public void TileColumns_NeverRoundUpBeforeAnotherTileFits(double width, int expected)
    {
        Assert.Equal(expected, PageResponsiveLayout.TileColumns(width, 320, maxColumns: 4));
        Assert.True(PageResponsiveLayout.TileWidth(PageResponsiveLayout.Snap(width), expected) >= 320);
    }

    [Fact]
    public void TileColumns_HonorsCapAndRejectsInvalidMinimum()
    {
        Assert.Equal(2, PageResponsiveLayout.TileColumns(1400, 320, maxColumns: 2));
        Assert.Equal(1, PageResponsiveLayout.TileColumns(0, 320));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageResponsiveLayout.TileColumns(480, 0));
    }
}
