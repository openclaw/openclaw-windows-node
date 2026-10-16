using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Exact registration identities accepted before resolving package-qualified aliases, plus an
/// explicit developer opt-in (<see cref="DevPatchEnvironmentVariable"/>) for one side-by-side
/// patched loose registration under the Store publisher.
/// </summary>
public static class NativeGatewayPackageIdentity
{
    public const string StoreName = "OpenClawFoundation.OpenClawGateway";
    public const string StorePublisher = "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC";

    // Retain already installed development packages and their saved profiles, not local acquisition.
    public const string DevelopmentName = "OpenClaw.Gateway";
    public const string DevelopmentPublisher =
        "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US";

    public const string DevPatchEnvironmentVariable = "OPENCLAW_NATIVE_GATEWAY_DEV_PATCH";

    // Mirrors openclaw-windows-packaging Deploy-LocalPackage.ps1 -Patch: lowercase, 1 to 15 chars.
    private const string DevPatchPattern = @"\A[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?\z";

    public static bool IsTrusted(string name, string publisher) =>
        (name == StoreName && publisher == StorePublisher) ||
        (name == DevelopmentName && publisher == DevelopmentPublisher);

    /// <summary>Null when unset/blank; otherwise the lowercased patch. Mirrors Deploy-LocalPackage.ps1 -Patch rules.</summary>
    /// <exception cref="InvalidOperationException">The value is not a valid patch name.</exception>
    public static string? ParseDevPatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var patch = value.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(patch, DevPatchPattern, RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                $"{DevPatchEnvironmentVariable} must be 1 to 15 letters, digits, or hyphens, starting and ending with a letter or digit.");
        }
        return patch;
    }

    public static string GetDevPatchPackageName(string patch) => $"{StoreName}-{patch}";

    /// <summary>True when packageName is StoreName + "-" + a valid patch; patch is the suffix.</summary>
    public static bool TryGetDevPatch(string packageName, [NotNullWhen(true)] out string? patch)
    {
        const string prefix = StoreName + "-";
        if (packageName.StartsWith(prefix, StringComparison.Ordinal))
        {
            var suffix = packageName[prefix.Length..];
            if (Regex.IsMatch(suffix, DevPatchPattern, RegexOptions.CultureInvariant))
            {
                patch = suffix;
                return true;
            }
        }
        patch = null;
        return false;
    }

    /// <summary>New-setup selection. Without an opt-in: IsTrusted. With one: only that patch under the Store publisher.</summary>
    public static bool IsSelectable(string name, string publisher, string? devPatch) =>
        devPatch is null
            ? IsTrusted(name, publisher)
            : name == GetDevPatchPackageName(devPatch) && publisher == StorePublisher;

    /// <summary>Saved-profile resolution. Trusted identities always; the opted-in patch additionally.</summary>
    public static bool IsResolvable(string name, string publisher, string? devPatch) =>
        IsTrusted(name, publisher) ||
        (devPatch is not null && name == GetDevPatchPackageName(devPatch) && publisher == StorePublisher);

    /// <summary>
    /// Resolver filter for one registered package. New setup (<paramref name="expectedFamily"/> null) uses
    /// <see cref="IsSelectable"/>; a saved profile uses <see cref="IsResolvable"/> and its exact family.
    /// </summary>
    public static bool IsCandidate(
        string name, string publisher, string familyName, string? expectedFamily, string? devPatch) =>
        expectedFamily is null
            ? IsSelectable(name, publisher, devPatch)
            : IsResolvable(name, publisher, devPatch) && familyName == expectedFamily;

    /// <summary>"openclaw"/"clawctl" plus "-&lt;patch&gt;" for patched names; ".exe" appended.</summary>
    /// <exception cref="ArgumentException">command is not "openclaw" or "clawctl".</exception>
    public static string GetAliasFileName(string packageName, string command)
    {
        if (command is not ("openclaw" or "clawctl"))
            throw new ArgumentException("The native gateway alias command must be openclaw or clawctl.", nameof(command));
        return TryGetDevPatch(packageName, out var patch) ? $"{command}-{patch}.exe" : $"{command}.exe";
    }
}
