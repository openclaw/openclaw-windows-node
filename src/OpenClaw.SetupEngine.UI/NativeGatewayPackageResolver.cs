using OpenClaw.Connection.NativeGateway;
using Windows.Management.Deployment;

namespace OpenClaw.SetupEngine.UI;

/// <summary>Uses current-user package registration, never an npm/PATH alias.</summary>
public sealed class NativeGatewayPackageResolver : INativeGatewayPackageResolver
{
    public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveCoreAsync(null, cancellationToken);

    public Task<NativeGatewayPackage> ResolveAsync(string expectedFamily, CancellationToken cancellationToken) =>
        ResolveCoreAsync(expectedFamily, cancellationToken);

    private static Task<NativeGatewayPackage> ResolveCoreAsync(
        string? expectedFamily, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packages = new PackageManager().FindPackagesForUser(string.Empty)
                .Where(package => NativeGatewayPackageIdentity.IsTrusted(package.Id.Name, package.Id.Publisher) &&
                    (expectedFamily is null || package.Id.FamilyName == expectedFamily) &&
                    !package.IsFramework && !package.IsResourcePackage)
                .ToArray();
            if (packages.Length == 0)
                throw new NativeGatewayPackageNotInstalledException();
            if (packages.Length > 1)
                throw new InvalidOperationException(
                    "Multiple supported OpenClaw Gateway MSIX packages are registered for this Windows user. " +
                    "Resolve the duplicate registrations, then retry native setup.");

            var package = packages[0];
            if (!package.Status.VerifyIsOK())
                throw new InvalidOperationException("The installed Gateway package needs repair. Repair it separately, then retry native setup.");

            var family = package.Id.FamilyName;
            var aliases = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", family);
            var openClaw = Path.Combine(aliases, "openclaw.exe");
            var clawCtl = Path.Combine(aliases, "clawctl.exe");
            if (!File.Exists(openClaw) || !File.Exists(clawCtl))
                throw new InvalidOperationException(
                    "The Gateway package's app execution aliases are unavailable. " +
                    "Enable its openclaw and clawctl aliases in Windows Settings, then retry native setup.");

            var version = package.Id.Version;
            return new NativeGatewayPackage(
                family,
                $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                openClaw,
                clawCtl);
        }, cancellationToken);

    public string ResolveDataPath(string path) => LogFileLauncher.ResolveRealPath(path);
}
