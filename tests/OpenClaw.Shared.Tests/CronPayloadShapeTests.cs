using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Validates that cron API payloads use the expected wire format.
/// All cron commands should use "id" (not "jobId") to identify a job,
/// matching the gateway contract for cron.run and cron.remove.
/// </summary>
public class CronPayloadShapeTests
{
    private static JsonElement Serialize(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void CronUpdate_Payload_Uses_Id_Not_JobId()
    {
        // Mirrors the wire object built by UpdateCronJobAsync(string id, object patch)
        var payload = new { id = "job-123", patch = new { enabled = false } };
        var el = Serialize(payload);

        Assert.True(el.TryGetProperty("id", out var idProp));
        Assert.Equal("job-123", idProp.GetString());
        Assert.False(el.TryGetProperty("jobId", out _), "Payload should use 'id', not 'jobId'");
        Assert.True(el.TryGetProperty("patch", out var patchProp));
        Assert.False(patchProp.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void CronRuns_Payload_Uses_Id_Not_JobId()
    {
        // Mirrors the wire object built by RequestCronRunsAsync
        var payload = new { id = "job-456", limit = 20, offset = 0 };
        var el = Serialize(payload);

        Assert.True(el.TryGetProperty("id", out var idProp));
        Assert.Equal("job-456", idProp.GetString());
        Assert.False(el.TryGetProperty("jobId", out _), "Payload should use 'id', not 'jobId'");
        Assert.Equal(20, el.GetProperty("limit").GetInt32());
        Assert.Equal(0, el.GetProperty("offset").GetInt32());
    }

    [Fact]
    public void CronRun_Payload_Uses_Id()
    {
        // Mirrors the wire object built by RunCronJobAsync
        var payload = new { id = "job-789", force = true };
        var el = Serialize(payload);

        Assert.True(el.TryGetProperty("id", out var idProp));
        Assert.Equal("job-789", idProp.GetString());
        Assert.False(el.TryGetProperty("jobId", out _));
        Assert.True(el.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void CronRemove_Payload_Uses_Id()
    {
        // Mirrors the wire object built by RemoveCronJobAsync
        var payload = new { id = "job-abc" };
        var el = Serialize(payload);

        Assert.True(el.TryGetProperty("id", out var idProp));
        Assert.Equal("job-abc", idProp.GetString());
        Assert.False(el.TryGetProperty("jobId", out _));
    }

    [Fact]
    public void CronAdd_Payload_Contains_Required_Fields()
    {
        // Mirrors the wire object built by the form save handler
        var payload = new Dictionary<string, object>
        {
            ["name"] = "My Test Job",
            ["enabled"] = true,
            ["schedule"] = new Dictionary<string, object> { ["kind"] = "cron", ["expr"] = "0 * * * *" },
            ["sessionTarget"] = "isolated",
            ["wakeMode"] = "now",
            ["payload"] = new Dictionary<string, object> { ["kind"] = "agentTurn", ["message"] = "Hello" },
            ["delivery"] = new Dictionary<string, object> { ["mode"] = "none" }
        };
        var el = Serialize(payload);

        Assert.Equal("My Test Job", el.GetProperty("name").GetString());
        Assert.True(el.GetProperty("enabled").GetBoolean());
        Assert.Equal("cron", el.GetProperty("schedule").GetProperty("kind").GetString());
        Assert.Equal("isolated", el.GetProperty("sessionTarget").GetString());
        Assert.Equal("agentTurn", el.GetProperty("payload").GetProperty("kind").GetString());
        Assert.False(el.TryGetProperty("jobId", out _), "Add payload should not contain jobId");
        Assert.False(el.TryGetProperty("id", out _), "Add payload should not contain id (server assigns it)");
    }

    [Fact]
    public void CronUpdate_Patch_With_DeleteAfterRun_Serializes_Correctly()
    {
        var patch = new Dictionary<string, object>
        {
            ["name"] = "Updated Job",
            ["schedule"] = new Dictionary<string, object> { ["kind"] = "at", ["at"] = "2026-12-31T23:59:59.000Z" },
            ["deleteAfterRun"] = true,
            ["sessionTarget"] = "isolated",
            ["wakeMode"] = "now",
            ["payload"] = new Dictionary<string, object> { ["kind"] = "agentTurn", ["message"] = "Run once" },
            ["delivery"] = new Dictionary<string, object> { ["mode"] = "none" }
        };
        var payload = new { id = "job-once", patch };
        var el = Serialize(payload);

        Assert.Equal("job-once", el.GetProperty("id").GetString());
        var patchEl = el.GetProperty("patch");
        Assert.True(patchEl.GetProperty("deleteAfterRun").GetBoolean());
        Assert.Equal("at", patchEl.GetProperty("schedule").GetProperty("kind").GetString());
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
