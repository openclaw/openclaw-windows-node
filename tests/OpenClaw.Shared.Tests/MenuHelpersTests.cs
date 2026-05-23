using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class MenuDisplayHelperTests
{
    // ── GetStatusIcon ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConnectionStatus.Connected, "✅")]
    [InlineData(ConnectionStatus.Connecting, "🔄")]
    [InlineData(ConnectionStatus.Error, "❌")]
    public void GetStatusIcon_KnownStatus_ReturnsExpectedIcon(ConnectionStatus status, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetStatusIcon(status));
    }

    [Fact]
    public void GetStatusIcon_UnknownStatus_ReturnsNeutralIcon()
    {
        var icon = MenuDisplayHelper.GetStatusIcon((ConnectionStatus)999);
        Assert.Equal("⚪", icon);
    }

    // ── GetChannelStatusIcon ─────────────────────────────────────────────────

    [Theory]
    [InlineData("active", "🟢")]
    [InlineData("ACTIVE", "🟢")]
    public void GetChannelStatusIcon_HealthyStatus_ReturnsGreen(string status, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetChannelStatusIcon(status));
    }

    [Fact]
    public void GetChannelStatusIcon_NullStatus_ReturnsNeutral()
    {
        Assert.Equal("⚪", MenuDisplayHelper.GetChannelStatusIcon(null));
    }

    [Fact]
    public void GetChannelStatusIcon_EmptyStatus_ReturnsNeutral()
    {
        Assert.Equal("⚪", MenuDisplayHelper.GetChannelStatusIcon(""));
    }

    [Fact]
    public void GetChannelStatusIcon_UnknownStatus_ReturnsRed()
    {
        Assert.Equal("🔴", MenuDisplayHelper.GetChannelStatusIcon("unknown-bad-status"));
    }

    // ── TruncateText ─────────────────────────────────────────────────────────

    [Fact]
    public void TruncateText_ShortText_ReturnsUnchanged()
    {
        Assert.Equal("hello", MenuDisplayHelper.TruncateText("hello"));
    }

    [Fact]
    public void TruncateText_NullText_ReturnsEmptyString()
    {
        Assert.Equal("", MenuDisplayHelper.TruncateText(null));
    }

    [Fact]
    public void TruncateText_WhitespaceOnly_ReturnsWhitespace()
    {
        Assert.Equal("   ", MenuDisplayHelper.TruncateText("   "));
    }

    [Fact]
    public void TruncateText_TextAtExactMaxLength_ReturnsUnchanged()
    {
        var text = new string('a', 96);
        Assert.Equal(text, MenuDisplayHelper.TruncateText(text));
    }

    [Fact]
    public void TruncateText_TextExceedsMaxLength_TruncatesWithEllipsis()
    {
        var text = new string('a', 200);
        var result = MenuDisplayHelper.TruncateText(text);
        Assert.EndsWith("…", result);
        Assert.Equal(96, result.Length);
    }

    [Fact]
    public void TruncateText_CustomMaxLength_TruncatesAtCustomLength()
    {
        var result = MenuDisplayHelper.TruncateText("hello world", maxLength: 8);
        Assert.Equal(8, result.Length);
        Assert.EndsWith("…", result);
    }

    // ── FormatProviderSummary ────────────────────────────────────────────────

    [Fact]
    public void FormatProviderSummary_One_UsesSingular()
    {
        Assert.Equal("1 provider active", MenuDisplayHelper.FormatProviderSummary(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void FormatProviderSummary_NotOne_UsesPlural(int count)
    {
        var result = MenuDisplayHelper.FormatProviderSummary(count);
        Assert.EndsWith("providers active", result);
    }

    // ── GetNextToggleValue ───────────────────────────────────────────────────

    [Theory]
    [InlineData("on", "off")]
    [InlineData("ON", "off")]
    [InlineData("On", "off")]
    public void GetNextToggleValue_OnVariants_ReturnsOff(string current, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetNextToggleValue(current));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("other")]
    public void GetNextToggleValue_NonOn_ReturnsOn(string? current)
    {
        Assert.Equal("on", MenuDisplayHelper.GetNextToggleValue(current));
    }
}

public class MenuSizingHelperTests
{
    // ── ConvertPixelsToViewUnits ─────────────────────────────────────────────

    [Fact]
    public void ConvertPixelsToViewUnits_96Dpi_ReturnsSamePixels()
    {
        Assert.Equal(100, MenuSizingHelper.ConvertPixelsToViewUnits(100, 96));
    }

    [Fact]
    public void ConvertPixelsToViewUnits_192Dpi_ReturnsHalfPixels()
    {
        Assert.Equal(50, MenuSizingHelper.ConvertPixelsToViewUnits(100, 192));
    }

    [Fact]
    public void ConvertPixelsToViewUnits_ZeroPixels_ReturnsZero()
    {
        Assert.Equal(0, MenuSizingHelper.ConvertPixelsToViewUnits(0, 96));
    }

    [Fact]
    public void ConvertPixelsToViewUnits_NegativePixels_ReturnsZero()
    {
        Assert.Equal(0, MenuSizingHelper.ConvertPixelsToViewUnits(-10, 96));
    }

    [Fact]
    public void ConvertPixelsToViewUnits_ZeroDpi_UsesDefault96()
    {
        // dpi=0 falls back to 96, so 100px at 96dpi = 100 view units
        Assert.Equal(100, MenuSizingHelper.ConvertPixelsToViewUnits(100, 0));
    }

    [Fact]
    public void ConvertPixelsToViewUnits_SmallResult_ReturnsAtLeastOne()
    {
        // 1 pixel at 192 dpi → floor(0.5) = 0, but clamped to 1
        Assert.Equal(1, MenuSizingHelper.ConvertPixelsToViewUnits(1, 192));
    }

    // ── HasDpiOrScaleChanged ─────────────────────────────────────────────────

    [Fact]
    public void HasDpiOrScaleChanged_SameDpiAndScale_ReturnsFalse()
    {
        Assert.False(MenuSizingHelper.HasDpiOrScaleChanged(96, 1.0, 96, 1.0));
    }

    [Fact]
    public void HasDpiOrScaleChanged_DifferentDpi_ReturnsTrue()
    {
        Assert.True(MenuSizingHelper.HasDpiOrScaleChanged(96, 1.0, 192, 1.0));
    }

    [Fact]
    public void HasDpiOrScaleChanged_DifferentScale_ReturnsTrue()
    {
        Assert.True(MenuSizingHelper.HasDpiOrScaleChanged(96, 1.0, 96, 1.5));
    }

    [Fact]
    public void HasDpiOrScaleChanged_TinyScaleDifference_ReturnsFalse()
    {
        // Difference < tolerance (0.001) should be treated as the same
        Assert.False(MenuSizingHelper.HasDpiOrScaleChanged(96, 1.0, 96, 1.0 + 0.0001));
    }

    [Fact]
    public void HasDpiOrScaleChanged_ZeroPreviousDpi_NormalizesToDefault()
    {
        // Both zero → both normalised to 96 → same → no change
        Assert.False(MenuSizingHelper.HasDpiOrScaleChanged(0, 1.0, 0, 1.0));
    }

    [Fact]
    public void HasDpiOrScaleChanged_InvalidScale_NormalizesToOne()
    {
        // NaN/0/negative scale → normalised to 1.0 on both sides → no change
        Assert.False(MenuSizingHelper.HasDpiOrScaleChanged(96, double.NaN, 96, 0.0));
    }

    // ── CalculateWindowHeight ────────────────────────────────────────────────

    [Fact]
    public void CalculateWindowHeight_ContentFitsInWorkArea_ReturnsContentHeight()
    {
        Assert.Equal(300, MenuSizingHelper.CalculateWindowHeight(300, 900));
    }

    [Fact]
    public void CalculateWindowHeight_ContentExceedsWorkArea_ClampsToWorkArea()
    {
        Assert.Equal(900, MenuSizingHelper.CalculateWindowHeight(1000, 900));
    }

    [Fact]
    public void CalculateWindowHeight_ZeroContentHeight_ReturnsMinimum()
    {
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(0, 900));
    }

    [Fact]
    public void CalculateWindowHeight_NegativeContentHeight_TreatedAsZero()
    {
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(-50, 900));
    }

    [Fact]
    public void CalculateWindowHeight_ZeroWorkArea_ReturnsContentOrMinimum()
    {
        Assert.Equal(200, MenuSizingHelper.CalculateWindowHeight(200, 0));
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(50, 0));
    }

    [Fact]
    public void CalculateWindowHeight_WorkAreaSmallerThanMinimum_ClampsToWorkArea()
    {
        // minimumHeight default is 100, but work area is only 60 → clamp to work area
        Assert.Equal(60, MenuSizingHelper.CalculateWindowHeight(0, 60));
    }

    [Fact]
    public void CalculateWindowHeight_CustomMinimumHeight_IsRespected()
    {
        Assert.Equal(50, MenuSizingHelper.CalculateWindowHeight(0, 900, minimumHeight: 50));
    }
}
