using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Cross-cutting smoke tests that exercise the full Phase 0/1 pipeline:
/// raw <c>channels.status</c> JSON → <see cref="ChannelsStatusParser"/> →
/// <see cref="ChannelsAggregator"/> → <see cref="ChannelRecord"/>s.
/// </summary>
public class ChannelsPipelineTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Pipeline_ProducesExpectedRecordsForRealisticSnapshot()
    {
        // Approximates what the gateway would return after a fresh probe
        // with WhatsApp connected, Telegram running, Discord erroring,
        // and Slack/Nostr unconfigured.
        var json = """
            {
              "ts": 1716000000,
              "channelOrder": ["whatsapp", "telegram", "discord", "slack", "nostr"],
              "channelLabels": { "whatsapp": "WhatsApp", "telegram": "Telegram", "discord": "Discord", "slack": "Slack", "nostr": "Nostr" },
              "channels": {
                "whatsapp": { "configured": true, "running": true, "connected": true, "linked": true, "self": { "e164": "+44..." }, "authAgeMs": 86400000, "lastMessageAt": 1716000000000 },
                "telegram": { "configured": true, "running": true, "probe": { "ok": true, "elapsedMs": 91 }, "lastProbeAt": 1716000000000, "botUsername": "openclaw_bot" },
                "discord":  { "configured": true, "running": false, "lastError": "RateLimited", "probe": { "ok": false, "status": 429 }, "lastProbeAt": 1716000000000 },
                "slack":    { "configured": false },
                "nostr":    { "configured": false }
              }
            }
            """;
        var snapshot = ChannelsStatusParser.Parse(Json(json));
        var records = ChannelsAggregator.Aggregate(snapshot, DateTime.UtcNow);

        // Order: configured first (whatsapp, telegram, discord), then unconfigured (slack, nostr),
        // then built-in extras the gateway didn't list (googlechat, signal, imessage) — appended
        // for discoverability so the user can add more channels.
        var ids = records.Select(r => r.Id).ToList();
        Assert.Equal(
            new[] { "whatsapp", "telegram", "discord", "slack", "nostr", "googlechat", "signal", "imessage" },
            ids);
        Assert.True(records[0].IsConfigured);
        Assert.True(records[1].IsConfigured);
        Assert.True(records[2].IsConfigured);
        Assert.False(records[3].IsConfigured); // slack
        Assert.False(records[4].IsConfigured); // nostr
        Assert.False(records[5].IsConfigured); // googlechat (preview)
        Assert.False(records[6].IsConfigured); // signal (preview)
        Assert.False(records[7].IsConfigured); // imessage (preview)

        // Capabilities reflect channel id
        Assert.True(records[0].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        Assert.True(records[0].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.True(records[1].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.False(records[2].Capabilities.HasFlag(ChannelCapabilities.CanLogout));

        // Labels: snapshot supplies the ones it knows; preview channels fall
        // back to the built-in pretty-name catalog (so users don't see
        // lowercase ids like "googlechat" in the AVAILABLE list).
        Assert.Equal("WhatsApp", records[0].Label);
        Assert.Equal("Nostr", records[4].Label);
        Assert.Equal("Google Chat", records[5].Label);
        Assert.Equal("Signal", records[6].Label);
        Assert.Equal("iMessage", records[7].Label);
    }

    [Fact]
    public void Pipeline_HandlesEmptySnapshotGracefully()
    {
        var snapshot = ChannelsStatusParser.Parse(Json("{}"));
        var records = ChannelsAggregator.Aggregate(snapshot, DateTime.UtcNow);
        // No order from gateway → built-in fallback list is used.
        Assert.Equal(ChannelsAggregator.BuiltInChannelOrder.Count, records.Count);
        Assert.All(records, r => Assert.False(r.IsConfigured));
    }
}
