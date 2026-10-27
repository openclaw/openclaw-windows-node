using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Cron response parser contracts. Production request shapes are exercised
/// through real clients in GatewayFixtureProtocolTests and live round-trip tests.
/// </summary>
public class CronPayloadShapeTests
{
    private static JsonElement Serialize(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void CronRun_Response_EmptyPayload_IsAcceptedLegacyAck()
    {
        var payload = Serialize(new { });

        var result = OpenClawGatewayClient.ParseCronRunRequestResult(payload);

        Assert.True(result.Accepted);
        Assert.True(result.Enqueued);
        Assert.Null(result.RunId);
    }

    [Fact]
    public void CronRun_Response_DetailedEnqueue_PreservesRunId()
    {
        var payload = Serialize(new { ok = true, enqueued = true, runId = "manual:job:1" });

        var result = OpenClawGatewayClient.ParseCronRunRequestResult(payload);

        Assert.True(result.Accepted);
        Assert.True(result.Enqueued);
        Assert.Equal("manual:job:1", result.RunId);
    }

    [Fact]
    public void CronRun_Response_ExplicitRanFalse_IsNotEnqueued()
    {
        var payload = Serialize(new { ok = true, ran = false, reason = "not-due" });

        var result = OpenClawGatewayClient.ParseCronRunRequestResult(payload);

        Assert.True(result.Accepted);
        Assert.False(result.Enqueued);
        Assert.Equal("not-due", result.Reason);
    }

    [Fact]
    public void ScratchResponse_PreservesContentAndMonotonicRevisionAfterClear()
    {
        var present = OpenClawGatewayClient.ParseCronScratch(Serialize(new
        {
            scratch = new { content = "Private state", revision = 3, updatedAtMs = 12 },
            currentRevision = 3, maxBytes = 262144
        }));
        Assert.Equal("Private state", present.Content);
        Assert.Equal(3, present.CurrentRevision);
        Assert.Equal(262144, present.MaxBytes);
        var cleared = OpenClawGatewayClient.ParseCronScratch(Serialize(new
        {
            scratch = (object?)null, currentRevision = 4, maxBytes = 262144
        }));
        Assert.Null(cleared.Content);
        Assert.Equal(4, cleared.CurrentRevision);
    }

    [Theory]
    [InlineData(-1, 262144)]
    [InlineData(0, 0)]
    public void ScratchResponse_InvalidBoundsAreNotAnEmptySuccess(long revision, int maxBytes)
    {
        Assert.Throws<JsonException>(() => OpenClawGatewayClient.ParseCronScratch(Serialize(new
        {
            scratch = (object?)null, currentRevision = revision, maxBytes
        })));
    }

    [Fact]
    public void ScratchWrite_ConflictIsNotSuccess()
    {
        var result = OpenClawGatewayClient.ParseCronScratchWrite(Serialize(new
        {
            ok = false, reason = "revision-conflict", currentRevision = 9
        }));
        Assert.False(result.Ok);
        Assert.Equal(9, result.CurrentRevision);
        Assert.Throws<JsonException>(() => OpenClawGatewayClient.ParseCronScratchWrite(Serialize(new
        {
            ok = false, reason = "unexpected", currentRevision = 9
        })));
    }

    [Fact]
    public void ScratchResponse_InconsistentRevisionCannotAuthorizeAnOverwrite()
    {
        Assert.Throws<JsonException>(() => OpenClawGatewayClient.ParseCronScratch(Serialize(new
        {
            scratch = new { content = "State", revision = 1, updatedAtMs = 12 },
            currentRevision = 2, maxBytes = 262144
        })));
    }

    [Fact]
    public void TranscriptResponse_PreservesRolesAndOpaqueCursorAndOwnsJsonLifetime()
    {
        CronTranscriptPage page;
        using (var document = JsonDocument.Parse("""{"messages":[{"role":"assistant","content":"Recorded answer"}],"nextCursor":"opaque:page"}"""))
            page = OpenClawGatewayClient.ParseCronTranscript(document.RootElement);
        Assert.Equal("opaque:page", page.NextCursor);
        Assert.Equal("Recorded answer", Assert.Single(page.Messages).GetProperty("content").GetString());
        Assert.Equal("assistant", page.Messages[0].GetProperty("role").GetString());
        var last = OpenClawGatewayClient.ParseCronTranscript(Serialize(new { messages = Array.Empty<object>() }));
        Assert.Empty(last.Messages);
        Assert.Null(last.NextCursor);
    }

    // ── Run history display sanitization ──

    // Same regex used by CronPage.SanitizeForDisplay
    private static readonly Regex AbsolutePathPattern = new(
        @"(?:[A-Za-z]:\\(?:Users|home|usr|var|tmp)\\[^\s""']+)|(?:/(?:home|Users|usr|var|tmp)/[^\s""']+)",
        RegexOptions.Compiled);

    private static string SanitizeForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sanitized = TokenSanitizer.Sanitize(text);
        sanitized = AbsolutePathPattern.Replace(sanitized, m =>
        {
            var sep = m.Value.Contains('\\') ? '\\' : '/';
            var lastSep = m.Value.LastIndexOf(sep);
            return lastSep >= 0 ? "…" + sep + m.Value[(lastSep + 1)..] : m.Value;
        });
        return sanitized;
    }

    [Theory]
    [InlineData(
        "Error at C:\\Users\\alice\\repos\\myapp\\src\\main.cs line 42",
        "Error at …\\main.cs line 42")]
    [InlineData(
        "Failed: /home/bob/projects/app/index.js:10",
        "Failed: …/index.js:10")]
    [InlineData(
        "Crash in C:\\Users\\dev\\.openclaw\\logs\\crash.log",
        "Crash in …\\crash.log")]
    [InlineData(
        "Simple error with no paths",
        "Simple error with no paths")]
    public void SanitizeForDisplay_Redacts_File_Paths(string input, string expected)
    {
        Assert.Equal(expected, SanitizeForDisplay(input));
    }

    [Fact]
    public void SanitizeForDisplay_Redacts_Bearer_Tokens()
    {
        var input = "Auth failed: Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.abc.def";
        var result = SanitizeForDisplay(input);
        Assert.DoesNotContain("eyJhbGci", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void SanitizeForDisplay_Handles_Null_And_Empty()
    {
        Assert.Null(SanitizeForDisplay(null!));
        Assert.Equal("", SanitizeForDisplay(""));
    }
}
