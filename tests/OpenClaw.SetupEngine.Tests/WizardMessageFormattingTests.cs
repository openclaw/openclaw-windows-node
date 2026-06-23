namespace OpenClaw.SetupEngine.Tests;

public class WizardMessageFormattingTests
{
    [Fact]
    public void PlainText_ClassifiesAsText()
    {
        var segment = WizardMessageFormatting.ClassifyLine("Just some instructions.");
        Assert.Equal(WizardLineKind.Text, segment.Kind);
        Assert.Equal("Just some instructions.", segment.Text);
    }

    [Fact]
    public void Url_IsDetected_WithPrefixAndSuffix()
    {
        var segment = WizardMessageFormatting.ClassifyLine("Open https://example.com/auth to continue");
        Assert.Equal(WizardLineKind.Url, segment.Kind);
        Assert.Equal("https://example.com/auth", segment.Highlight);
        Assert.Equal("Open ", segment.Prefix);
        Assert.Equal(" to continue", segment.Suffix);
    }

    [Fact]
    public void Url_TrailingPunctuation_IsTrimmed()
    {
        var segment = WizardMessageFormatting.ClassifyLine("Visit https://example.com/device.");
        Assert.Equal(WizardLineKind.Url, segment.Kind);
        Assert.Equal("https://example.com/device", segment.Highlight);
    }

    [Theory]
    [InlineData("Code: ABCD-EFGH", "Code: ", "ABCD-EFGH")]
    [InlineData("user_code = ABC123", "user_code = ", "ABC123")]
    [InlineData("USER_CODE: WDJB-MJHT", "USER_CODE: ", "WDJB-MJHT")]
    public void DeviceCode_IsDetected(string line, string expectedPrefix, string expectedCode)
    {
        var segment = WizardMessageFormatting.ClassifyLine(line);
        Assert.Equal(WizardLineKind.Code, segment.Kind);
        Assert.Equal(expectedPrefix, segment.Prefix);
        Assert.Equal(expectedCode, segment.Highlight);
    }

    [Fact]
    public void Null_ClassifiesAsEmptyText()
    {
        var segment = WizardMessageFormatting.ClassifyLine(null);
        Assert.Equal(WizardLineKind.Text, segment.Kind);
        Assert.Equal(string.Empty, segment.Text);
    }

    [Fact]
    public void ExtractUrls_ReturnsDistinctAbsoluteUrls()
    {
        var message = "Go to https://example.com/a and https://example.com/b\nor retry https://example.com/a";
        var urls = WizardMessageFormatting.ExtractUrls(message);

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://example.com/a", urls);
        Assert.Contains("https://example.com/b", urls);
    }

    [Fact]
    public void ExtractUrls_EmptyForNoUrls()
    {
        Assert.Empty(WizardMessageFormatting.ExtractUrls("nothing to see here"));
        Assert.Empty(WizardMessageFormatting.ExtractUrls(null));
    }

    [Fact]
    public void ContainsAuthUrl_TrueOnlyWhenUrlPresent()
    {
        Assert.True(WizardMessageFormatting.ContainsAuthUrl("login at https://accounts.google.com/o/oauth2"));
        Assert.False(WizardMessageFormatting.ContainsAuthUrl("paste your API key"));
    }
}
