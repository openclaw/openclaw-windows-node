using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Test factory that tracks created clients.
/// </summary>
internal sealed class TrackingClientFactory : IGatewayClientFactory
{
    public List<(string url, string token, bool isBootstrap, string? identityPath)> Created { get; } = new();

    public OpenClawGatewayClient Create(string gatewayUrl, string token, IOpenClawLogger logger, bool tokenIsBootstrapToken = false, string? identityPath = null)
    {
        Created.Add((gatewayUrl, token, tokenIsBootstrapToken, identityPath));
        return new OpenClawGatewayClient(gatewayUrl, token, logger, tokenIsBootstrapToken, identityPath: identityPath);
    }
}

public class GatewayConnectionServiceTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-svc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (GatewayConnectionService svc, GatewayRegistry reg, TrackingClientFactory factory) CreateService()
    {
        var dir = CreateTempDir();
        var reg = new GatewayRegistry(dir);
        var factory = new TrackingClientFactory();
        var svc = new GatewayConnectionService(reg, factory);
        return (svc, reg, factory);
    }

    // ── State: Initial ──

    [Fact]
    public void Initial_State_Is_Idle()
    {
        var (svc, _, _) = CreateService();
        var snap = svc.Snapshot;
        Assert.Equal(GatewayConnectionState.Idle, snap.Overall);
        Assert.Equal(GatewayRoleState.Idle, snap.Operator);
        Assert.Equal(GatewayRoleState.Disabled, snap.Node);
        Assert.Null(svc.OperatorClient);
    }

    // ── ApplySetupCode ──

    [Fact]
    public void ApplySetupCode_Creates_Gateway_Record()
    {
        var (svc, reg, _) = CreateService();
        var result = svc.ApplySetupCode("ws://localhost:18789", "bootstrap-tok-123");

        Assert.True(result);
        var gw = reg.GetActive();
        Assert.NotNull(gw);
        Assert.Equal("ws://localhost:18789", gw.Url);
        Assert.Equal("bootstrap-tok-123", gw.BootstrapToken);
        Assert.Null(gw.OperatorDeviceToken);
    }

    [Fact]
    public void ApplySetupCode_Clears_Previous_Record()
    {
        var (svc, reg, _) = CreateService();
        reg.AddOrUpdate(new GatewayRecord
        {
            Id = "localhost-18789",
            Url = "ws://localhost:18789",
            OperatorDeviceToken = "old-op-token",
            NodeDeviceToken = "old-node-token",
        });

        svc.ApplySetupCode("ws://localhost:18789", "fresh-bootstrap");

        var gw = reg.GetActive();
        Assert.NotNull(gw);
        Assert.Equal("fresh-bootstrap", gw.BootstrapToken);
        Assert.Null(gw.OperatorDeviceToken);
        Assert.Null(gw.NodeDeviceToken);
    }

    [Fact]
    public void ApplySetupCode_EmptyUrl_ReturnsFalse()
    {
        var (svc, _, _) = CreateService();
        Assert.False(svc.ApplySetupCode("", "tok"));
        Assert.False(svc.ApplySetupCode(null!, "tok"));
    }

    // ── SetCredential ──

    [Fact]
    public void SetCredential_SharedGatewayToken()
    {
        var (svc, reg, _) = CreateService();
        svc.SetCredential("ws://gw:1234", "shared-secret", GatewayCredentialKind.SharedGatewayToken);

        var gw = reg.GetActive();
        Assert.NotNull(gw);
        Assert.Equal("shared-secret", gw.SharedGatewayToken);
    }

    [Fact]
    public void SetCredential_BootstrapToken()
    {
        var (svc, reg, _) = CreateService();
        svc.SetCredential("ws://gw:1234", "bt-123", GatewayCredentialKind.BootstrapToken);

        var gw = reg.GetActive();
        Assert.Equal("bt-123", gw!.BootstrapToken);
    }

    [Fact]
    public void SetCredential_OperatorDeviceToken_ClearsBootstrap()
    {
        var (svc, reg, _) = CreateService();
        svc.SetCredential("ws://gw:1234", "bt-123", GatewayCredentialKind.BootstrapToken);
        svc.SetCredential("ws://gw:1234", "op-device-tok", GatewayCredentialKind.OperatorDeviceToken);

        var gw = reg.GetActive();
        Assert.Equal("op-device-tok", gw!.OperatorDeviceToken);
        Assert.Null(gw.BootstrapToken); // Cleared after device token received
    }

    [Fact]
    public void SetCredential_PreservesExistingFields()
    {
        var (svc, reg, _) = CreateService();
        svc.SetCredential("ws://gw:1234", "shared", GatewayCredentialKind.SharedGatewayToken);
        svc.SetCredential("ws://gw:1234", "op-tok", GatewayCredentialKind.OperatorDeviceToken);

        var gw = reg.GetActive();
        Assert.Equal("shared", gw!.SharedGatewayToken);
        Assert.Equal("op-tok", gw.OperatorDeviceToken);
    }

    // ── ConnectOperatorAsync ──

    [Fact]
    public async Task ConnectOperator_WithBootstrapToken_CreatesClient()
    {
        var (svc, reg, factory) = CreateService();
        svc.ApplySetupCode("ws://localhost:18789", "bootstrap-tok");

        await svc.ConnectOperatorAsync();

        Assert.Single(factory.Created);
        Assert.Equal("ws://localhost:18789", factory.Created[0].url);
        Assert.Equal("bootstrap-tok", factory.Created[0].token);
        Assert.True(factory.Created[0].isBootstrap);
        Assert.NotNull(svc.OperatorClient);
    }

    [Fact]
    public async Task ConnectOperator_WithOperatorToken_PrefersOverBootstrap()
    {
        var (svc, reg, factory) = CreateService();
        reg.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1234",
            Url = "ws://gw:1234",
            OperatorDeviceToken = "device-tok",
            BootstrapToken = "should-not-use",
        });

        await svc.ConnectOperatorAsync();

        Assert.Single(factory.Created);
        Assert.Equal("device-tok", factory.Created[0].token);
        Assert.False(factory.Created[0].isBootstrap);
    }

    [Fact]
    public async Task ConnectOperator_NoCredential_DoesNothing()
    {
        var (svc, reg, factory) = CreateService();
        reg.AddOrUpdate(new GatewayRecord { Id = "gw", Url = "ws://gw:1234" });

        await svc.ConnectOperatorAsync();

        Assert.Empty(factory.Created);
        Assert.Null(svc.OperatorClient);
    }

    [Fact]
    public async Task ConnectOperator_NoActiveGateway_DoesNothing()
    {
        var (svc, _, factory) = CreateService();

        await svc.ConnectOperatorAsync();

        Assert.Empty(factory.Created);
    }

    // ── State transitions ──

    [Fact]
    public async Task ConnectOperator_SetsState_Connecting()
    {
        var (svc, _, _) = CreateService();
        svc.ApplySetupCode("ws://gw:1234", "tok");

        var states = new List<GatewayConnectionState>();
        svc.StateChanged += (old, @new, snap) => states.Add(@new);

        await svc.ConnectOperatorAsync();

        Assert.Contains(GatewayConnectionState.Connecting, states);
    }

    // ── Token storage ──

    [Fact]
    public void StoreOperatorDeviceToken_UpdatesRegistry()
    {
        var (svc, reg, _) = CreateService();
        svc.ApplySetupCode("ws://gw:1234", "bootstrap");

        svc.StoreOperatorDeviceToken("new-device-tok");

        var gw = reg.GetActive();
        Assert.Equal("new-device-tok", gw!.OperatorDeviceToken);
        Assert.Null(gw.BootstrapToken); // Cleared
    }

    [Fact]
    public void StoreNodeDeviceToken_UpdatesRegistry()
    {
        var (svc, reg, _) = CreateService();
        svc.ApplySetupCode("ws://gw:1234", "bootstrap");

        svc.StoreNodeDeviceToken("node-tok");

        var gw = reg.GetActive();
        Assert.Equal("node-tok", gw!.NodeDeviceToken);
    }

    // ── Chat credentials ──

    [Fact]
    public void ResolveChatCredentials_PrefersSharedToken()
    {
        var (svc, reg, _) = CreateService();
        reg.AddOrUpdate(new GatewayRecord
        {
            Id = "gw",
            Url = "ws://gw:1234",
            SharedGatewayToken = "shared-secret",
            OperatorDeviceToken = "device-tok",
        });

        var cred = svc.ResolveChatCredentials();
        Assert.NotNull(cred);
        Assert.Equal("shared-secret", cred.Value.token);
        Assert.Equal("shared", cred.Value.source);
    }

    [Fact]
    public void ResolveChatCredentials_FallsBackToOperatorToken()
    {
        var (svc, reg, _) = CreateService();
        reg.AddOrUpdate(new GatewayRecord
        {
            Id = "gw",
            Url = "ws://gw:1234",
            OperatorDeviceToken = "device-tok",
        });

        var cred = svc.ResolveChatCredentials();
        Assert.NotNull(cred);
        Assert.Equal("device-tok", cred.Value.token);
        Assert.Equal("operator", cred.Value.source);
    }

    [Fact]
    public void ResolveChatCredentials_NoTokens_ReturnsNull()
    {
        var (svc, reg, _) = CreateService();
        reg.AddOrUpdate(new GatewayRecord { Id = "gw", Url = "ws://gw:1234" });

        Assert.Null(svc.ResolveChatCredentials());
    }

    // ── Disconnect ──

    [Fact]
    public async Task Disconnect_ResetsState()
    {
        var (svc, _, _) = CreateService();
        svc.ApplySetupCode("ws://gw:1234", "tok");
        await svc.ConnectOperatorAsync();

        await svc.DisconnectAsync();

        Assert.Null(svc.OperatorClient);
        Assert.Equal(GatewayRoleState.Idle, svc.Snapshot.Operator);
    }

    // ── Reconnect disposes old client ──

    [Fact]
    public async Task Reconnect_CreatesNewClient()
    {
        var (svc, _, factory) = CreateService();
        svc.ApplySetupCode("ws://gw:1234", "tok");

        await svc.ConnectOperatorAsync();
        await svc.ReconnectAsync();

        Assert.Equal(2, factory.Created.Count); // Old + new
    }

    // ── DeriveOverall ──

    [Theory]
    [InlineData(GatewayRoleState.Connected, GatewayRoleState.Connected, true, GatewayConnectionState.Ready)]
    [InlineData(GatewayRoleState.Connected, GatewayRoleState.Disabled, true, GatewayConnectionState.Ready)]
    [InlineData(GatewayRoleState.Connecting, GatewayRoleState.Disabled, true, GatewayConnectionState.Connecting)]
    [InlineData(GatewayRoleState.Error, GatewayRoleState.Disabled, true, GatewayConnectionState.Error)]
    [InlineData(GatewayRoleState.PairingRequired, GatewayRoleState.Disabled, true, GatewayConnectionState.PairingRequired)]
    [InlineData(GatewayRoleState.Idle, GatewayRoleState.Disabled, true, GatewayConnectionState.Configured)]
    [InlineData(GatewayRoleState.Idle, GatewayRoleState.Disabled, false, GatewayConnectionState.Idle)]
    [InlineData(GatewayRoleState.Connected, GatewayRoleState.Connecting, true, GatewayConnectionState.Connecting)]
    [InlineData(GatewayRoleState.Connected, GatewayRoleState.PairingRequired, true, GatewayConnectionState.PairingRequired)]
    public void DeriveOverall_CorrectState(GatewayRoleState op, GatewayRoleState node, bool hasCred, GatewayConnectionState expected)
    {
        Assert.Equal(expected, GatewayConnectionSnapshot.DeriveOverall(op, node, hasCred));
    }
}
