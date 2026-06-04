using System.Text.Json;
using OpenClaw.SetupEngine.UI;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class WizardPayloadHelpersTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    // ---- ExtractStepMessage -----------------------------------------------

    [Fact]
    public void ExtractStepMessage_returns_string_message_unchanged()
    {
        var step = Parse("""{"type":"text","message":"Paste the code:"}""");
        Assert.Equal("Paste the code:", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_field_missing()
    {
        var step = Parse("""{"type":"note"}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_null()
    {
        var step = Parse("""{"type":"note","message":null}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_gemini_note_with_title_and_body()
    {
        // Real-world Gemini OAuth payload
        var step = Parse("""
            {
              "type": "text",
              "message": {
                "type": "note",
                "title": "Gemini CLI OAuth",
                "message": "You are running in a remote/VPS environment. Open the URL below..."
              }
            }
            """);
        var result = WizardPayloadHelpers.ExtractStepMessage(step);
        Assert.Equal(
            "Gemini CLI OAuth\n\nYou are running in a remote/VPS environment. Open the URL below...",
            result);
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_message()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","message":"hi"}}""");
        Assert.Equal("hi", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_title()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","title":"Heads up"}}""");
        Assert.Equal("Heads up", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_unknown_object_shape()
    {
        // Defensive: never render raw JSON in the UI. Empty is better than garbage.
        var step = Parse("""{"type":"text","message":{"some":"thing"}}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_non_string_non_object()
    {
        var step = Parse("""{"type":"text","message":42}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    // ---- WizardUrlLauncher -------------------------------------------------

    [Theory]
    [InlineData("https://auth.openai.com/oauth/authorize?client_id=foo")]
    [InlineData("https://auth.x.ai/oauth2/authorize?response_type=code")]
    [InlineData("https://accounts.google.com/o/oauth2/auth?x=1")]
    [InlineData("https://login.anthropic.com/oauth/authorize")]
    [InlineData("https://example.com/oauth/callback")]
    [InlineData("https://something.com/authorize?x=1")]
    [InlineData("https://api.foo.com/device/code")]
    [InlineData("https://something.com/?response_type=code")]
    public void LooksLikeOAuthUrl_accepts_oauth_patterns(string url)
    {
        Assert.True(WizardUrlLauncher.LooksLikeOAuthUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://docs.openclaw.ai/gateway/security")]
    [InlineData("https://example.com/blog/post")]
    [InlineData("https://github.com/openclaw/openclaw/issues/640")] // github IS in auth-host list — see next test
    public void LooksLikeOAuthUrl_accepts_or_rejects_general_urls(string url)
    {
        // docs/blog → reject. github → accept (device-code flow uses github.com)
        var uri = new Uri(url);
        var expected = uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, WizardUrlLauncher.LooksLikeOAuthUrl(uri));
    }

    [Fact]
    public void ShouldLaunch_rejects_non_http_schemes()
    {
        var seen = new HashSet<string>();
        Assert.False(WizardUrlLauncher.ShouldLaunch(seen, new Uri("ftp://auth.openai.com/oauth/authorize")));
        Assert.False(WizardUrlLauncher.ShouldLaunch(seen, new Uri("file:///c:/foo")));
        Assert.Empty(seen);
    }

    [Fact]
    public void ShouldLaunch_rejects_non_oauth_urls()
    {
        var seen = new HashSet<string>();
        Assert.False(WizardUrlLauncher.ShouldLaunch(seen, new Uri("https://docs.openclaw.ai/security")));
        Assert.Empty(seen);
    }

    [Fact]
    public void ShouldLaunch_returns_true_first_time_then_false_for_dedupe()
    {
        var seen = new HashSet<string>();
        var uri = new Uri("https://auth.openai.com/oauth/authorize?x=1");
        Assert.True(WizardUrlLauncher.ShouldLaunch(seen, uri));
        Assert.False(WizardUrlLauncher.ShouldLaunch(seen, uri));
        Assert.Single(seen);
    }

    [Fact]
    public void ShouldLaunch_dedupes_case_insensitively()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(WizardUrlLauncher.ShouldLaunch(seen, new Uri("https://auth.openai.com/oauth/authorize")));
        // Same URL different case in the path — should still dedupe via the set's comparer.
        Assert.False(WizardUrlLauncher.ShouldLaunch(seen, new Uri("https://auth.openai.com/OAUTH/AUTHORIZE")));
    }

    [Fact]
    public void ShouldLaunch_allows_distinct_oauth_urls_in_same_session()
    {
        var seen = new HashSet<string>();
        Assert.True(WizardUrlLauncher.ShouldLaunch(seen, new Uri("https://auth.openai.com/oauth/authorize?id=1")));
        Assert.True(WizardUrlLauncher.ShouldLaunch(seen, new Uri("https://auth.x.ai/oauth2/authorize?id=2")));
        Assert.Equal(2, seen.Count);
    }
}
