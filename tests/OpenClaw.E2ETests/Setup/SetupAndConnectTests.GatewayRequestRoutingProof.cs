using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

public partial class SetupAndConnectTests
{
    [E2EFact]
    [Trait("Proof", "GatewayRequestRouting")]
    public async Task RealGateway_TrackedRequestRouting_SurvivesReconnect()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        Assert.False(string.IsNullOrWhiteSpace(gateway.SharedGatewayToken));

        var proofIdentityDir = Path.Combine(_fixture.DataDir, "proof-client-identity");
        CopyProofIdentity(
            Path.Combine(_fixture.DataDir, "gateways", gateway.ActiveId),
            proofIdentityDir);

        using var client = new OpenClawGatewayClient(
            gateway.GatewayUrl,
            gateway.SharedGatewayToken!,
            NullLogger.Instance,
            identityPath: proofIdentityDir);
        var routes = new RequestRoutingCollector();
        client.PendingRequests.DiagnosticObserver = routes.Observe;

        var transcript = new List<string>
        {
            "PROOF=REAL_DEPLOYED_GATEWAY_REQUEST_ROUTING",
            $"HEAD={ResolveProofHead()}",
            "PROVIDER_OR_MODEL_CALLS=NONE",
        };

        var connectRouteTask = routes.Arm("connect");
        var firstHandshake = await CaptureHandshakeAsync(client, client.ConnectAsync);
        var firstConnectRoute = await connectRouteTask.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHandshake(firstHandshake);
        transcript.Add($"GATEWAY_VERSION={firstHandshake.VersionText}");
        transcript.Add($"PROTOCOL={firstHandshake.Protocol}");
        transcript.Add(FormatRoute(1, firstConnectRoute, "hello-ok"));

        await routes.WaitForQuietAsync();

        var firstHealth = await CaptureTrackedResponseAsync(
            client,
            routes,
            "health",
            client.CheckHealthAsync);
        transcript.Add(FormatRoute(1, firstHealth, "response-owned"));

        var firstSessions = await CaptureTrackedResponseAsync(
            client,
            routes,
            "sessions.list",
            () => client.RequestSessionsAsync());
        transcript.Add(FormatRoute(1, firstSessions, "response-owned"));

        var firstNodes = await CaptureTrackedResponseAsync(
            client,
            routes,
            "node.list",
            client.RequestNodesAsync);
        transcript.Add(FormatRoute(1, firstNodes, "response-owned"));

        var disconnected = ArmDisconnect(client);
        var reconnectDiagnosticMarker = routes.Mark();
        var secondHandshakeTask = CaptureHandshakeAsync(
            client,
            async () =>
            {
                var restart = await _fixture.RunInWslAsync(
                    "openclaw gateway restart || systemctl --user restart openclaw-gateway.service",
                    TimeSpan.FromSeconds(60));
                Assert.False(restart.TimedOut, "real Gateway restart timed out");
                Assert.Equal(0, restart.ExitCode);
            });

        await disconnected.WaitAsync(TimeSpan.FromSeconds(60));
        transcript.Add("GENERATION=1 DISCONNECT=OBSERVED");
        var secondHandshake = await secondHandshakeTask;
        AssertHandshake(secondHandshake);
        Assert.Equal(firstHandshake.Protocol, secondHandshake.Protocol);
        Assert.Equal(firstHandshake.VersionText, secondHandshake.VersionText);
        transcript.Add(
            $"GENERATION=2 METHOD=connect RESPONSE={routes.DescribeConnectHandshakeSince(reconnectDiagnosticMarker)} " +
            "STATUS=hello-ok");

        await routes.WaitForQuietAsync();

        var secondHealth = await CaptureTrackedResponseAsync(
            client,
            routes,
            "health",
            client.CheckHealthAsync);
        transcript.Add(FormatRoute(2, secondHealth, "response-owned"));

        var secondSessions = await CaptureTrackedResponseAsync(
            client,
            routes,
            "sessions.list",
            () => client.RequestSessionsAsync());
        transcript.Add(FormatRoute(2, secondSessions, "response-owned"));

