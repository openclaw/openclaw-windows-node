using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupGatewaySessionBindingTests
{
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
