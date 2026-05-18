using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelsStatusParserTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Parse_ReturnsEmptySnapshot_ForNonObjectInput()
    {
        var snap = ChannelsStatusParser.Parse(Json("[]"));
        Assert.Empty(snap.ChannelOrder);
        Assert.Empty(snap.Channels);
    }

    [Fact]
    public void Parse_ExtractsChannelOrderAndLabels()
    {
        var json = """
            {
              "ts": 1716000000.5,
              "channelOrder": ["whatsapp", "telegram"],
              "channelLabels": { "whatsapp": "WhatsApp", "telegram": "Telegram" },
              "channels": {}
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.Equal(new[] { "whatsapp", "telegram" }, snap.ChannelOrder);
        Assert.Equal("WhatsApp", snap.ChannelLabels["whatsapp"]);
        Assert.Equal(1716000000.5, snap.Ts);
    }

    [Fact]
    public void Parse_PrefersChannelMetaOverLabels()
    {
        var json = """
            {
              "channelOrder": ["whatsapp"],
              "channelLabels": { "whatsapp": "Legacy Label" },
              "channelMeta": [
                { "id": "whatsapp", "label": "WhatsApp", "detailLabel": "WhatsApp Business", "systemImage": "message.fill" }
              ],
              "channels": {}
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.Equal("WhatsApp", snap.ResolveLabel("whatsapp"));
        Assert.Equal("WhatsApp Business", snap.ResolveDetailLabel("whatsapp"));
        Assert.Equal("message.fill", snap.ResolveSystemImage("whatsapp"));
    }

    [Fact]
    public void Parse_FallsBackToIdWhenLabelMissing()
    {
        var snap = ChannelsStatusParser.Parse(Json("""{ "channelOrder": ["custom-plugin"], "channelLabels": {} }"""));
        Assert.Equal("custom-plugin", snap.ResolveLabel("custom-plugin"));
        Assert.Equal("custom-plugin", snap.ResolveDetailLabel("custom-plugin"));
        Assert.Null(snap.ResolveSystemImage("custom-plugin"));
    }

    [Fact]
    public void Parse_ExtractsChannelsAndKeepsRawJson()
    {
        var json = """
            {
              "channelOrder": ["telegram"],
              "channels": {
                "telegram": { "configured": true, "running": true, "botUsername": "openclaw_bot" }
              }
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.True(snap.Channels.ContainsKey("telegram"));
        var tg = snap.Channels["telegram"];
        Assert.Equal(JsonValueKind.True, tg.GetProperty("configured").ValueKind);
        Assert.Equal("openclaw_bot", tg.GetProperty("botUsername").GetString());
    }

    [Fact]
    public void Parse_ExtractsChannelAccounts()
    {
        var json = """
            {
              "channelOrder": ["whatsapp"],
              "channels": {},
              "channelAccounts": {
                "whatsapp": [
                  { "id": "primary", "configured": true, "running": true, "lastInboundAt": 1716000000000 },
                  { "id": "secondary", "configured": false }
                ]
              },
              "channelDefaultAccountId": { "whatsapp": "primary" }
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        var accs = snap.ChannelAccounts["whatsapp"];
        Assert.Equal(2, accs.Count);
        Assert.Equal("primary", accs[0].Id);
        Assert.True(accs[0].Configured);
        Assert.Equal(1716000000000d, accs[0].LastInboundAt);
        Assert.False(accs[1].Configured);
        Assert.Equal("primary", snap.ChannelDefaultAccountId["whatsapp"]);
    }

    [Fact]
    public void ExtractWhatsApp_PopulatesAllFields()
    {
        var json = """
            {
              "configured": true, "running": true, "connected": true, "linked": true,
              "self": { "e164": "+44...", "jid": "44...@s.whatsapp.net" },
              "authAgeMs": 86400000, "lastConnectedAt": 1716000000000, "lastMessageAt": 1716000005000,
              "reconnectAttempts": 2,
              "lastDisconnect": { "at": 1715999000000, "status": 1006, "error": "timeout", "loggedOut": false }
            }
            """;
        var status = ChannelsStatusParser.ExtractWhatsApp(Json(json));
        Assert.NotNull(status);
        Assert.True(status!.Connected);
        Assert.Equal("+44...", status.Self!.E164);
        Assert.Equal(2, status.ReconnectAttempts);
        Assert.Equal("timeout", status.LastDisconnect!.Error);
        Assert.False(status.LastDisconnect.LoggedOut);
    }

    [Fact]
    public void ExtractGeneric_ParsesProbe()
    {
        var json = """
            { "configured": true, "running": true, "probe": { "ok": true, "status": 200, "elapsedMs": 91, "version": "1.2" } }
            """;
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.True(generic!.Configured);
        Assert.True(generic.Probe!.Ok);
        Assert.Equal(200, generic.Probe.Status);
        Assert.Equal(91d, generic.Probe.ElapsedMs);
        Assert.Equal("1.2", generic.Probe.Version);
    }

    [Fact]
    public void ExtractGeneric_FallsBackToErrorWhenLastErrorMissing()
    {
        var json = """{ "configured": false, "error": "boom" }""";
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.Equal("boom", generic!.LastError);
    }

    [Fact]
    public void ExtractGeneric_PopulatesActivityAndModeFields()
    {
        // Mirrors what the live gateway returns for a running Telegram channel.
        var json = """
            {
              "configured": true, "running": true, "connected": true,
              "mode": "polling",
              "lastStartAt": 1778990651099,
              "lastEventAt": 1778997012450,
              "lastTransportActivityAt": 1778997012450,
              "reconnectAttempts": 3,
              "restartPending": false
            }
            """;
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.Equal("polling", generic!.Mode);
        Assert.Equal(1778990651099d, generic.LastStartAt);
        Assert.Equal(1778997012450d, generic.LastEventAt);
        Assert.Equal(1778997012450d, generic.LastTransportActivityAt);
        Assert.Equal(3, generic.ReconnectAttempts);
        Assert.False(generic.RestartPending);
    }

    [Fact]
    public void ExtractGeneric_DefaultsActivityFields_WhenMissing()
    {
        var json = """{ "configured": true, "running": true }""";
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.Null(generic!.Mode);
        Assert.Null(generic.LastStartAt);
        Assert.Null(generic.LastEventAt);
        Assert.Null(generic.LastTransportActivityAt);
        Assert.Equal(0, generic.ReconnectAttempts);
        Assert.False(generic.RestartPending);
    }
}

public class ChannelsAggregatorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static ChannelsStatusSnapshot SnapshotWith(string order, params (string id, string statusJson)[] channels)
    {
        var ids = order.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var map = new Dictionary<string, JsonElement>();
        foreach (var (id, statusJson) in channels)
            map[id] = Json(statusJson);
        return new ChannelsStatusSnapshot { ChannelOrder = ids, Channels = map };
    }

    [Fact]
    public void Aggregate_FallsBackToBuiltInOrder_WhenSnapshotEmpty()
    {
        var records = ChannelsAggregator.Aggregate(null, DateTime.UtcNow);
        Assert.Equal(ChannelsAggregator.BuiltInChannelOrder.Count, records.Count);
        var ids = records.Select(r => r.Id).ToHashSet();
        Assert.Contains("whatsapp", ids);
        Assert.Contains("telegram", ids);
        Assert.Contains("nostr", ids);
    }

    [Fact]
    public void Aggregate_PutsConfiguredFirst()
    {
        var snap = SnapshotWith(
            "whatsapp,telegram,discord",
            ("whatsapp", """{ "configured": false }"""),
            ("telegram", """{ "configured": true, "running": true }"""),
            ("discord", """{ "configured": false }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.Equal("telegram", records[0].Id);
        Assert.True(records[0].IsConfigured);
        Assert.False(records[1].IsConfigured);
        Assert.False(records[2].IsConfigured);
    }

    [Fact]
    public void Aggregate_MultiAccount_OneActiveCountsAsConfigured()
    {
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "whatsapp" },
            Channels = new Dictionary<string, JsonElement> { ["whatsapp"] = Json("""{ "configured": false }""") },
            ChannelAccounts = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>
            {
                ["whatsapp"] = new[]
                {
                    new ChannelAccountSnapshot { Id = "primary", Configured = true, Running = true },
                }
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.True(records[0].IsConfigured);
    }

    [Fact]
    public void Aggregate_AlwaysUnionsBuiltInCatalog_SoUserCanDiscoverMoreChannels()
    {
        // Gateway reports only the channel the user has configured.
        // The page should still surface the other built-in channels as
        // AVAILABLE entries so the user can discover and add more.
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "telegram" },
            Channels = new Dictionary<string, JsonElement>
            {
                ["telegram"] = Json("""{ "configured": true, "running": true }"""),
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);

        // Configured (telegram) appears first, then every other built-in
        // catalog channel as unconfigured "preview" entries.
        Assert.Equal("telegram", records[0].Id);
        Assert.True(records[0].IsConfigured);

        var allIds = records.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in ChannelsAggregator.BuiltInChannelOrder)
            Assert.Contains(builtIn, allIds);

        // No duplicate ids (built-in entries dedupe against gateway-reported).
        Assert.Equal(records.Count, records.Select(r => r.Id.ToLowerInvariant()).Distinct().Count());

        // All non-target built-ins are unconfigured.
        Assert.All(records.Where(r => !string.Equals(r.Id, "telegram", System.StringComparison.OrdinalIgnoreCase)),
            r => Assert.False(r.IsConfigured));
    }

    [Fact]
    public void Aggregate_BuiltInExtras_GetPrettyLabels()
    {
        // Gateway provides no labels for preview channels; the aggregator
        // should fall back to the built-in pretty-name catalog so the
        // AVAILABLE list never shows raw lowercase ids like "googlechat".
        var records = ChannelsAggregator.Aggregate(null, DateTime.UtcNow);
        var byId = records.ToDictionary(r => r.Id.ToLowerInvariant());
        Assert.Equal("WhatsApp",    byId["whatsapp"].Label);
        Assert.Equal("Google Chat", byId["googlechat"].Label);
        Assert.Equal("iMessage",    byId["imessage"].Label);
    }

    [Fact]
    public void Aggregate_CapabilitiesAreInferredFromId()
    {
        // All four channels are configured here, so their full capability set
        // (Logout for whatsapp/telegram/signal, ShowQr+Relink for whatsapp/signal)
        // should be reported. CanLogout/CanRelink are gated on "configured" —
        // see Aggregate_NotConfigured_HidesLogoutAndRelink for the negative case.
        var snap = SnapshotWith(
            "whatsapp,telegram,discord,signal",
            ("whatsapp", """{ "configured": true, "running": true }"""),
            ("telegram", """{ "configured": true, "running": true }"""),
            ("discord",  """{ "configured": true, "running": true }"""),
            ("signal",   """{ "configured": true, "running": true }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow).ToDictionary(r => r.Id);

        Assert.True(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.True(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        Assert.True(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanRelink));
        Assert.True(records["telegram"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.False(records["telegram"].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        Assert.False(records["discord"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.True(records["signal"].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        // Configured Signal exposes CanLogout — it's a QR channel and Logout
        // is the unlink-the-device action (lightweight, re-scan to reconnect).
        // Without this, the Channels page would render zero control buttons
        // for a configured Signal card (isQr=true + hasLogout=false fell
        // through both branches in BuildControlsSection).
        Assert.True(records["signal"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
    }

    [Fact]
    public void Aggregate_NotConfigured_HidesLogoutAndRelink_KeepsShowQr()
    {
        // Regression: unconfigured WhatsApp / Telegram used to surface a
        // Logout header action because CanLogout was id-driven only. We now
        // gate CanLogout AND CanRelink on IsConfigured so the page doesn't
        // show "Logout" on a channel the user has never signed into.
        // CanShowQr stays available on QR channels because it's the bootstrap
        // path — that's how the user *first* configures WhatsApp/Signal.
        var snap = SnapshotWith(
            "whatsapp,telegram,signal",
            ("whatsapp", """{ "configured": false }"""),
            ("telegram", """{ "configured": false }"""),
            ("signal",   """{ "configured": false }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow).ToDictionary(r => r.Id);

        Assert.False(records["whatsapp"].IsConfigured);
        Assert.False(records["telegram"].IsConfigured);
        Assert.False(records["signal"].IsConfigured);

        // CanLogout must be absent on every unconfigured channel.
        Assert.False(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.False(records["telegram"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.False(records["signal"].Capabilities.HasFlag(ChannelCapabilities.CanLogout));

        // CanRelink (rotate an existing link) is meaningless when there's no link.
        Assert.False(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanRelink));
        Assert.False(records["signal"].Capabilities.HasFlag(ChannelCapabilities.CanRelink));

        // CanShowQr stays — it's the bootstrap path for QR channels.
        Assert.True(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        Assert.True(records["signal"].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));

        // Refresh is always available.
        Assert.True(records["whatsapp"].Capabilities.HasFlag(ChannelCapabilities.CanRefresh));
        Assert.True(records["telegram"].Capabilities.HasFlag(ChannelCapabilities.CanRefresh));
    }

    [Fact]
    public void Aggregate_iMessageIsUnavailableOnWindows()
    {
        var snap = SnapshotWith("imessage", ("imessage", "{}"));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.True(records[0].IsUnavailableOnWindows);
    }

    [Fact]
    public void Aggregate_UsesGatewayLabelsWhenProvided()
    {
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "custom-plugin" },
            ChannelLabels = new Dictionary<string, string> { ["custom-plugin"] = "Custom Plugin" },
            Channels = new Dictionary<string, JsonElement>(),
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.Equal("Custom Plugin", records[0].Label);
    }

    [Fact]
    public void IsChannelConfigured_TrueWhenRunningOrConnected()
    {
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "running": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "connected": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "configured": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelConfigured(Json("""{ }"""), null));
        Assert.False(ChannelsAggregator.IsChannelConfigured(Json("""{ "configured": false }"""), null));
    }

    [Fact]
    public void IsChannelRunning_TrueOnlyOnRunningOrConnected()
    {
        // Running is a strict subset of Configured: configured-without-running
        // returns false so the page can offer "Start channel" as recovery.
        Assert.True(ChannelsAggregator.IsChannelRunning(Json("""{ "running": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelRunning(Json("""{ "connected": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelRunning(Json("""{ "configured": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelRunning(Json("""{ }"""), null));
    }

    [Fact]
    public void Aggregate_CanStart_OnlyWhenConfiguredButNotRunning()
    {
        // 'telegram': configured but stopped — this is the canonical "Start
        // channel" scenario.
        // 'discord':  configured AND running — Start would be a no-op, no flag.
        // 'slack':    not configured at all — no Start until credentials exist.
        var snap = SnapshotWith(
            "telegram,discord,slack",
            ("telegram", """{ "configured": true }"""),
            ("discord",  """{ "configured": true, "running": true }"""),
            ("slack",    """{ }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow).ToDictionary(r => r.Id);

        Assert.True(records["telegram"].IsConfigured);
        Assert.False(records["telegram"].IsRunning);
        Assert.True(records["telegram"].Capabilities.HasFlag(ChannelCapabilities.CanStart));

        Assert.True(records["discord"].IsConfigured);
        Assert.True(records["discord"].IsRunning);
        Assert.False(records["discord"].Capabilities.HasFlag(ChannelCapabilities.CanStart));

        Assert.False(records["slack"].IsConfigured);
        Assert.False(records["slack"].IsRunning);
        Assert.False(records["slack"].Capabilities.HasFlag(ChannelCapabilities.CanStart));
    }

    [Fact]
    public void Aggregate_SurfacesChannelsMissingFromChannelOrder()
    {
        // Older gateways or plugin-only setups may omit channelOrder while still
        // populating channels/channelMeta. The aggregator must still surface
        // those channels — dropping them silently is a regression.
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "telegram" }, // only one in order
            Channels = new Dictionary<string, JsonElement>
            {
                ["telegram"] = Json("""{ "configured": true, "running": true }"""),
                ["custom-plugin"] = Json("""{ "configured": true, "running": true }"""),
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        var byId = records.ToDictionary(r => r.Id);
        Assert.Contains("telegram", byId.Keys);
        Assert.Contains("custom-plugin", byId.Keys);
        // Both gateway-reported channels are configured. (Built-in extras —
        // whatsapp/discord/etc. — appear unconfigured per the discoverability
        // union; see Aggregate_AlwaysUnionsBuiltInCatalog.)
        Assert.True(byId["telegram"].IsConfigured);
        Assert.True(byId["custom-plugin"].IsConfigured);
    }

    [Fact]
    public void Aggregate_UnionsChannelsAndMetaWhenChannelOrderEmpty()
    {
        var snap = new ChannelsStatusSnapshot
        {
            // No channelOrder at all
            Channels = new Dictionary<string, JsonElement>
            {
                ["plugin-a"] = Json("""{ "configured": true }"""),
            },
            ChannelMeta = new[] { new ChannelUiMetaEntry { Id = "plugin-b", Label = "Plugin B" } },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        var ids = records.Select(r => r.Id).ToHashSet();
        // Custom plugin-a / plugin-b surface — and the built-in catalog
        // ALWAYS unions in for discoverability, so whatsapp et al. also
        // appear (per Aggregate_AlwaysUnionsBuiltInCatalog).
        Assert.Contains("plugin-a", ids);
        Assert.Contains("plugin-b", ids);
        Assert.Contains("whatsapp", ids);
    }

    [Fact]
    public void Aggregate_AccountsKeysAlsoCountAsKnownIds()
    {
        var snap = new ChannelsStatusSnapshot
        {
            // No channelOrder, no channels — only multi-account info present.
            ChannelAccounts = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>
            {
                ["acct-only"] = new[] { new ChannelAccountSnapshot { Id = "primary", Configured = true } }
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        // acct-only is surfaced (the assertion this test was originally
        // protecting); built-in extras append for discoverability.
        var byId = records.ToDictionary(r => r.Id);
        Assert.Contains("acct-only", byId.Keys);
        Assert.True(byId["acct-only"].IsConfigured);
    }
}
