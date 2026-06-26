using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class CronRunHistoryDisplayTests
{
    [Fact]
    public void ExtractText_UsesSummaryAsExpandableFullResponse()
    {
        var response = string.Join("\n", new[]
        {
            "☀️ **Woodinville, WA — Wednesday, June 24**",
            "",
            "**Right now (11:08 AM):**",
            "- **Sunny** — 74°F (23°C), feels like 77°F",
            "- Humidity: 48%",
            "- Wind: 2 mph SSW (light breeze)",
            "",
            "Stay hydrated!"
        });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "ok",
            summary = response
        }));

        var text = CronRunHistoryDisplay.ExtractText(doc.RootElement, isError: false);

        Assert.Equal(response, text.PreviewText);
        Assert.Equal(response, text.FullText);
        Assert.True(text.HasExpandableFullText);
    }

    [Fact]
    public void ExtractText_PrefersFullResponseWhenSummaryIsShort()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "ok",
            summary = "☀️ **Woodinville, WA — Wednesday, June 24**",
            fullResponse = "☀️ **Woodinville, WA — Wednesday, June 24**\n\nFull forecast details."
        }));

        var text = CronRunHistoryDisplay.ExtractText(doc.RootElement, isError: false);

        Assert.Equal("☀️ **Woodinville, WA — Wednesday, June 24**", text.PreviewText);
        Assert.Equal("☀️ **Woodinville, WA — Wednesday, June 24**\n\nFull forecast details.", text.FullText);
        Assert.True(text.HasExpandableFullText);
    }

    [Fact]
    public void ExtractText_MultilineSummaryIsExpandableEvenWhenShort()
    {
        const string response = "Line one\nLine two\nLine three";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "ok",
            summary = response
        }));

        var text = CronRunHistoryDisplay.ExtractText(doc.RootElement, isError: false);

        Assert.Equal(response, text.PreviewText);
        Assert.Equal(response, text.FullText);
        Assert.True(text.HasExpandableFullText);
    }

    [Fact]
    public void ExtractText_CanRecoverAssistantTextFromTranscriptJsonl()
    {
        var transcript = string.Join('\n', new[]
        {
            """{"stream":"assistant","data":{"delta":"Hello "}}""",
            """{"stream":"assistant","data":{"delta":"there"}}""",
            """{"stream":"assistant","data":{"text":"Hello there\n\nFinal answer."}}"""
        });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "ok",
            summary = "Hello there",
            transcript
        }));

        var text = CronRunHistoryDisplay.ExtractText(doc.RootElement, isError: false);

        Assert.Equal("Hello there", text.PreviewText);
        Assert.Equal("Hello there\n\nFinal answer.", text.FullText);
        Assert.True(text.HasExpandableFullText);
    }

    [Fact]
    public void SanitizeForDisplay_RedactsTokensAndPaths()
    {
        var input = "Auth failed at /home/openclaw/.openclaw/agents/main/sessions/abc.jsonl with Authorization: Bearer secret.token.value";

        var result = CronRunHistoryDisplay.SanitizeForDisplay(input);

        Assert.Equal("Auth failed at …/abc.jsonl with Authorization: Bearer [REDACTED]", result);
    }
}
