using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class MenuDisplayHelperTests
{
    #region GetStatusIcon

    [Theory]
    [InlineData(ConnectionStatus.Connected, "✅")]
    [InlineData(ConnectionStatus.Connecting, "🔄")]
    [InlineData(ConnectionStatus.Error, "❌")]
    [InlineData(ConnectionStatus.Disconnected, "⚪")]
    public void GetStatusIcon_ReturnsExpectedEmoji(ConnectionStatus status, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetStatusIcon(status));
    }

    [Fact]
    public void GetStatusIcon_UndefinedEnumValue_ReturnsFallback()
    {
        Assert.Equal("⚪", MenuDisplayHelper.GetStatusIcon((ConnectionStatus)999));
    }

    #endregion

    #region GetChannelStatusIcon

    [Theory]
    [InlineData("ok", "🟢")]
    [InlineData("connected", "🟢")]
    [InlineData("running", "🟢")]
    [InlineData("active", "🟢")]
    [InlineData("ready", "🟢")]
    [InlineData("stopped", "🟡")]
    [InlineData("idle", "🟡")]
    [InlineData("paused", "🟡")]
    [InlineData("configured", "🟡")]
    [InlineData("pending", "🟡")]
    [InlineData("connecting", "🟡")]
    [InlineData("reconnecting", "🟡")]
    [InlineData("error", "🔴")]
    [InlineData("disconnected", "🔴")]
    [InlineData("failed", "🔴")]
    public void GetChannelStatusIcon_KnownStatuses(string status, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetChannelStatusIcon(status));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("Connected")]
    [InlineData("RUNNING")]
    [InlineData("Error")]
    [InlineData("Connecting")]
    [InlineData("RECONNECTING")]
    public void GetChannelStatusIcon_CaseInsensitive(string status)
    {
        // Should not return the neutral fallback
        Assert.NotEqual("⚪", MenuDisplayHelper.GetChannelStatusIcon(status));
    }

    [Theory]
    [InlineData(null, "⚪")]
    [InlineData("", "⚪")]
    [InlineData("unknown", "🔴")]
    [InlineData("something-new", "🔴")]
    [InlineData("timeout", "🔴")]
    public void GetChannelStatusIcon_UnknownOrEmpty(string? status, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetChannelStatusIcon(status));
    }

    #endregion

    #region TruncateText

    [Fact]
    public void TruncateText_ShortText_ReturnsUnchanged()
    {
        Assert.Equal("hello", MenuDisplayHelper.TruncateText("hello", 10));
    }

    [Fact]
    public void TruncateText_ExactLength_ReturnsUnchanged()
    {
        Assert.Equal("12345", MenuDisplayHelper.TruncateText("12345", 5));
    }

    [Fact]
    public void TruncateText_OverLength_Truncates()
    {
        var result = MenuDisplayHelper.TruncateText("Hello, World!", 6);
        Assert.Equal("Hello…", result);
        Assert.Equal(6, result.Length);
    }

    [Fact]
    public void TruncateText_DefaultMaxLength_Is96()
    {
        var input = new string('x', 100);
        var result = MenuDisplayHelper.TruncateText(input);
        Assert.Equal(96, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncateText_Null_ReturnsEmpty()
    {
        Assert.Equal("", MenuDisplayHelper.TruncateText(null));
    }

    [Fact]
    public void TruncateText_Empty_ReturnsEmpty()
    {
        Assert.Equal("", MenuDisplayHelper.TruncateText(""));
    }

    [Fact]
    public void TruncateText_Whitespace_ReturnsAsIs()
    {
        Assert.Equal("   ", MenuDisplayHelper.TruncateText("   "));
    }

    #endregion

    #region FormatProviderSummary

    [Theory]
    [InlineData(0, "0 providers active")]
    [InlineData(1, "1 provider active")]
    [InlineData(2, "2 providers active")]
    [InlineData(10, "10 providers active")]
    public void FormatProviderSummary_FormatsCorrectly(int count, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.FormatProviderSummary(count));
    }

    #endregion

    #region GetNextToggleValue

    [Theory]
    [InlineData("on", "off")]
    [InlineData("off", "on")]
    [InlineData("ON", "off")]
    [InlineData("On", "off")]
    [InlineData("OFF", "on")]
    [InlineData("Off", "on")]
    public void GetNextToggleValue_TogglesCorrectly(string current, string expected)
    {
        Assert.Equal(expected, MenuDisplayHelper.GetNextToggleValue(current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void GetNextToggleValue_NullEmptyOrUnknown_ReturnsOn(string? current)
    {
        Assert.Equal("on", MenuDisplayHelper.GetNextToggleValue(current));
    }

    #endregion
}
