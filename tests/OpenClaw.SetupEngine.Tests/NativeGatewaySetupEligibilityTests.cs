using OpenClaw.Shared.Mxc;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewaySetupEligibilityTests
{
    [Theory]
    [InlineData(true, NativeGatewayEligibility.Available)]
    [InlineData(false, NativeGatewayEligibility.CapabilityUnavailable)]
    [InlineData(null, NativeGatewayEligibility.CheckFailed)]
    public void Eligibility_UsesReportedSessionCapability(bool? capability, NativeGatewayEligibility expected)
    {
        // The legacy backend property and process tier must not override the actual session verdict.
        var availability = new MxcAvailability(true, capability != true, true, "wxc-exec.exe", [],
            isolationTier: "appcontainer-dacl", needsDaclAugmentation: true,
            isolationSessionCapability: capability);
        Assert.Equal(expected, NativeGatewaySetupEligibility.Evaluate(availability));
    }

    [Theory]
    [InlineData(true, true, false, NativeGatewayEligibility.CheckFailed)]
    [InlineData(false, false, false, NativeGatewayEligibility.CheckFailed)]
    [InlineData(false, false, true, NativeGatewayEligibility.UnsupportedPlatform)]
    [InlineData(true, false, true, NativeGatewayEligibility.CheckFailed)]
    public void ProbeFailuresAndUnsupportedSku_DoNotBecomeWindowsUpdateAdvice(
        bool errored, bool resolvable, bool suppressed, NativeGatewayEligibility expected)
    {
        var availability = new MxcAvailability(false, false, resolvable, null, [],
            probeErrored: errored, probeSuppressedBySkuGate: suppressed,
            isolationSessionCapability: true);
        Assert.Equal(expected, NativeGatewaySetupEligibility.Evaluate(availability));
    }

    [Theory]
    [InlineData(null, NativeGatewayEligibility.Available, GatewaySetupChoice.Native)]
    [InlineData(null, NativeGatewayEligibility.CapabilityUnavailable, null)]
    [InlineData(null, NativeGatewayEligibility.CheckFailed, null)]
    [InlineData(null, NativeGatewayEligibility.UnsupportedPlatform, null)]
    [InlineData(GatewaySetupChoice.Native, NativeGatewayEligibility.Available, GatewaySetupChoice.Native)]
    [InlineData(GatewaySetupChoice.Native, NativeGatewayEligibility.CapabilityUnavailable, null)]
    [InlineData(GatewaySetupChoice.Native, NativeGatewayEligibility.CheckFailed, null)]
    [InlineData(GatewaySetupChoice.Existing, NativeGatewayEligibility.Available, GatewaySetupChoice.Existing)]
    [InlineData(GatewaySetupChoice.Existing, NativeGatewayEligibility.CheckFailed, GatewaySetupChoice.Existing)]
    [InlineData(GatewaySetupChoice.Wsl, NativeGatewayEligibility.Available, GatewaySetupChoice.Wsl)]
    [InlineData(GatewaySetupChoice.Wsl, NativeGatewayEligibility.CapabilityUnavailable, GatewaySetupChoice.Wsl)]
    [InlineData(GatewaySetupChoice.Wsl, NativeGatewayEligibility.CheckFailed, GatewaySetupChoice.Wsl)]
    [InlineData(GatewaySetupChoice.Wsl, NativeGatewayEligibility.UnsupportedPlatform, GatewaySetupChoice.Wsl)]
    public void Selection_PreservesExplicitWslAndExistingChoicesRegardlessOfNativeSupport(
        GatewaySetupChoice? selected, NativeGatewayEligibility eligibility, GatewaySetupChoice? expected) =>
        Assert.Equal(expected, NativeGatewaySetupEligibility.ResolveSelection(selected, eligibility));
}