        var secondNodes = await CaptureTrackedResponseAsync(
            client,
            routes,
            "node.list",
            client.RequestNodesAsync);
        transcript.Add(FormatRoute(2, secondNodes, "response-owned"));

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(90));

        transcript.Add(routes.SawTombstonedResponse
            ? "REAL_GATEWAY_DUPLICATE_OR_LATE_RESPONSE=OBSERVED_AND_SUPPRESSED"
            : "REAL_GATEWAY_DUPLICATE_OR_LATE_RESPONSE=NOT_NATURALLY_OBSERVED");
        transcript.Add(
            "SYNTHETIC_DUPLICATE_LATE_PROOF=TrackedHealthResponse_PublishesOnceAndSuppressesDuplicate;" +
            "SendWizardRequestAsync_LateResponseAfterTimeout_DoesNotChangeOutcomeOrTracking;" +
            "OwnerlessHealthResponse_PreservesGenericHealthRouting;" +
            "TombstonedDuplicateStatePayloads_DoNotRepublishGenericState");
        transcript.Add("RESULT=PASS");

        var proofPath = Path.Combine(_fixture.ArtifactDir, "gateway-request-routing-proof.txt");
        await File.WriteAllLinesAsync(proofPath, transcript);
        Console.WriteLine(string.Join(Environment.NewLine, transcript));
    }

    private static async Task<RequestRoute> CaptureTrackedResponseAsync(
        OpenClawGatewayClient client,
        RequestRoutingCollector routes,
        string method,
        Func<Task> send)
    {
        var routeTask = routes.Arm(method);
        await send();
        var route = await routeTask.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(PendingRequestKind.Method, route.Kind);
        Assert.Equal(PendingResponseDisposition.Active, route.Disposition);
        return route;
    }

    private static async Task<GatewaySelfInfo> CaptureHandshakeAsync(
        OpenClawGatewayClient client,
        Func<Task> connect)
    {
        var completion = new TaskCompletionSource<GatewaySelfInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GatewaySelfInfo? latestSelf = null;
        EventHandler<GatewaySelfInfo> selfHandler = (_, info) =>
        {
            if (info.Protocol.HasValue)
                latestSelf = info;
        };
        EventHandler handshakeHandler = (_, _) =>
        {
            if (latestSelf is not null)
                completion.TrySetResult(latestSelf);
        };

        client.GatewaySelfUpdated += selfHandler;
        client.HandshakeSucceeded += handshakeHandler;
        try
        {
            await connect();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }
        finally
        {
            client.GatewaySelfUpdated -= selfHandler;
            client.HandshakeSucceeded -= handshakeHandler;
        }
    }

    private static Task ArmDisconnect(OpenClawGatewayClient client)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConnectionStatus>? handler = null;
        handler = (_, status) =>
        {
            if (status is not (ConnectionStatus.Disconnected or ConnectionStatus.Error))
                return;

            client.StatusChanged -= handler;
            completion.TrySetResult();
        };
        client.StatusChanged += handler;
        return completion.Task;
    }

    private static void CopyProofIdentity(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(
                sourceFile,
                Path.Combine(destinationDir, Path.GetFileName(sourceFile)),
                overwrite: true);
        }
    }

    private static void AssertHandshake(GatewaySelfInfo info)
    {
        Assert.False(string.IsNullOrWhiteSpace(info.ServerVersion));
        Assert.NotNull(info.Protocol);
    }

    private static string FormatRoute(
        int generation,
        RequestRoute route,
        string status) =>
        $"GENERATION={generation} METHOD={route.Method} ID_HASH={HashId(route.RequestId)} " +
        $"OWNER={route.Kind} RESPONSE={route.Disposition} STATUS={status}";

    private static string HashId(string requestId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)))
            .ToLowerInvariant()[..12];

    private static string ResolveProofHead() =>
        Environment.GetEnvironmentVariable("OPENCLAW_PROOF_HEAD")
        ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
        ?? "local-worktree";

}
