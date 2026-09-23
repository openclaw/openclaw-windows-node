namespace OpenClaw.Connection.NativeGateway;

/// <summary>A current-user installed package and its package-qualified execution aliases.</summary>
public sealed record NativeGatewayPackage(
    string PackageFamilyName,
    string Version,
    string OpenClawAliasPath,
    string ClawCtlAliasPath);

/// <summary>No matching trusted package is registered. Other validation failures are not install requests.</summary>
public sealed class NativeGatewayPackageNotInstalledException : InvalidOperationException
{
    public NativeGatewayPackageNotInstalledException()
        : base("A supported OpenClaw Gateway MSIX is not registered for this Windows user. " +
            "Complete installation from Microsoft Store, then retry native setup.")
    {
    }
}

/// <summary>Resolves only an already installed, trusted OpenClaw Gateway package. Never installs it.</summary>
public interface INativeGatewayPackageResolver
{
    /// <exception cref="NativeGatewayPackageNotInstalledException">No matching package is registered.</exception>
    Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>Resolves a saved profile's exact family without selecting a different installed Gateway.</summary>
    async Task<NativeGatewayPackage> ResolveAsync(string expectedFamily, CancellationToken cancellationToken)
    {
        NativeGatewayPaths.ValidateFamilyName(expectedFamily);
        var package = await ResolveAsync(cancellationToken);
        NativeGatewayPaths.ValidatePackage(package, expectedFamily);
        return package;
    }

    /// <summary>
    /// Maps Companion's logical data path to the physical path visible to another package
    /// identity. Used only at the launch boundary; registry/state ownership remains logical.
    /// </summary>
    string ResolveDataPath(string path) => path;
}

/// <summary>
/// Owns a non-isolated native gateway's process lifetime, not its installation or persisted state.
/// Ensure is an explicit start/retry request; Stop and Dispose never schedule subsequent restarts.
/// </summary>
public interface INativeGatewayRuntime : IAsyncDisposable
{
    Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<GatewayEndpointProvenance> InspectAsync(GatewayRecord record, CancellationToken cancellationToken);
    /// <summary>Fresh ownership inspection without starting or waiting for a lifecycle operation. Busy runtimes deny handoff.</summary>
    GatewayEndpointProvenance Inspect(GatewayRecord record);
}
