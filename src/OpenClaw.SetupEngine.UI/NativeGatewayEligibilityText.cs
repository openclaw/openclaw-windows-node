namespace OpenClaw.SetupEngine.UI;

internal static class NativeGatewayEligibilityText
{
    internal static string Get(NativeGatewayEligibility eligibility) => eligibility switch
    {
        NativeGatewayEligibility.Available => SetupLocalization.GetString("Onboarding_Native_SupportAvailable"),
        NativeGatewayEligibility.CapabilityUnavailable => SetupLocalization.Format(
            "Onboarding_Native_SupportUnavailable", NativeGatewaySetupEligibility.InsiderBuild),
        NativeGatewayEligibility.UnsupportedPlatform => SetupLocalization.GetString("Onboarding_Native_UnsupportedPlatform"),
        NativeGatewayEligibility.CheckFailed => SetupLocalization.GetString("Onboarding_Native_SupportCheckFailed"),
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility)),
    };
}
