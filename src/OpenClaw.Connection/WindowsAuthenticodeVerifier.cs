using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Security.Extensions;

namespace OpenClaw.Connection;

internal readonly record struct AuthenticodeTrustResult(
    bool IsTrusted,
    string? Detail,
    bool IsWarning = false)
{
    public static AuthenticodeTrustResult Trusted() => new(true, null);

    /// <summary>Usable trust that could not be fully proven; callers must corroborate with the
    /// managed distro ownership probe before treating the endpoint as the expected gateway.</summary>
    public static AuthenticodeTrustResult Warned(string detail) => new(true, detail, true);

    public static AuthenticodeTrustResult Rejected(string detail) => new(false, detail);
}

/// <summary>
/// Minimal projection of a Windows AppX/MSIX package as reported by
/// <c>Get-AppxPackage</c>, used to corroborate genuine Microsoft WSL binaries that
/// ship without a per-file Authenticode/catalog signature (MSIX packages are
/// trusted at the package level, not per-file).
/// </summary>
internal readonly record struct AppxPackageInfo(
    string? PackageFamilyName,
    string? Publisher,
    string? SignatureKind);

/// <summary>Filesystem protection verdict for a relay deployment path.</summary>
internal enum WslRelayPathProtection
{
    Protected,
    Unprotected,
    Unverifiable,
}

internal readonly record struct WslRelayPathInspection(WslRelayPathProtection Protection, string? Detail)
{
    public static WslRelayPathInspection Protected() =>
        new(WslRelayPathProtection.Protected, null);

    public static WslRelayPathInspection Unprotected(string detail) =>
        new(WslRelayPathProtection.Unprotected, detail);

    public static WslRelayPathInspection Unverifiable(string detail) =>
        new(WslRelayPathProtection.Unverifiable, detail);
}

internal static class WindowsAuthenticodeVerifier
{
    // Modern WSL ships as an MSIX package. The trailing suffix is a hash derived
    // from the publisher's signing certificate/public key, so an impostor package
    // re-signed with a different certificate would get a different family name.
    private const string WslPackageFamilyName =
        "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe";
    private const string WslPackageNamePrefix = "MicrosoftCorporationII.WindowsSubsystemForLinux_";
    private const string WslPackagePublisherSuffix = "__8wekyb3d8bbwe";
    private const string WslRelayFileName = "wslrelay.exe";
    private const string TrustedInstallerSidString =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string PackageLookupCacheSecondsEnvironmentVariable =
        "OPENCLAW_WSL_PACKAGE_LOOKUP_CACHE_SECONDS";
    private const int DefaultPackageLookupCacheSeconds = 300;

    private const int PackageLookupTimeoutMs = 8_000;

    private static readonly object PackageLookupGate = new();
    private static AppxPackageInfo? PackageLookupCache;
    private static DateTime PackageLookupCacheUtc;
    private static readonly TimeSpan PackageLookupCacheLifetime = ReadPackageLookupCacheLifetime();

    public static AuthenticodeTrustResult VerifyMicrosoftSignedFile(string path) =>
        VerifyMicrosoftSignedFile(
            path,
            VerifyAuthenticodeSignature,
            LookupWslAppxPackage,
            InspectWslRelayPath);

    /// <summary>
    /// Test seam for the primary signature, WSL package, and protected-path evidence.
    /// Trust requires either a valid embedded Microsoft Authenticode signature or a
    /// package-level proof that the relay was deployed by the genuine WSL package;
    /// canonical paths that prove neither degrade to a warning instead of a rejection.
    /// </summary>
    internal static AuthenticodeTrustResult VerifyMicrosoftSignedFile(
        string path,
        Func<string, AuthenticodeTrustResult> verifyAuthenticode,
        Func<AppxPackageInfo?> lookupWslPackage,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        AuthenticodeTrustResult primary;
        try
        {
            primary = verifyAuthenticode(path);
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }

        if (primary.IsTrusted)
            return primary;

        // MSIX packages (modern WSL) don't carry a per-file Authenticode/catalog
        // signature, so a failed classic check isn't conclusive for wslrelay.exe.
        // Only consult the package-level fallback for the WSL binaries this
        // codebase actually cares about; never widen trust for arbitrary files.
        if (!string.Equals(Path.GetFileName(path), WslRelayFileName, StringComparison.OrdinalIgnoreCase))
            return primary;

        return VerifyWslRelayPackageFallback(path, primary, lookupWslPackage, inspectRelayPath);
    }

    private static AuthenticodeTrustResult VerifyAuthenticodeSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var signature = FileSignatureInfo.GetFromFileStream(stream);
            using var signingCertificate = signature.SigningCertificate;
            using var timestampCertificate = signature.TimestampCertificate;

