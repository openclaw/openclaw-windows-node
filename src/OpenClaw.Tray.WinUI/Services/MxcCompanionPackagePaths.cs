using OpenClaw.Shared.Mxc;

namespace OpenClawTray.Services;

/// <summary>Projects registered Companion identities into one command's private-state denies.</summary>
internal static class MxcCompanionPackagePaths
{
    internal static MxcRequestContext Capture(
        MxcRequestContext context,
        Func<IEnumerable<MxcCompanionPackage>> registeredPackages,
        Func<string> unredirectedLocalAppData)
    {
        try
        {
            var paths = context.ProtectedPaths.ToList();
            var companions = registeredPackages()
                .Where(package => !package.IsFramework && !package.IsResourcePackage)
                .Select(package => (Package: package, Leaf: DataLeaf(package)))
                .Where(candidate => candidate.Leaf is not null)
                .ToArray();
            if (companions.Length > 0)
            {
                var local = unredirectedLocalAppData();
                if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                    throw new InvalidOperationException("Windows did not supply an absolute local app data path.");
                local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(local));
                if (local == Path.GetPathRoot(local))
                    throw new InvalidOperationException("Local app data cannot be a volume root.");

                foreach (var (package, leaf) in companions)
                {
                    RequireFamilyName(package);
                    var cache = Path.Combine(local, "Packages", package.FamilyName, "LocalCache");
                    paths.Add(Path.Combine(cache, "Roaming", leaf!));
                    paths.Add(Path.Combine(cache, "Local", leaf!));
                }
            }

            return context with
            {
                UserFolders = Array.AsReadOnly(context.UserFolders.ToArray()),
                ProtectedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            };
        }
        catch (Exception ex)
        {
            // Never turn partial inventory or known-folder failure into an unprotected command.
            throw new NotSupportedException(
                "Companion package-state protection could not be resolved. " +
                "Check this Windows user's Companion registrations and local app data access, then retry.", ex);
        }
    }

    private static string? DataLeaf(MxcCompanionPackage package)
    {
        if (string.Equals(package.Name, "OpenClawFoundation.OpenClaw", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.Publisher, "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC", StringComparison.OrdinalIgnoreCase))
            return "OpenClawTray";
        if (string.Equals(package.Name, "OpenClawFoundation.OpenClaw.Dev", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.Publisher, "CN=OpenClaw Local Development", StringComparison.OrdinalIgnoreCase))
            return "OpenClawTray-Dev";
        return null;
    }

    private static void RequireFamilyName(MxcCompanionPackage package)
    {
        // Windows supplies the publisher ID. Validate the PFN as one component, never guess its hash.
        var prefix = package.Name + "_";
        var family = package.FamilyName;
        if (family is null || !family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            family.Length != prefix.Length + 13 ||
            !family[prefix.Length..].All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("A registered Companion package family name is malformed.");
    }
}

internal sealed record MxcCompanionPackage(
    string Name, string Publisher, string FamilyName, bool IsFramework = false, bool IsResourcePackage = false);
