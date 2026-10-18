namespace OpenClaw.Shared.Mxc;

internal static class MxcIsolationTierPolicy
{
    internal const string BaseContainer = "base-container";

    /// <summary>
    /// MXC 0.9 removes SBOX. Require the admitted PSEC/BaseContainer contract
    /// before adding non-cascading root grants. Directional policy must retain
    /// denied ingress and host loopback; network capability derivation is native.
    /// </summary>
    internal static bool IsSystemRunConfigBaseContainerCompatible(MxcConfig config) =>
        string.Equals(
            config.Version,
            MxcPolicyBuilder.SupportedPolicyVersion,
            StringComparison.Ordinal) &&
        config.Containment is null &&
        config.ProcessContainer?.LeastPrivilege == false &&
        config.Filesystem?.DeniedPaths is null &&
        config.Network?.Egress.Default is "allow" or "deny" &&
        config.Network.Ingress.Default == "deny" &&
        config.Network.Ingress.HostLoopback == "deny";
}
