using System.Diagnostics.CodeAnalysis;
using OpenClaw.Connection.NativeGateway;
using Windows.Management.Deployment;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Uses current-user package registration, never an npm/PATH alias.
/// <c>OPENCLAW_NATIVE_GATEWAY_DEV_PATCH</c> selects one patched loose registration instead of the Store package.
/// </summary>
public sealed class NativeGatewayPackageResolver : INativeGatewayPackageResolver
{
    // Raw value is parsed per resolve so an invalid setting surfaces as a setup error, not a startup crash.
    private readonly string? _devPatchSetting;

    public NativeGatewayPackageResolver()
        : this(Environment.GetEnvironmentVariable(NativeGatewayPackageIdentity.DevPatchEnvironmentVariable))
    {
    }

    internal NativeGatewayPackageResolver(string? devPatchSetting) => _devPatchSetting = devPatchSetting;

    public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveCoreAsync(null, cancellationToken);

    public Task<NativeGatewayPackage> ResolveAsync(string expectedFamily, CancellationToken cancellationToken) =>
        ResolveCoreAsync(expectedFamily, cancellationToken);

    private Task<NativeGatewayPackage> ResolveCoreAsync(
        string? expectedFamily, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? devPatch = NativeGatewayPackageIdentity.ParseDevPatch(_devPatchSetting);
            RequireDevPatchForProfile(expectedFamily, devPatch);

            var packages = new PackageManager().FindPackagesForUser(string.Empty)
                .Where(package => !package.IsFramework && !package.IsResourcePackage &&
                    NativeGatewayPackageIdentity.IsCandidate(
                        package.Id.Name, package.Id.Publisher, package.Id.FamilyName, expectedFamily, devPatch))
                .ToArray();
            if (packages.Length == 0)
            {
                // Not NativeGatewayPackageNotInstalledException: an opted-in dev patch must never fall back to WinGet,
                // and the Store cannot install a patched identity a saved profile is bound to.
                if (devPatch is not null && expectedFamily is null)
                    throw new InvalidOperationException(
                        $"The development Gateway package {NativeGatewayPackageIdentity.GetDevPatchPackageName(devPatch)} " +
                        "is not registered for this Windows user. " +
                        $"Run scripts\\Build-NativeGatewayFromSource.ps1 -Patch {devPatch}, or clear " +
                        $"{NativeGatewayPackageIdentity.DevPatchEnvironmentVariable} to use the Microsoft Store Gateway.");
                // Clearing the variable cannot help here: the saved profile stays pinned to the patched family.
                if (devPatch is not null && TryGetFamilyDevPatch(expectedFamily, out _))
                    throw new InvalidOperationException(
                        $"This Gateway profile uses the development Gateway package {NativeGatewayPackageIdentity.GetDevPatchPackageName(devPatch)}, " +
                        "which is not registered for this Windows user. " +
                        $"Run scripts\\Build-NativeGatewayFromSource.ps1 -Patch {devPatch} to register it again, " +
                        "or remove this gateway in Connection settings and set up a new one.");
                throw new NativeGatewayPackageNotInstalledException();
            }
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
            var openClaw = Path.Combine(aliases, NativeGatewayPackageIdentity.GetAliasFileName(package.Id.Name, "openclaw"));
            var clawCtl = Path.Combine(aliases, NativeGatewayPackageIdentity.GetAliasFileName(package.Id.Name, "clawctl"));
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

    /// <summary>A saved profile bound to a patched package resolves only while the opt-in names that patch.</summary>
    private static void RequireDevPatchForProfile(string? expectedFamily, string? devPatch)
    {
        if (TryGetFamilyDevPatch(expectedFamily, out var required) && required != devPatch)
            throw new InvalidOperationException(
                "This Gateway profile uses the development Gateway package " +
                $"{NativeGatewayPackageIdentity.GetDevPatchPackageName(required)}. " +
                $"Set {NativeGatewayPackageIdentity.DevPatchEnvironmentVariable}={required} and restart Companion to use it.");
    }

    private static bool TryGetFamilyDevPatch(string? family, [NotNullWhen(true)] out string? patch)
    {
        int separator = family?.LastIndexOf('_') ?? -1;
        if (separator < 0)
        {
            patch = null;
            return false;
        }
        return NativeGatewayPackageIdentity.TryGetDevPatch(family![..separator], out patch);
    }

    public string ResolveDataPath(string path) => LogFileLauncher.ResolveRealPath(path);
}
