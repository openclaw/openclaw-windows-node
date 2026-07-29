using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class BrowserProxyActivationTests
{
    [Fact]
    public void Registration_RequiresToggleClientAndSharedToken()
    {
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.ToggleDisabled,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: false,
                sharedGatewayToken: "token",
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.NoGatewayClient,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "token",
                hasGatewayClient: false));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.MissingSharedGatewayToken,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: null,
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.MissingSharedGatewayToken,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "  ",
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.None,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "token",
                hasGatewayClient: true));
        Assert.True(BrowserProxyActivation.ShouldRegister(true, "token", true));
        Assert.False(BrowserProxyActivation.ShouldRegister(true, null, true));
    }

    [Fact]
    public void MissingSharedTokenWarning_RequiresAttachedClient()
    {
        Assert.True(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: true));
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: false));
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true,
            hasGatewayClient: true));
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: true));
    }

    [Fact]
    public void CapabilityPill_UsesNeedsSharedTokenOnlyWhenClientAttached()
    {
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.NeedsSharedToken,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.PendingApproval,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                hasGatewayClient: false));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.PendingApproval,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: true,
                hasSharedGatewayToken: true,
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.Active,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: true,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                hasGatewayClient: false));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.Off,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: false,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                hasGatewayClient: false));
    }
}