            if (signature.State != SignatureState.SignedAndTrusted)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"WSL relay Authenticode verification failed ({signature.State}).");
            }
            if (signingCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer could not be read.");
            }

            return HasMicrosoftPublisherIdentity(signingCertificate.Subject)
                ? AuthenticodeTrustResult.Trusted()
                : AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer is not Microsoft Corporation.");
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }
    }

    private static AuthenticodeTrustResult VerifyWslRelayPackageFallback(
        string path,
        AuthenticodeTrustResult primaryFailure,
        Func<AppxPackageInfo?> lookupWslPackage,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return primaryFailure;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var canonicalProgramFiles = Path.Combine(programFiles, "WSL", WslRelayFileName);
        var canonicalSystem = Path.Combine(windowsRoot, "System32", WslRelayFileName);
        var windowsAppsRoot = Path.Combine(programFiles, "WindowsApps");
        var isWithinWindowsApps = IsPathWithin(fullPath, windowsAppsRoot);
        var isCanonicalExternal =
            string.Equals(fullPath, canonicalProgramFiles, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, canonicalSystem, StringComparison.OrdinalIgnoreCase);
        if (!isCanonicalExternal && !isWithinWindowsApps)
            return primaryFailure;

        AppxPackageInfo? package = null;
        try
        {
            package = lookupWslPackage();
        }
        catch
        {
            package = null;
        }

        if (isWithinWindowsApps && !IsWslPackageRootRelay(fullPath, windowsAppsRoot))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay package verification failed: the relay is not owned by the WSL package family.");
        }

        if (package is not { } info)
        {
            // A canonical, admin-protected deployment without a verifiable package
            // registration. Degrade to a warning: the managed distro ownership probe
            // is the remaining strong evidence, and callers treat warnings as
            // verify-with-distro-confirmation instead of failing closed.
            return AuthenticodeTrustResult.Warned(
                $"no installed WSL package could corroborate the relay at '{fullPath}'");
        }

        if (!string.Equals(info.PackageFamilyName, WslPackageFamilyName, StringComparison.Ordinal) ||
            info.Publisher is null ||
            !HasMicrosoftPublisherIdentity(info.Publisher))
        {
            return AuthenticodeTrustResult.Warned(
                "the installed WSL package identity could not be corroborated");
        }

        if (string.Equals(info.SignatureKind, "None", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticodeTrustResult.Warned(
                "the installed WSL package reports an unsigned signature kind");
        }

        var trustedRoot = isWithinWindowsApps
            ? windowsAppsRoot
            : string.Equals(fullPath, canonicalSystem, StringComparison.OrdinalIgnoreCase)
                ? windowsRoot
                : programFiles;
        var inspection = InspectProtectedRelayPath(fullPath, trustedRoot, inspectRelayPath);
        return inspection.Protection switch
        {
            WslRelayPathProtection.Protected => AuthenticodeTrustResult.Trusted(),
            WslRelayPathProtection.Unprotected => AuthenticodeTrustResult.Rejected(
                inspection.Detail ?? "WSL relay package verification failed: the relay path permits untrusted modification."),
            _ => AuthenticodeTrustResult.Warned(
                inspection.Detail ?? "the relay path protection could not be inspected"),
        };
    }

    private static WslRelayPathInspection InspectProtectedRelayPath(
        string fullPath,
        string trustedRoot,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        try
        {
            return inspectRelayPath(fullPath, trustedRoot);
        }
        catch
        {
            return WslRelayPathInspection.Unverifiable(
                "the relay path could not be inspected");
        }
    }

    internal static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    internal static bool HasMicrosoftPublisherIdentity(string subject) =>
        subject.Split(',')
            .Select(part => part.Trim())
            .Any(part =>
                string.Equals(
                    part,
                    "O=Microsoft Corporation",
                    StringComparison.OrdinalIgnoreCase));


    /// <summary>
    /// True only for <c>{windowsAppsRoot}\{WSL package directory}\wslrelay.exe</c>. The package
    /// root is the first directory below WindowsApps; a WSL-named folder nested inside another
    /// package's directory is not a WSL deployment.
    /// </summary>
    private static bool IsWslPackageRootRelay(string fullPath, string windowsAppsRoot)
    {
        var segments = Path.GetRelativePath(windowsAppsRoot, fullPath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return segments.Length == 2 &&
            segments[0].StartsWith(WslPackageNamePrefix, StringComparison.OrdinalIgnoreCase) &&
            segments[0].EndsWith(WslPackagePublisherSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ReadPackageLookupCacheLifetime()
    {
        var raw = Environment.GetEnvironmentVariable(PackageLookupCacheSecondsEnvironmentVariable);
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultPackageLookupCacheSeconds);
    }

    /// <summary>
    /// Setup polls provenance about once a second while waiting for the gateway, so
    /// the PowerShell package lookup is cached briefly instead of spawned per poll.
    /// </summary>
    private static AppxPackageInfo? LookupWslAppxPackage()
    {
        lock (PackageLookupGate)
        {
            if (PackageLookupCache is not null &&
                DateTime.UtcNow - PackageLookupCacheUtc < PackageLookupCacheLifetime)
            {
                return PackageLookupCache;
            }
        }

        var info = LookupWslAppxPackageViaPowerShell();
        lock (PackageLookupGate)
        {
            PackageLookupCache = info;
            PackageLookupCacheUtc = DateTime.UtcNow;
        }
        return info;
    }

    private static AppxPackageInfo? LookupWslAppxPackageViaPowerShell()
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "Get-AppxPackage -Name 'MicrosoftCorporationII.WindowsSubsystemForLinux' | " +
                "Sort-Object Version -Descending | " +
                "Select-Object -First 1 PackageFamilyName, Publisher, " +
                "@{Name='SignatureKind'; Expression={$_.SignatureKind.ToString()}} | " +
                "ConvertTo-Json -Compress");

            process = Process.Start(psi);
            if (process is null)
                return null;

            var timeout = TimeSpan.FromMilliseconds(PackageLookupTimeoutMs);
            using var cancellation = new CancellationTokenSource(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellation.Token);

            if (!process.WaitForExit(PackageLookupTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output;
            try
            {
                output = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ParseAppxPackageJson(output);
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolvePowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static AppxPackageInfo? ParseAppxPackageJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var familyName = GetStringProperty(root, "PackageFamilyName");
            if (familyName is null)
                return null;

            return new AppxPackageInfo(
                familyName,
                GetStringProperty(root, "Publisher"),
                GetStringProperty(root, "SignatureKind"));
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static WslRelayPathInspection InspectWslRelayPath(string path, string trustedRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WslRelayPathInspection.Unverifiable(
                "WSL package verification is only available on Windows.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(trustedRoot);
            if (!File.Exists(fullPath) || !IsPathWithin(fullPath, fullRoot))
            {
                return WslRelayPathInspection.Unverifiable(
                    "the relay path is unavailable or outside its trusted root");
            }

            for (var current = fullPath; ; current = Path.GetDirectoryName(current)!)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return WslRelayPathInspection.Unprotected(
                        "the relay path contains a reparse point");
                }

                FileSystemSecurity security =
                    string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase)
                        ? new FileInfo(current).GetAccessControl(
                            AccessControlSections.Owner | AccessControlSections.Access)
                        : new DirectoryInfo(current).GetAccessControl(
                            AccessControlSections.Owner | AccessControlSections.Access);
                if (!HasProtectedOwnershipAndWriteAcl(
                        security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
                        security.GetAccessRules(
                                includeExplicit: true,
                                includeInherited: true,
                                targetType: typeof(SecurityIdentifier))
                            .OfType<FileSystemAccessRule>(),
                        HasDiscretionaryAcl(security)))
                {
                    return WslRelayPathInspection.Unprotected(
                        "the relay path permits untrusted modification");
                }

                if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(current)))
                {
                    return WslRelayPathInspection.Unverifiable(
                        "the relay path escaped its trusted root");
                }
            }

            return WslRelayPathInspection.Protected();
        }
        catch
        {
            return WslRelayPathInspection.Unverifiable(
                "the relay path could not be inspected");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasDiscretionaryAcl(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        return (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 &&
            descriptor.DiscretionaryAcl is not null;
    }

    [SupportedOSPlatform("windows")]
    internal static bool HasProtectedOwnershipAndWriteAcl(
        SecurityIdentifier? owner,
        IEnumerable<FileSystemAccessRule> rules,
        bool hasDiscretionaryAcl)
    {
        if (!hasDiscretionaryAcl)
            return false;

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var trustedInstaller = new SecurityIdentifier(TrustedInstallerSidString);
        if (owner is null ||
            (!owner.Equals(system) &&
             !owner.Equals(administrators) &&
             !owner.Equals(trustedInstaller)))
        {
            return false;
        }

        const FileSystemRights writeRights =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (var rule in rules)
        {
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & writeRights) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid ||
                (!sid.Equals(system) &&
                 !sid.Equals(administrators) &&
                 !sid.Equals(trustedInstaller)))
            {
                return false;
            }
        }

        return true;
    }
}
