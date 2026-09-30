using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupGatewaySessionBindingTests
{
    private const string Token = "setup-binding-fixture-token";
    private const string Model = "fixture/restart-model";
    private static object SetupReply(string method, JsonElement parameters) => method switch
    {
        "openclaw.setup.detect" => new
        {
            candidates = new[] { new { kind = "existing", label = "Fixture", detail = "", modelRef = Model, recommended = true } },
            manualProviders = Array.Empty<object>(), workspace = "fixture", setupComplete = true, configuredModel = Model,
        },
        "openclaw.setup.activate.start" => new
        {
            sessionId = parameters.GetProperty("sessionId").GetString(), done = true, status = "done",
            modelActivation = new { modelRef = Model, gatewayRestartRequired = true },
        },
        "openclaw.setup.verify" => new { ok = true, modelRef = Model, latencyMs = 1 },
        _ => throw new InvalidOperationException("Unexpected fixture request"),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdOnlyLocalAiSelectionRetainsTypedRejectionBeforeOrdinaryOrNativeTransport(bool native)
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(SetupReply), Token);
        var registry = new GatewayRegistry(temp.Path);
        var active = registry.AddOrUpdate(new()
        {
            Id = "other", Url = server.Endpoint.ToString(),
            NativePackageFamilyName = native ? "OpenClaw.Gateway_123456789abcd" : null,
            NativeRuntimeContract = native ? OpenClaw.Connection.NativeGateway.NativeGatewayPackageClient.IsolatedContract : null,
        });
        registry.SetActive(active.Id);
        registry.Save();
        Assert.Throws<LocalAiSelectionRejectedException>(() =>
            SetupGatewaySession.RequireExpectedGateway(active, "selected", endpointBinding: null));
        if (native)
        {
            await using var manager = new GatewayConnectionManager(
                new CredentialResolver(DeviceIdentityFileReader.Instance), new GatewayClientFactory(), registry, NullLogger.Instance);
            await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() =>
                GatewayAiSetupTransport.BorrowNativeAsync(temp.Path, manager, "selected", default));
        }
        else
            await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() =>
                SetupGatewaySession.ConnectAsync(temp.Path, expectedGatewayId: "selected"));
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task RealSessionRestart_ClearsLiveHandshakeThenReverifiesSameAuthorityWithoutActivationReplay()
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(SetupReply), Token);
        var registry = new GatewayRegistry(temp.Path);
        var record = registry.AddOrUpdate(new() { Id = "gateway", Url = server.Endpoint.ToString(), SharedGatewayToken = Token });
        registry.SetActive(record.Id);
        registry.Save();
        GatewayAiSetupClient? client = null;
        await using var session = await SetupGatewaySession.ConnectAsync(temp.Path,
            () => client?.RequiresReconciliation == true, expectedGatewayId: record.Id,
            expectedEndpointBinding: GatewayDashboardBinding.Capture(record));
        client = new GatewayAiSetupClient(new GatewayAiSetupTransport(session.Client, session.GetRoute,
            requireRestartAuthority: session.RequireRestartAuthority));
        var controller = new GatewayAiSetupController(client);
        await client.DetectAsync();
        client.SelectCandidate("existing", Model);
        await controller.StartSelectedAsync(null, null);
        var disconnected = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? sender, ConnectionStatus status)
        {
            if (status != ConnectionStatus.Disconnected || disconnected.Task.IsCompleted) return;
            try
            {
                Assert.False(session.Client.HasHandshakeSnapshot);
                Assert.Null(session.Client.MainSessionKey);
                Assert.Null(session.Client.AuthenticatedSigningDeviceId);
                session.RequireRestartAuthority(client.Route);
                disconnected.TrySetResult(controller.WaitForExpectedRestartAsync());
            }
            catch (Exception error) { disconnected.TrySetException(error); }
        }
        session.Client.StatusChanged += OnStatus;
        try
        {
            await server.CloseConnectionsAsync();
            var waiting = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await waiting.WaitAsync(TimeSpan.FromSeconds(35));
            Assert.True((await client.VerifyAsync()).Ok);
            Assert.Equal(client.Route, session.GetRoute());
            Assert.Single(server.Requests, request => request.Method == "openclaw.setup.activate.start");
            Assert.Equal(record.Id, client.GetVerifiedCompletion().GatewayId);
        }
        finally { session.Client.StatusChanged -= OnStatus; }
    }

    [Theory]
    [InlineData("gateway", false)]
    [InlineData("endpoint", false)]
    [InlineData("ssh", false)]
    [InlineData("gateway", true)]
    [InlineData("endpoint", true)]
    [InlineData("runtime", true)]
    public async Task CommittedBindingRejectsChangedSelectionBeforeAnyConnectionOrRpc(string change, bool native)
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(SetupReply), Token);
        var registry = new GatewayRegistry(temp.Path);
        var original = new GatewayRecord
        {
            Id = "committed-a", Url = server.Endpoint.ToString(), SharedGatewayToken = Token,
            NativePackageFamilyName = native ? "OpenClaw.Gateway_123456789abcd" : null,
            NativeRuntimeContract = native ? OpenClaw.Connection.NativeGateway.NativeGatewayPackageClient.IsolatedContract : null,
        };
        registry.AddOrUpdate(original);
        registry.SetActive(original.Id);
        registry.Save();
        var binding = GatewayDashboardBinding.Capture(original);
        var changed = change switch
        {
            "gateway" => original with { Id = "gateway-b" },
            "endpoint" => original with { Url = server.Endpoint + "different-path" },
            "runtime" => original with { NativeRuntimeContract = null },
            _ => original with { SshTunnel = new("other", "ssh.example", 18789, server.Endpoint.Port) },
        };
        registry.AddOrUpdate(changed);
        registry.SetActive(changed.Id);
        registry.Save();
        if (native)
        {
            await using var manager = new GatewayConnectionManager(
                new CredentialResolver(DeviceIdentityFileReader.Instance), new GatewayClientFactory(), registry, NullLogger.Instance);
            await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
                GatewayAiSetupTransport.BorrowNativeAsync(temp.Path, manager, original.Id, default, binding));
        }
        else
            await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
                SetupGatewaySession.ConnectAsync(temp.Path, expectedGatewayId: original.Id, expectedEndpointBinding: binding));
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.Requests);
    }

    private static GatewayRecord Original => new()
    {
        Id = "gateway-a", Url = "wss://gateway.example/control/",
        SshTunnel = new("user", "ssh.example", 18789, 19001),
    };

    [Theory]
    [InlineData("id")]
    [InlineData("url")]
    [InlineData("ssh-host")]
    [InlineData("ssh-local-port")]
    [InlineData("ssh-user")]
    public async Task ChangedRegistryDuringConnect_CannotRelabelAlreadyCreatedClient(string change)
    {
        GatewayRecord active = Original;
        var captured = new SetupGatewaySessionBinding(active);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = AfterConnectionAsync();
        active = change switch
        {
            "id" => active with { Id = "gateway-b" }, // Same URL is still a different Gateway.
            "url" => active with { Url = "wss://different.example/" },
            "ssh-host" => active with { SshTunnel = active.SshTunnel! with { Host = "different.example" } },
            "ssh-local-port" => active with { SshTunnel = active.SshTunnel! with { LocalPort = 19002 } },
            _ => active with { SshTunnel = active.SshTunnel! with { User = "different-user" } },
        };
        connecting.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => route);
        Assert.Equal("gateway-a", captured.GatewayId);
        Assert.Equal("ws://localhost:19001", captured.Endpoint);

        async Task<GatewayAiSetupRoute> AfterConnectionAsync()
        {
            captured.RequireCurrent(active);
            await connecting.Task;
            // This is the post-connect/GetRoute seam, before GatewayAiSetupClient captures its route.
            return captured.GetRoute(active, "identity-a", "agent:primary:main", "device-a");
        }
    }

    [Fact]
    public void PersistedBinding_RejectsOnlyLocalForwardPortDrift()
    {
        var changed = Original with { SshTunnel = Original.SshTunnel! with { LocalPort = 19002 } };
        Assert.NotEqual(GatewayDashboardBinding.Capture(Original), GatewayDashboardBinding.Capture(changed));
    }

    [Fact]
    public void LastConnectedAndRotatedCredentials_DoNotChangeCapturedAuthority()
    {
        var binding = new SetupGatewaySessionBinding(Original);
        var current = Original with { LastConnected = DateTime.UtcNow, SharedGatewayToken = "rotated" };
        var route = binding.GetRoute(current, "identity-a", "agent:primary:main", "device-a");
        Assert.Equal("gateway-a", route.GatewayId);
        Assert.Equal("primary", route.AgentId);
        Assert.DoesNotContain("identity-a", route.AuthorityId);
        Assert.Equal(SetupCompletionAuthority.CaptureIdentity("identity-a", "device-a"), route.IdentityBinding);
        Assert.Equal("agent:primary:main", route.SessionKey);
        Assert.Equal(GatewayDashboardBinding.Capture(Original), route.EndpointBinding);
    }

    [Fact]
    public void MissingActiveGateway_FailsAdmissionAndRouteProjection()
    {
        var binding = new SetupGatewaySessionBinding(Original);
        Assert.Throws<InvalidOperationException>(() => binding.RequireCurrent(null));
        Assert.Throws<InvalidOperationException>(() => binding.GetRoute(null, "identity-a", null, null));
    }

    [Fact]
    public void ProductionSession_GuardsAdmissionHandshakePostConnectAndRequests()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); root is null && directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                root = directory.FullName;
        }
        Assert.NotNull(root);
        var session = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "SetupGatewaySession.cs"))
            .Replace("\r\n", "\n");
        Assert.Contains("var binding = new SetupGatewaySessionBinding(record);", session);
        Assert.Contains("client.ReconnectAuthorizationAsync = AuthorizeHandshakeAsync;", session);
        Assert.Contains("client.HandshakeAuthorizationAsync = AuthorizeHandshakeAsync;", session);
        Assert.Contains("RequireCurrentGateway();\n        var client = new OpenClawGatewayClient", session);
        Assert.Contains("RequireCurrentGateway();\n            await client.ConnectAsync()", session);
        Assert.Contains("RequireCurrentGateway();\n            return new(dataDir, record, binding, identityPath, client);", session);
        Assert.Contains("_binding.GetRoute(registry.GetActive()", session);
        Assert.Contains("catch\n        {\n            client.Dispose();\n            throw;", session);
        var transport = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "GatewayAiSetupTransport.cs"));
        Assert.True(transport.IndexOf("var route = routeProvider();", StringComparison.Ordinal) <
            transport.IndexOf("await client.SendWizardRequestAsync", StringComparison.Ordinal));
        Assert.Contains("if (routeProvider() != route)", transport);
    }
}
