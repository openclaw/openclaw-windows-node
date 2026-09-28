using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class DashboardCredentialGateTests
{
    [Fact]
    public void SharedToken_IsAppendedWhenItIsThePinnedCredential()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceSharedGatewayToken,
            isBootstrapToken: false,
            resolvedToken: "shared-secret",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.PinMismatch);
        Assert.True(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, decision.CredentialSource);
        Assert.Equal("shared-secret", decision.Token);
    }

    [Fact]
    public void DeviceToken_IsNeverAppendedEvenWhenASharedTokenIsStored()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceDeviceToken,
            isBootstrapToken: false,
            resolvedToken: "device-token",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.PinMismatch);
        Assert.False(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceDeviceToken, decision.CredentialSource);
        Assert.Null(decision.Token);
    }

    [Fact]
    public void BootstrapToken_IsNotAppended()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceBootstrapToken,
            isBootstrapToken: true,
            resolvedToken: "bootstrap-token",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceBootstrapToken, decision.CredentialSource);
        Assert.Null(decision.Token);
    }

    [Theory]
    [InlineData(false, true, "shared-secret")]
    [InlineData(true, false, "shared-secret")]
    [InlineData(true, true, "changed-secret")]
    public void PinMismatch_ReturnsNoTokenUrlMaterial(
        bool pinMatches, bool samePinnedRecord, string pinnedSharedToken)
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches,
            samePinnedRecord,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceSharedGatewayToken,
            isBootstrapToken: false,
            resolvedToken: "shared-secret",
            pinnedSharedToken);

        Assert.True(decision.PinMismatch);
        Assert.False(decision.AppendToken);
        Assert.Null(decision.Token);
        Assert.Equal("none", decision.CredentialSource);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void PinnedSshHttpResolver_OnlyAppendsSharedToken(
        bool hasSharedToken, bool hasDeviceToken)
    {
        using var temp = new TempDirectory();
        var record = new GatewayRecord
        {
            Id = "paired-ssh",
            Url = "ws://gateway.example:18789",
            SharedGatewayToken = hasSharedToken ? "shared-secret" : null,
            BootstrapToken = "bootstrap-secret",
            SshTunnel = new SshTunnelConfig("user", "gateway.example", 18789, 45678),
        };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(record);
        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        if (hasDeviceToken)
            identity.StoreDeviceTokenForRole("operator", "paired-device-secret");

        var operatorCredential = new CredentialResolver(DeviceIdentityFileReader.Instance)
            .ResolveOperator(record, identityDirectory);
        Assert.NotNull(operatorCredential);
        Assert.Equal(hasDeviceToken ? CredentialResolver.SourceDeviceToken
            : CredentialResolver.SourceBootstrapToken, operatorCredential.Source);

        var authorized = false;
        Assert.True(InteractiveGatewayCredentialResolver.TryResolveRecord(
            record, identityDirectory, DeviceIdentityFileReader.Instance,
            (pinned, candidate) =>
            {
                Assert.Same(record, pinned);
                Assert.Equal(hasSharedToken ? "shared-secret" : operatorCredential.Token, candidate.Token);
                authorized = true;
                return true;
            },
            out var credential, out var rejected));
        Assert.True(authorized);
        Assert.False(rejected);
        Assert.NotNull(credential);

        var tunnel = new SshTunnelSnapshot(true, "user", "gateway.example", 18789, 45678,
            0, 0, DateTime.UtcNow, null, TunnelStatus.Up);
        Assert.True(GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record, tunnel, out var endpoint, out var appendSharedToken, listenerOwned: true));
        Assert.Equal("ws://localhost:45678", endpoint);
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true, samePinnedRecord: true, appendSharedToken,
            credential.Source, credential.IsBootstrapToken, credential.Token, record.SharedGatewayToken);

        Assert.False(decision.PinMismatch);
        Assert.Equal(hasSharedToken, decision.AppendToken);
        Assert.Equal(hasSharedToken ? "shared-secret" : null, decision.Token);
        Assert.Equal(hasSharedToken ? CredentialResolver.SourceSharedGatewayToken
            : operatorCredential.Source, decision.CredentialSource);
    }

    [Fact]
    public void PinnedSshHttpResolver_AuthorizationRejectionDoesNotFallBackToDeviceToken()
    {
        using var temp = new TempDirectory();
        var identity = new DeviceIdentity(temp.Path);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "paired-device-secret");
        var record = new GatewayRecord
        {
            Id = "paired-ssh",
            Url = "ws://gateway.example:18789",
            SharedGatewayToken = "shared-secret",
            SshTunnel = new SshTunnelConfig("user", "gateway.example", 18789, 45678),
        };

        Assert.False(InteractiveGatewayCredentialResolver.TryResolveRecord(
            record, temp.Path, DeviceIdentityFileReader.Instance,
            (pinned, candidate) =>
            {
                Assert.Same(record, pinned);
                Assert.Equal(CredentialResolver.SourceSharedGatewayToken, candidate.Source);
                return false;
            },
            out var credential, out var rejected));

        Assert.True(rejected);
        Assert.Null(credential);
    }
}
