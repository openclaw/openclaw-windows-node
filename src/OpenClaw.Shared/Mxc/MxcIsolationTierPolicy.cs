namespace OpenClaw.Shared.Mxc;

internal static class MxcIsolationTierPolicy
{
    internal const string BaseContainer = "base-container";
    internal const string AppContainerBfs = "appcontainer-bfs";
    internal const string AppContainerDacl = "appcontainer-dacl";

    internal static bool IsDacl(string? isolationTier) =>
        string.Equals(isolationTier, AppContainerDacl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MXC 0.8 chooses BaseContainer per request. Its request compatibility
    /// check can fall through only for least-privilege mode, denied paths that
    /// the OS contract cannot enforce, proxy/directional-network policy, or
    /// denial capture. <see cref="MxcConfig"/> cannot express the latter three,
    /// and OpenClaw omits backend denied paths. Validate the remaining emitted
    /// fields here before relying on BaseContainer's non-cascading root grants.
    /// </summary>
    internal static bool CanUseNonCascadingVolumeRootGrants(
        MxcAvailability availability,
        MxcConfig config) =>
        string.Equals(
            availability.IsolationTier,
            BaseContainer,
            StringComparison.OrdinalIgnoreCase) &&
        config.ProcessContainer?.LeastPrivilege != true &&
        config.Filesystem?.DeniedPaths is not { Length: > 0 };

    internal static bool IsBlockingHostPreparationWarning(string warning) =>
        warning.Contains("wxc-host-prep", StringComparison.OrdinalIgnoreCase) &&
        (warning.Contains("prepare-system-drive", StringComparison.OrdinalIgnoreCase) ||
         warning.Contains("prepare-null-device", StringComparison.OrdinalIgnoreCase));
}
