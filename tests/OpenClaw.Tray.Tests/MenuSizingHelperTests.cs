using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit tests for MenuSizingHelper — CalculateWindowHeight and ConvertPixelsToViewUnits.
/// </summary>
public class MenuSizingHelperTests
{
    // ── ConvertPixelsToViewUnits ────────────────────────────────────

    [Fact]
    public void ConvertPixels_ZeroPixels_ReturnsZero()
    {
        Assert.Equal(0, MenuSizingHelper.ConvertPixelsToViewUnits(0, 96));
    }

    [Fact]
    public void ConvertPixels_NegativePixels_ReturnsZero()
    {
        Assert.Equal(0, MenuSizingHelper.ConvertPixelsToViewUnits(-100, 96));
    }

    [Fact]
    public void ConvertPixels_StandardDpi96_ReturnsSameValue()
    {
        // At 100% scale (96 DPI) view units == pixels
        Assert.Equal(200, MenuSizingHelper.ConvertPixelsToViewUnits(200, 96));
        Assert.Equal(1080, MenuSizingHelper.ConvertPixelsToViewUnits(1080, 96));
    }

    [Fact]
    public void ConvertPixels_ZeroDpi_FallsBackTo96()
    {
        // DPI 0 is treated as 96
        Assert.Equal(200, MenuSizingHelper.ConvertPixelsToViewUnits(200, 0));
    }

    [Fact]
    public void ConvertPixels_Dpi192_HalvesValue()
    {
        // 200% scale: 1200 physical px → 600 view units
        Assert.Equal(600, MenuSizingHelper.ConvertPixelsToViewUnits(1200, 192));
    }

    [Fact]
    public void ConvertPixels_Dpi144_FloorsDivision()
    {
        // 150% scale: 100 px → floor(100 * 96 / 144) = floor(66.6) = 66
        Assert.Equal(66, MenuSizingHelper.ConvertPixelsToViewUnits(100, 144));
    }

    [Fact]
    public void ConvertPixels_HighDpi_ReturnsAtLeastOne()
    {
        // Even with extremely high DPI, result must be at least 1
        Assert.Equal(1, MenuSizingHelper.ConvertPixelsToViewUnits(1, 960));
    }

    [Fact]
    public void ConvertPixels_Dpi120_CorrectScaling()
    {
        // 125% scale: 1000 px → floor(1000 * 96 / 120) = floor(800) = 800
        Assert.Equal(800, MenuSizingHelper.ConvertPixelsToViewUnits(1000, 120));
    }

    // ── CalculateWindowHeight ───────────────────────────────────────

    [Fact]
    public void CalcHeight_ContentSmallerThanWorkArea_ReturnsContent()
    {
        // Normal case: content fits, no minimum constraint
        Assert.Equal(300, MenuSizingHelper.CalculateWindowHeight(300, 1000, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_ContentLargerThanWorkArea_ClampsToWorkArea()
    {
        // Content too tall for screen → clamp to work area
        Assert.Equal(800, MenuSizingHelper.CalculateWindowHeight(1200, 800, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_ContentSmallerThanMinimum_ReturnsMinimum()
    {
        // Small content → at least minimumHeight
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(50, 1000, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_DefaultMinimumIs100()
    {
        // Verify default parameter value
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(0, 1000));
    }

    [Fact]
    public void CalcHeight_NegativeContent_TreatedAsZero()
    {
        // Negative content → clamped to 0, result is minimumHeight
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(-50, 1000, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_MinimumLessThanOne_SetToOne()
    {
        // minimumHeight < 1 → forced to 1
        Assert.Equal(1, MenuSizingHelper.CalculateWindowHeight(0, 1000, minimumHeight: 0));
        Assert.Equal(1, MenuSizingHelper.CalculateWindowHeight(0, 1000, minimumHeight: -50));
    }

    [Fact]
    public void CalcHeight_ZeroWorkArea_ReturnsMaxContentMinimum()
    {
        // workAreaHeight <= 0: can't constrain, return max(content, minimum)
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(50, 0, minimumHeight: 100));
        Assert.Equal(200, MenuSizingHelper.CalculateWindowHeight(200, 0, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_NegativeWorkArea_ReturnsMaxContentMinimum()
    {
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(50, -200, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_MinimumLargerThanWorkArea_ClampsMinimumToWorkArea()
    {
        // minimumHeight (200) > workAreaHeight (100) → result capped at workAreaHeight
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(50, 100, minimumHeight: 200));
    }

    [Fact]
    public void CalcHeight_ContentEqualsWorkArea_ReturnsWorkArea()
    {
        Assert.Equal(800, MenuSizingHelper.CalculateWindowHeight(800, 800, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_ContentEqualsMinimum_ReturnsMinimum()
    {
        Assert.Equal(100, MenuSizingHelper.CalculateWindowHeight(100, 1000, minimumHeight: 100));
    }

    [Fact]
    public void CalcHeight_TypicalTrayMenuScenario_FitsInWorkArea()
    {
        // Typical: 400px content, 1040px work area (1080 - 40px taskbar), 100px min
        var height = MenuSizingHelper.CalculateWindowHeight(400, 1040, minimumHeight: 100);
        Assert.Equal(400, height);
        Assert.True(height <= 1040);
    }

    [Fact]
    public void CalcHeight_OversizedMenu_ClampsToWorkArea()
    {
        // Lots of sessions → menu taller than screen
        var height = MenuSizingHelper.CalculateWindowHeight(2000, 1040, minimumHeight: 100);
        Assert.Equal(1040, height);
    }
}
