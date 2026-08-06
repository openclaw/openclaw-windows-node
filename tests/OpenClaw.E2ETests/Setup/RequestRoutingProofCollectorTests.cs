using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

public sealed class RequestRoutingProofCollectorTests
{
    [Fact]
    public async Task ArmedConnect_RebindsToNewestReconnectAttempt()
    {
        var collector = new RequestRoutingCollector();
        var routeTask = collector.Arm("connect");

        collector.Observe(Registered("connect-old", "connect"));
        collector.Observe(Registered("connect-new", "connect"));
        collector.Observe(Classified("connect-old", "connect"));

        Assert.False(routeTask.IsCompleted);

        collector.Observe(Classified("connect-new", "connect"));
        var route = await routeTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("connect-new", route.RequestId);
        Assert.Equal("connect", route.Method);
        Assert.Equal(PendingResponseDisposition.Active, route.Disposition);
    }

    [Fact]
    public async Task PostReconnectGate_RequiresAllThreeTrackedRoutes()
    {
        var collector = new RequestRoutingCollector();
        var health = collector.Arm("health");
        var sessions = collector.Arm("sessions.list");
        var nodes = collector.Arm("node.list");
        var allRoutes = Task.WhenAll(health, sessions, nodes);

        collector.Observe(new(
            PendingRequestDiagnosticStage.ResponseClassified,
            string.Empty,
            null,
            null,
            PendingResponseDisposition.Ownerless));
        Assert.False(allRoutes.IsCompleted);

        Complete(collector, "health-id", "health");
        Complete(collector, "sessions-id", "sessions.list");
        Assert.False(allRoutes.IsCompleted);

        Complete(collector, "nodes-id", "node.list");
        var routes = await allRoutes.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            ["health", "sessions.list", "node.list"],
            routes.Select(route => route.Method).ToArray());
        Assert.All(
            routes,
            route => Assert.Equal(PendingResponseDisposition.Active, route.Disposition));
    }

    [Fact]
    public async Task ArmedNonConnectRoute_RemainsPinnedToOriginalRegistration()
    {
        var collector = new RequestRoutingCollector();
        var routeTask = collector.Arm("sessions.list");

        collector.Observe(Registered("proof-request", "sessions.list"));
        collector.Observe(Registered("background-refresh", "sessions.list"));
        collector.Observe(Classified("background-refresh", "sessions.list"));

        Assert.False(routeTask.IsCompleted);

        collector.Observe(Classified("proof-request", "sessions.list"));
        var route = await routeTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("proof-request", route.RequestId);
    }

    private static void Complete(
        RequestRoutingCollector collector,
        string requestId,
        string method)
    {
        collector.Observe(Registered(requestId, method));
        collector.Observe(Classified(requestId, method));
    }

    private static PendingRequestDiagnostic Registered(
        string requestId,
        string method) =>
        new(
            PendingRequestDiagnosticStage.Registered,
            requestId,
            method,
            PendingRequestKind.Method,
            null);

    private static PendingRequestDiagnostic Classified(
        string requestId,
        string method) =>
        new(
            PendingRequestDiagnosticStage.ResponseClassified,
            requestId,
            method,
            PendingRequestKind.Method,
            PendingResponseDisposition.Active);
}
