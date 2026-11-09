using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class AppStateTests
{
    private AppState CreateState() => new();

    // ── PropertyChanged ─────────────────────────────────────────────────

    [Fact]
    public void SetField_FiresPropertyChanged_ExactlyOnce()
    {
        var state = CreateState();
        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        state.Status = ConnectionStatus.Connected;

        Assert.Single(fired);
        Assert.Equal(nameof(AppState.Status), fired[0]);
    }

    [Fact]
    public void SetField_SameValue_DoesNotFire()
    {
        var state = CreateState();
        state.Status = ConnectionStatus.Disconnected; // default
        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        state.Status = ConnectionStatus.Disconnected; // same value

        Assert.Empty(fired);
    }

    [Fact]
    public void SetField_NewArrayReference_AlwaysFires()
    {
        var state = CreateState();
        var arr1 = new SessionInfo[] { new() { Key = "a" } };
        state.Sessions = arr1;

        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        var arr2 = new SessionInfo[] { new() { Key = "a" } };
        state.Sessions = arr2; // different reference, same content

        Assert.Single(fired);
        Assert.Equal(nameof(AppState.Sessions), fired[0]);
    }

    [Fact]
    public void SetField_NullableProperty_FiresOnChange()
    {
        var state = CreateState();
        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        state.GatewaySelf = new GatewaySelfInfo { ServerVersion = "1.0" };
        state.GatewaySelf = null;

        Assert.Equal(2, fired.Count);
        Assert.All(fired, name => Assert.Equal(nameof(AppState.GatewaySelf), name));
    }

    // ── ClearCachedData ────────────────────────────────────────────────

    [Fact]
    public void ClearCachedData_ResetsAllFieldsAndFiresPropertyChanged()
    {
        var state = CreateState();
        state.Status = ConnectionStatus.Connected;
        state.Sessions = new[] { new SessionInfo { Key = "test" } };
        state.Channels = new[] { new ChannelHealth { Name = "ch1" } };
        state.Nodes = new[] { new GatewayNodeInfo { NodeId = "n1" } };
        state.Usage = new GatewayUsageInfo { TotalTokens = 100 };
        state.GatewaySelf = new GatewaySelfInfo { ServerVersion = "1.0" };
        state.NodePairList = new PairingListInfo();
        state.AuthFailureMessage = "test error";

        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        state.ClearCachedData();

        // Status should NOT be reset by ClearCachedData
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.DoesNotContain(nameof(AppState.Status), fired);

        // Everything else should be cleared
        Assert.Empty(state.Sessions);
        Assert.Empty(state.Channels);
        Assert.Empty(state.Nodes);
        Assert.Null(state.Usage);
        Assert.Null(state.GatewaySelf);
        Assert.Null(state.NodePairList);
        // AuthFailureMessage is NOT cleared by ClearCachedData (preserved for UI display)
        Assert.Equal("test error", state.AuthFailureMessage);
        Assert.Null(state.CurrentActivity);
        Assert.Null(state.Config);
        Assert.Null(state.AgentsList);

        // Should have fired PropertyChanged for each cleared property
        Assert.Contains(nameof(AppState.Sessions), fired);
        Assert.Contains(nameof(AppState.Channels), fired);
        Assert.Contains(nameof(AppState.GatewaySelf), fired);
        // AuthFailureMessage is NOT cleared by ClearCachedData
        Assert.DoesNotContain(nameof(AppState.AuthFailureMessage), fired);
    }

    [Fact]
    public void FindAgent_ComposesRosterFirstIdentityAndNotifiesChatOnRosterChanges()
    {
        var state = CreateState();
        using var roster = JsonDocument.Parse("""{"agents":[{"id":"main","name":"Roster Assistant"}]}""");
        var rosterUpdates = 0;
        state.AgentRosterChanged += (_, _) => rosterUpdates++;

        state.AgentsList = roster.RootElement.Clone();
        state.AgentIdentities = new Dictionary<string, ChatAgentIdentity>(StringComparer.Ordinal)
        {
            ["main"] = new("main", "Gateway identity", "data:image/png;base64,AA==", "✨"),
        };

        var agent = Assert.IsType<ChatAgentPresentation>(state.FindAgent("main"));
        Assert.Equal("Roster Assistant", agent.Name);
        Assert.Equal("data:image/png;base64,AA==", agent.AvatarUrl);
        Assert.Equal("✨", agent.Emoji);
        Assert.Equal(2, rosterUpdates);

        state.ClearAgentIdentities();

        Assert.Null(state.FindAgent("main")?.AvatarUrl);
        Assert.Equal(3, rosterUpdates);
    }

    // ── Session previews ────────────────────────────────────────────────

    [Fact]
    public void SessionPreview_SetAndGet()
    {
        var state = CreateState();
        var preview = new SessionPreviewInfo { Key = "s1" };

        state.SetSessionPreview("s1", preview);

        Assert.Equal("s1", state.GetSessionPreview("s1")?.Key);
        Assert.Null(state.GetSessionPreview("nonexistent"));
    }

    [Fact]
    public void PruneSessionPreviews_RemovesStale()
    {
        var state = CreateState();
        state.SetSessionPreview("keep", new SessionPreviewInfo { Key = "keep" });
        state.SetSessionPreview("remove", new SessionPreviewInfo { Key = "remove" });

        state.PruneSessionPreviews(new HashSet<string> { "keep" });

        Assert.NotNull(state.GetSessionPreview("keep"));
        Assert.Null(state.GetSessionPreview("remove"));
    }

    // ── GetAgentIds ─────────────────────────────────────────────────────

    [Fact]
    public void GetAgentIds_DefaultsToMain()
    {
        var state = CreateState();
        var ids = state.GetAgentIds();
        Assert.Single(ids);
        Assert.Equal("main", ids[0]);
    }

    [Fact]
    public void GetAgentIds_ParsesFromAgentsList()
    {
        var state = CreateState();
        var json = JsonDocument.Parse("""{"agents":[{"id":"alpha"},{"id":"beta"}]}""");
        state.AgentsList = json.RootElement.Clone();

        var ids = state.GetAgentIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("alpha", ids);
        Assert.Contains("beta", ids);
    }

    // ── Multiple properties ─────────────────────────────────────────────

    [Fact]
    public void AllProperties_FirePropertyChanged()
    {
        var state = CreateState();
        var fired = new List<string>();
        state.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        state.Status = ConnectionStatus.Connected;
        state.CurrentActivity = new AgentActivity();
        state.AuthFailureMessage = "fail";
        state.Channels = new[] { new ChannelHealth() };
        state.Sessions = new[] { new SessionInfo() };
        state.Nodes = new[] { new GatewayNodeInfo() };
        state.Usage = new GatewayUsageInfo();
        state.UsageStatus = new GatewayUsageStatusInfo();
        state.UsageCost = new GatewayCostUsageInfo();
        state.GatewaySelf = new GatewaySelfInfo();
        state.NodePairList = new PairingListInfo();
        state.DevicePairList = new DevicePairingListInfo();
        state.ModelsList = new ModelsListInfo();
        state.Presence = new PresenceEntry[] { };
        state.UpdateInfo = new UpdateCommandCenterInfo();

        Assert.Equal(15, fired.Count);
        Assert.All(fired, name => Assert.False(string.IsNullOrEmpty(name)));
    }
}
