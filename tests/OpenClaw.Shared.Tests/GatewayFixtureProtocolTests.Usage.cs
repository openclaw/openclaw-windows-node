using System.Collections.Concurrent;
using System.Text.Json;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UsageRefresh_InvalidPeriodIsRejectedBeforeConnectionOrWireDispatch(int days)
    {
        using var identity = CreateIdentity();
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:1", "fixture-token", identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RefreshUsageAsync(days));
    }

    // Source-shaped synthetic payloads, not live/package proof:
    // openclaw/openclaw@098e991549b7986bcfaebdbe427aeaaf95afb926
    // src/gateway/server-methods/usage.ts and apps/macos/Sources/OpenClaw/StatusMenuSummaries.swift.
    private static object UsageReply(string method, JsonElement parameters)
    {
        if (method == "usage.status")
        {
            Assert.Empty(parameters.EnumerateObject());
            return new
            {
                updatedAt = 1_787_227_200_000L,
                providers = new[]
                {
                    new
                    {
                        provider = "fixture", displayName = "Fixture provider", plan = "Synthetic",
                        windows = new[] { new { label = "Daily", usedPercent = 42.5, resetAt = 1_787_313_600_000L } }
                    }
                }
            };
        }
        if (method == "usage")
            return new { requestCount = 42, totalTokens = 1200, cost = 0.25 };

        Assert.Equal("usage.cost", method);
        Assert.Single(parameters.EnumerateObject());
        var days = parameters.GetProperty("days").GetInt32();
        Assert.Contains(days, new[] { 7, 30 });
        return new
        {
            updatedAt = 1_787_227_200_000L, days,
            totals = new { input = 900, output = 300, totalTokens = 1200, totalCost = days == 7 ? 0.07 : 0.30 },
            daily = new[] { new { date = "2026-08-20", totalTokens = 1200, totalCost = 0.01 } }
        };
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    public async Task UsageRefresh_OneStatusAndOneSelectedPeriodCostWithoutGlobalCacheDelivery(int days)
    {
        var calls = new ConcurrentQueue<(string Method, int? Days)>();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage((method, p) =>
        {
            calls.Enqueue((method, method == "usage.cost" ? p.GetProperty("days").GetInt32() : null));
            return UsageReply(method, p);
        }), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        calls.Clear();
        var statusEvents = 0;
        var costEvents = 0;
        connected.Client.UsageStatusUpdated += (_, _) => statusEvents++;
        connected.Client.UsageCostUpdated += (_, _) => costEvents++;

        var result = await connected.Client.RefreshUsageAsync(days).WaitAsync(Deadline);

        Assert.Equal(2, calls.Count);
        Assert.Single(calls, call => call == ("usage.status", null));
        Assert.Single(calls, call => call == ("usage.cost", days));
        Assert.Null(result.StatusError);
        Assert.Null(result.CostError);
        var provider = Assert.Single(Assert.IsType<GatewayUsageStatusInfo>(result.Status).Providers);
        Assert.Equal("Fixture provider", provider.DisplayName);
        Assert.Equal(42.5, Assert.Single(provider.Windows).UsedPercent);
        var cost = Assert.IsType<GatewayCostUsageInfo>(result.Cost);
        Assert.Equal(days, cost.Days);
        Assert.Equal(days == 7 ? 0.07 : 0.30, cost.Totals.TotalCost);
        Assert.Equal(1200, cost.Totals.TotalTokens);
        Assert.Equal("2026-08-20", Assert.Single(cost.Daily).Date);
        Assert.Equal(0, statusEvents);
        Assert.Equal(0, costEvents);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("usage.status", "unknown method: usage.status")]
    [InlineData("usage.cost", "unknown method: usage.cost")]
    [InlineData("usage.status", "missing scope: operator.read")]
    [InlineData("usage.cost", "missing scope: operator.read")]
    [InlineData("usage.status", "Temporary fixture failure")]
    [InlineData("usage.cost", "Temporary fixture failure")]
    [InlineData("usage.cost", "Aggregate usage includes sessions hidden by your operator role")]
    public async Task UsageRefresh_ErrorFallbackAndPollingSuppressionAreMethodSpecific(string failingMethod, string error)
    {
        var fail = false;
        var calls = new ConcurrentQueue<string>();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage((method, p) =>
        {
            calls.Enqueue(method);
            if (fail && method == failingMethod)
                throw new FixtureRequestException("FIXTURE_ERROR", error);
            return UsageReply(method, p);
        }), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        calls.Clear();
        fail = true;
        var result = await connected.Client.RefreshUsageAsync(7).WaitAsync(Deadline);
        Assert.Equal(error, failingMethod == "usage.status" ? result.StatusError : result.CostError);
        if (!error.StartsWith("missing scope"))
        {
            if (failingMethod == "usage.status") Assert.NotNull(result.Cost);
            else Assert.NotNull(result.Status);
        }
        if (failingMethod == "usage.status" && error.StartsWith("unknown"))
        {
            Assert.Equal(42, Assert.IsType<GatewayUsageInfo>(result.Legacy).RequestCount);
            Assert.Single(calls, method => method == "usage");
            Assert.NotNull(result.Cost);
        }
        else Assert.Null(result.Legacy);
        Assert.Single(calls, method => method == failingMethod);

        calls.Clear();
        var retry = await connected.Client.RefreshUsageAsync(30).WaitAsync(Deadline);
        if (error.StartsWith("missing scope"))
        {
            Assert.Empty(calls);
            Assert.Contains("operator.read", retry.StatusError);
            Assert.Contains("operator.read", retry.CostError);
        }
        else if (error.StartsWith("unknown"))
        {
            Assert.DoesNotContain(failingMethod, calls);
            if (failingMethod == "usage.status")
                Assert.Equal(42, Assert.IsType<GatewayUsageInfo>(retry.Legacy).RequestCount);
            else Assert.NotNull(retry.Status);
            calls.Clear();
            if (failingMethod == "usage.status")
                await connected.Client.RequestUsageStatusAsync();
            else
                await connected.Client.RequestUsageCostAsync(7);
            await connected.Client.SendWizardRequestAsync("health");
            Assert.Empty(calls);
        }
        else Assert.Contains(failingMethod, calls);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task UsageRefresh_OutOfOrderPeriodsRemainBoundToTheirOwnReplies()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage(UsageReply), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var occurrence = server.Requests.Count(r => r.Method == "usage.cost") + 1;
        server.HoldUsageCost(7);
        var seven = connected.Client.RefreshUsageAsync(7);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("usage.cost", null, occurrence, deadline.Token);
        var thirty = await connected.Client.RefreshUsageAsync(30).WaitAsync(Deadline);
        Assert.Equal(30, thirty.Cost!.Days);
        Assert.False(seven.IsCompleted);
        server.ReleaseUsageCost(7);
        Assert.Equal(7, (await seven.WaitAsync(Deadline)).Cost!.Days);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("usage.status")]
    [InlineData("usage.cost")]
    public async Task UsageRefresh_MalformedSuccessIsAnErrorNotAnEmptySummary(string invalidMethod)
    {
        var invalid = false;
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage((method, p) =>
            invalid && method == invalidMethod ? new { } : UsageReply(method, p)), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        invalid = true;
        var result = await connected.Client.RefreshUsageAsync(7).WaitAsync(Deadline);
        if (invalidMethod == "usage.status")
        {
            Assert.Null(result.Status);
            Assert.Equal("Invalid usage.status response.", result.StatusError);
            Assert.NotNull(result.Cost);
        }
        else
        {
            Assert.Null(result.Cost);
            Assert.Equal("Invalid usage.cost response.", result.CostError);
            Assert.NotNull(result.Status);
        }
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task UsageRefresh_DisconnectRejectsPendingPeriodRatherThanPublishingRetiredData()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage(UsageReply), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.HoldUsageCost(7);
        var occurrence = server.Requests.Count(r => r.Method == "usage.cost") + 1;
        var refresh = connected.Client.RefreshUsageAsync(7);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("usage.cost", null, occurrence, deadline.Token);
        await connected.Client.DisconnectAsync();
        Assert.NotNull(await Record.ExceptionAsync(() => refresh.WaitAsync(Deadline)));
        server.ReleaseUsageCost(7);
    }

    [Theory]
    [InlineData("unknown method: usage.cost")]
    [InlineData("missing scope: operator.read")]
    public async Task UsageRefresh_ReconnectDoesNotCarryUnsupportedOrDeniedFlags(string error)
    {
        var fail = false;
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsage((method, p) =>
        {
            if (fail && method == "usage.cost")
                throw new FixtureRequestException("FIXTURE_ERROR", error);
            return UsageReply(method, p);
        }), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        fail = true;
        Assert.Equal(error, (await connected.Client.RefreshUsageAsync(7).WaitAsync(Deadline)).CostError);
        await connected.Client.DisconnectAsync();
        fail = false;
        var nextHandshake = Signal<bool>();
        connected.Client.HandshakeSucceeded += (_, _) => nextHandshake.TrySetResult(true);
        await connected.Client.ConnectAsync();
        await nextHandshake.Task.WaitAsync(Deadline);
        var result = await connected.Client.RefreshUsageAsync(7).WaitAsync(Deadline);
        Assert.Null(result.StatusError);
        Assert.Null(result.CostError);
        Assert.Equal(7, result.Cost!.Days);
        Assert.NotNull(result.Status);
        Assert.Empty(server.UnexpectedRequests);
    }
}
