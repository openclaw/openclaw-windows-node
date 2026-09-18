using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.SetupEngine;

public enum NativeGatewayEligibility { Available, CapabilityUnavailable, CheckFailed, UnsupportedPlatform }
public enum GatewaySetupChoice { Native, Existing, Wsl }

/// <summary>Onboarding admission only. Capability does not imply that the Gateway runs in an MXC session.</summary>
public static class NativeGatewaySetupEligibility
{
    // Session baseline documented by the pinned @microsoft/mxc-sdk README.
    // The live probe, not a build-number comparison, remains authoritative.
    public const string InsiderBuild = "26340.9212";

    public static NativeGatewayEligibility Probe(IOpenClawLogger? logger = null) =>
        Evaluate(MxcAvailability.Probe(logger));

    public static NativeGatewayEligibility Evaluate(MxcAvailability availability)
    {
        if (availability.ProbeErrored)
            return NativeGatewayEligibility.CheckFailed;
        if (availability.ProbeSuppressedBySkuGate)
            return NativeGatewayEligibility.UnsupportedPlatform;
        if (!availability.IsWxcExecResolvable)
            return NativeGatewayEligibility.CheckFailed;
        return availability.IsolationSessionCapability switch
        {
            true => NativeGatewayEligibility.Available,
            false => NativeGatewayEligibility.CapabilityUnavailable,
            null => NativeGatewayEligibility.CheckFailed,
        };
    }

    public static GatewaySetupChoice? ResolveSelection(
        GatewaySetupChoice? selected, NativeGatewayEligibility eligibility) =>
        selected == GatewaySetupChoice.Existing ||
        (selected == GatewaySetupChoice.Wsl && ShowAlternatives(eligibility))
            ? selected
            : eligibility == NativeGatewayEligibility.Available ? GatewaySetupChoice.Native : null;

    public static bool ShowAlternatives(NativeGatewayEligibility? eligibility) =>
        eligibility.HasValue && eligibility != NativeGatewayEligibility.Available;
}
