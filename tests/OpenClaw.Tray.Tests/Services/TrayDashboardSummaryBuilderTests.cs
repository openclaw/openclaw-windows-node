using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class TrayDashboardSummaryBuilderTests
{
    private static readonly DateTime FixedNowUtc = new(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc);

    private static TrayMenuSnapshot Base(
        ConnectionStatus status = ConnectionStatus.Connected,
        string? gatewayUrl = "http://localhost:7070",
        string? authFailure = null,
        GatewayNodeInfo[]? nodes = null,
        SessionInfo[]? sessions = null,
        GatewayUsageInfo? usage = null,
        DateTime? lastUpdated = null,
        PairingListInfo? nodePairList = null,
        DevicePairingListInfo? devicePairList = null) => new()
    {
        CurrentStatus = status,
        AuthFailureMessage = authFailure,
        GatewayUrl = gatewayUrl,
        GatewaySelf = null,
        Presence = null,
        EnableNodeMode = false,
        NodeIsPaired = false,
        NodeIsPendingApproval = false,
        NodeIsConnected = false,
        NodePairList = nodePairList,
        DevicePairList = devicePairList,
        Nodes = nodes ?? Array.Empty<GatewayNodeInfo>(),
        Sessions = sessions ?? Array.Empty<SessionInfo>(),
        Usage = usage,
        UsageStatus = null,
        UsageCost = null,
        Settings = null,
        SetupMenuLabel = "Reconfigure...",
        ShowSetupMenuEntry = true,
        LastUpdated = lastUpdated,
    };

    private static TrayDashboardSummary Build(TrayMenuSnapshot snapshot) =>
        new TrayDashboardSummaryBuilder(snapshot, FixedNowUtc).Build();

    // ── Health classification ──

    [Fact]
    public void Connected_IsOkSeverityWithEndpoint()
    {
        var summary = Build(Base(ConnectionStatus.Connected));

        Assert.Equal(TrayHealthSeverity.Ok, summary.Severity);
        Assert.Equal("Connected", summary.Headline);
        Assert.Equal("localhost:7070", summary.Endpoint);
    }

    [Fact]
    public void Disconnected_IsNeutralSeverity()
    {
        var summary = Build(Base(ConnectionStatus.Disconnected));

        Assert.Equal(TrayHealthSeverity.Neutral, summary.Severity);
        Assert.Equal("Disconnected", summary.Headline);
    }

    [Fact]
    public void Connecting_IsCautionSeverity()
    {
        var summary = Build(Base(ConnectionStatus.Connecting));

        Assert.Equal(TrayHealthSeverity.Caution, summary.Severity);
        Assert.Equal("Connecting…", summary.Headline);
    }

    [Fact]
    public void Error_IsCriticalSeverity()
    {
        var summary = Build(Base(ConnectionStatus.Error));

        Assert.Equal(TrayHealthSeverity.Critical, summary.Severity);
        Assert.Equal("Connection error", summary.Headline);
    }

    [Fact]
    public void AuthFailure_OverridesConnectedWithCriticalSeverity()
    {
        var summary = Build(Base(ConnectionStatus.Connected, authFailure: "token expired"));

        Assert.Equal(TrayHealthSeverity.Critical, summary.Severity);
        Assert.Equal("Authentication failed", summary.Headline);
    }

    [Fact]
    public void PendingPairing_TakesPriorityOverConnectedStatus()
    {
        var nodePairList = new PairingListInfo();
        nodePairList.Pending.Add(new PairingRequest());
        nodePairList.Pending.Add(new PairingRequest());

        var summary = Build(Base(ConnectionStatus.Connected, nodePairList: nodePairList));

        Assert.Equal(TrayHealthSeverity.Caution, summary.Severity);
        Assert.Equal("Pairing approval pending (2)", summary.Headline);
    }

    [Fact]
    public void NullGatewayUrl_ProducesNullEndpoint()
    {
        var summary = Build(Base(gatewayUrl: null));

        Assert.Null(summary.Endpoint);
    }

    // ── Heartbeat ──

    [Fact]
    public void Heartbeat_NullWhenNoLastUpdated()
    {
        var summary = Build(Base(lastUpdated: null));

        Assert.Null(summary.Heartbeat);
    }

    [Fact]
    public void Heartbeat_FormatsRelativeAge()
    {
        var summary = Build(Base(lastUpdated: FixedNowUtc.AddSeconds(-12)));

        Assert.Equal("Updated 12s ago", summary.Heartbeat);
    }

    [Fact]
    public void Heartbeat_FormatsMinutes()
    {
        var summary = Build(Base(lastUpdated: FixedNowUtc.AddMinutes(-5)));

        Assert.Equal("Updated 5m ago", summary.Heartbeat);
    }

    [Fact]
    public void Heartbeat_ConvertsLocalTimestampToUtc()
    {
        var localUpdated = FixedNowUtc.AddMinutes(-7).ToLocalTime();

        var summary = Build(Base(lastUpdated: localUpdated));

        Assert.Equal("Updated 7m ago", summary.Heartbeat);
    }

    [Fact]
    public void Heartbeat_FutureTimestampClampsToZero()
    {
        var summary = Build(Base(lastUpdated: FixedNowUtc.AddSeconds(30)));

        Assert.Equal("Updated 0s ago", summary.Heartbeat);
    }

    // ── Metrics line ──

    [Fact]
    public void MetricsLine_NullWhenNothingToShow()
    {
        var summary = Build(Base(ConnectionStatus.Disconnected));

        Assert.Null(summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_SummarizesNodesOnlineOverTotal()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { IsOnline = true },
            new GatewayNodeInfo { IsOnline = false },
            new GatewayNodeInfo { IsOnline = true },
        };

        var summary = Build(Base(nodes: nodes));

        Assert.NotNull(summary.MetricsLine);
        Assert.Contains("2/3 nodes", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_SingularNodeLabel()
    {
        var nodes = new[] { new GatewayNodeInfo { IsOnline = true } };

        var summary = Build(Base(nodes: nodes));

        Assert.Contains("1/1 node", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_ShowsSessionsAndActiveCount()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "a", Status = "active" },
            new SessionInfo { Key = "b", Status = "idle" },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Contains("2 sessions (1 active)", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_ShowsCostWhenConnected()
    {
        var usage = new GatewayUsageInfo { CostUsd = 1.25, TotalTokens = 5000 };

        var summary = Build(Base(ConnectionStatus.Connected, usage: usage));

        Assert.Contains("$1.25", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_OmitsUsageWhenDisconnected()
    {
        var usage = new GatewayUsageInfo { CostUsd = 1.25, TotalTokens = 5000 };
        var nodes = new[] { new GatewayNodeInfo { IsOnline = true } };

        var summary = Build(Base(ConnectionStatus.Disconnected, nodes: nodes, usage: usage));

        Assert.DoesNotContain("$", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_FallsBackToSessionTokensWhenNoUsage()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "a", Status = "active", InputTokens = 1500, OutputTokens = 500 },
        };

        var summary = Build(Base(ConnectionStatus.Connected, sessions: sessions));

        Assert.Contains("2.0K tokens", summary.MetricsLine);
    }

    [Fact]
    public void MetricsLine_SubCentCost_FallsBackToTokens()
    {
        var usage = new GatewayUsageInfo { CostUsd = 0.004, TotalTokens = 2500 };

        var summary = Build(Base(ConnectionStatus.Connected, usage: usage));

        Assert.Contains("2.5K tokens", summary.MetricsLine);
        Assert.DoesNotContain("$0.00", summary.MetricsLine);
    }

    // ── Active session ──

    [Fact]
    public void ActiveSession_NullWhenNoSessions()
    {
        var summary = Build(Base());

        Assert.Null(summary.ActiveSession);
        Assert.False(summary.HasActiveSession);
    }

    [Fact]
    public void ActiveSession_PrefersMainSession()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "sub", IsMain = false, DisplayName = "Sub" },
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main work" },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.NotNull(summary.ActiveSession);
        Assert.Equal("Main work", summary.ActiveSession!.Title);
    }

    [Fact]
    public void ActiveSession_FallsBackToMostRecentlyUpdated()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "old", IsMain = false, DisplayName = "Older", UpdatedAt = FixedNowUtc.AddMinutes(-30) },
            new SessionInfo { Key = "new", IsMain = false, DisplayName = "Newer", UpdatedAt = FixedNowUtc.AddMinutes(-1) },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal("Newer", summary.ActiveSession!.Title);
    }

    [Fact]
    public void ActiveSession_ComputesContextPercent()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "main", IsMain = true, DisplayName = "Main",
                Model = "claude-opus", InputTokens = 90_000, OutputTokens = 10_000,
                ContextTokens = 200_000,
            },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal(50, summary.ActiveSession!.ContextPercent);
        Assert.NotNull(summary.ActiveSession.Detail);
        Assert.Contains("claude-opus", summary.ActiveSession.Detail);
        Assert.DoesNotContain("ctx", summary.ActiveSession.Detail);
    }

    [Fact]
    public void ActiveSession_DefaultsContextWindowWhenUnset()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", InputTokens = 20_000, OutputTokens = 0 },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal(10, summary.ActiveSession!.ContextPercent);
    }

    // ── Edge cases: severity ordering, usage fallbacks, formatting ──

    [Fact]
    public void AuthFailure_OutranksPendingPairing()
    {
        var nodePairList = new PairingListInfo();
        nodePairList.Pending.Add(new PairingRequest());

        var summary = Build(Base(
            ConnectionStatus.Connected, authFailure: "token expired", nodePairList: nodePairList));

        Assert.Equal(TrayHealthSeverity.Critical, summary.Severity);
        Assert.Equal("Authentication failed", summary.Headline);
    }

    [Fact]
    public void Usage_NonNullZeroCost_FallsBackToUsageCostTotals()
    {
        var usage = new GatewayUsageInfo { CostUsd = 0, TotalTokens = 0 };
        var usageCost = new GatewayCostUsageInfo
        {
            Totals = new GatewayCostUsageTotalsInfo { TotalCost = 2.50, TotalTokens = 1234 },
        };
        var snapshot = Base(ConnectionStatus.Connected, usage: usage) with { UsageCost = usageCost };

        var summary = Build(snapshot);

        Assert.Contains("$2.50", summary.MetricsLine);
    }

    [Fact]
    public void Usage_NonNullZeroTokens_FallsBackToSessionTotals()
    {
        var usage = new GatewayUsageInfo { CostUsd = 0, TotalTokens = 0 };
        var sessions = new[]
        {
            new SessionInfo { Key = "a", Status = "active", TotalTokens = 3000 },
        };

        var summary = Build(Base(ConnectionStatus.Connected, usage: usage, sessions: sessions));

        Assert.Contains("3.0K tokens", summary.MetricsLine);
    }

    [Fact]
    public void Endpoint_SuppressesDefaultPort()
    {
        var summary = Build(Base(gatewayUrl: "https://gateway.example.com"));

        Assert.Equal("gateway.example.com", summary.Endpoint);
    }

    [Fact]
    public void Endpoint_KeepsExplicitNonDefaultPort()
    {
        var summary = Build(Base(gatewayUrl: "http://localhost:7070"));

        Assert.Equal("localhost:7070", summary.Endpoint);
    }

    [Fact]
    public void ActiveSession_PrefersActiveSubOverIdleMain()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Status = "idle" },
            new SessionInfo { Key = "sub", IsMain = false, DisplayName = "Worker", Status = "active" },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal("Worker", summary.ActiveSession!.Title);
        Assert.Equal("Active", summary.ActiveSession.Label);
    }

    [Fact]
    public void ActiveSession_PrefersActiveMainOverActiveSub()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "main", IsMain = true, DisplayName = "Main",
                Status = "active", UpdatedAt = FixedNowUtc.AddMinutes(-30)
            },
            new SessionInfo
            {
                Key = "sub", IsMain = false, DisplayName = "Worker",
                Status = "active", UpdatedAt = FixedNowUtc.AddMinutes(-1)
            },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal("Main", summary.ActiveSession!.Title);
        Assert.Equal("Active", summary.ActiveSession.Label);
    }

    [Fact]
    public void ActiveSession_LabelIsMainForIdleMain()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Status = "idle" },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal("Main", summary.ActiveSession!.Label);
    }

    [Fact]
    public void ActiveSession_ContextPercentClampsAtHundredWhenOverBudget()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "main", IsMain = true, DisplayName = "Main",
                InputTokens = 250_000, OutputTokens = 50_000, ContextTokens = 200_000,
            },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal(100, summary.ActiveSession!.ContextPercent);
    }

    [Fact]
    public void ActiveSession_ContextPercentRoundsToNearest()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "main", IsMain = true, DisplayName = "Main",
                // 179800 / 200000 = 89.9% -> rounds to 90
                InputTokens = 179_800, OutputTokens = 0, ContextTokens = 200_000,
            },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal(90, summary.ActiveSession!.ContextPercent);
    }

    [Fact]
    public void Heartbeat_HiddenWhenDisconnected()
    {
        var summary = Build(Base(ConnectionStatus.Disconnected, lastUpdated: FixedNowUtc.AddSeconds(-5)));

        Assert.Null(summary.Heartbeat);
    }

    [Fact]
    public void Heartbeat_FormatsHoursAndDays()
    {
        var hours = Build(Base(lastUpdated: FixedNowUtc.AddHours(-3)));
        Assert.Equal("Updated 3h ago", hours.Heartbeat);

        var days = Build(Base(lastUpdated: FixedNowUtc.AddDays(-2)));
        Assert.Equal("Updated 2d ago", days.Heartbeat);
    }

    [Fact]
    public void MetricsLine_FormatsMillionsTokenCount()
    {
        var usage = new GatewayUsageInfo { CostUsd = 0, TotalTokens = 2_500_000 };

        var summary = Build(Base(ConnectionStatus.Connected, usage: usage));

        Assert.Contains("2.5M tokens", summary.MetricsLine);
    }

    [Fact]
    public void ActiveSession_ContextPercentUsesTotalTokensWhenInputOutputZero()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "main", IsMain = true, DisplayName = "Main",
                TotalTokens = 100_000, InputTokens = 0, OutputTokens = 0, ContextTokens = 200_000,
            },
        };

        var summary = Build(Base(sessions: sessions));

        Assert.Equal(50, summary.ActiveSession!.ContextPercent);
    }

    [Fact]
    public void SessionUsedTokens_PrefersTotalTokensWhenPositive()
    {
        var withTotal = new SessionInfo { TotalTokens = 5000, InputTokens = 1, OutputTokens = 1 };
        var withoutTotal = new SessionInfo { TotalTokens = 0, InputTokens = 1200, OutputTokens = 300 };

        Assert.Equal(5000, TrayDashboardSummaryBuilder.SessionUsedTokens(withTotal));
        Assert.Equal(1500, TrayDashboardSummaryBuilder.SessionUsedTokens(withoutTotal));
    }

    [Fact]
    public void MetricsLine_MixedSessionTokenSources_SumsBothPerSession()
    {
        // One session reports TotalTokens only, the other input/output only.
        // The token fallback must count both (3000 + 1500 = 4500 = "4.5K").
        var usage = new GatewayUsageInfo { CostUsd = 0, TotalTokens = 0 };
        var sessions = new[]
        {
            new SessionInfo { Key = "a", Status = "active", TotalTokens = 3000 },
            new SessionInfo { Key = "b", Status = "idle", InputTokens = 1000, OutputTokens = 500 },
        };

        var summary = Build(Base(ConnectionStatus.Connected, usage: usage, sessions: sessions));

        Assert.Contains("4.5K tokens", summary.MetricsLine);
    }

    [Fact]
    public void SelectActiveSession_EmptyReturnsNull()
    {
        Assert.Null(TrayDashboardSummaryBuilder.SelectActiveSession(Array.Empty<SessionInfo>()));
    }
}
