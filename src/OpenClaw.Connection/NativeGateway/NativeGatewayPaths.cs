using System.Text.RegularExpressions;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>One durable state/config location for native setup and runtime. Stop never removes it.</summary>
public static class NativeGatewayPaths
{
    public static string GetStateDirectory(GatewayRegistry registry, string gatewayId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ValidateGatewayId(gatewayId);
        return Path.Combine(registry.GetIdentityDirectory(gatewayId), "native-gateway");
    }

    public static string GetConfigPath(GatewayRegistry registry, string gatewayId) =>
        Path.Combine(GetStateDirectory(registry, gatewayId), "openclaw.json");

    public static IReadOnlyDictionary<string, string> GetEnvironment(GatewayRegistry registry, string gatewayId) =>
        GetEnvironment(registry, gatewayId, static path => path);

    /// <summary>
    /// Builds a launch-only environment using physical cross-package paths. The canonical
    /// registry/state paths returned by GetStateDirectory and GetConfigPath are not changed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetEnvironment(
        GatewayRegistry registry, string gatewayId, Func<string, string> resolveDataPath)
    {
        ArgumentNullException.ThrowIfNull(resolveDataPath);
        var stateDirectory = resolveDataPath(GetStateDirectory(registry, gatewayId));
        var configPath = resolveDataPath(GetConfigPath(registry, gatewayId));
        if (string.IsNullOrWhiteSpace(stateDirectory) || string.IsNullOrWhiteSpace(configPath) ||
            !Path.IsPathFullyQualified(stateDirectory) || !Path.IsPathFullyQualified(configPath))
        {
            throw new InvalidOperationException("Native gateway launch data paths must be absolute.");
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCLAW_STATE_DIR"] = stateDirectory,
            ["OPENCLAW_CONFIG_PATH"] = configPath,
            ["OPENCLAW_SUPERVISOR_MODE"] = "external",
            ["OPENCLAW_SERVICE_REPAIR_POLICY"] = "external",
            ["OPENCLAW_NO_AUTO_UPDATE"] = "1",
        };
    }

    internal static void ValidateGatewayId(string gatewayId)
    {
        // Existing registry IDs include both GUIDs and gw- identifiers. No filesystem aliases,
        // separators, alternate streams, trailing dots/spaces, or DOS device names are allowed.
        if (string.IsNullOrEmpty(gatewayId) ||
            !Regex.IsMatch(gatewayId, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z",
                RegexOptions.CultureInvariant) ||
            Regex.IsMatch(gatewayId, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("The native gateway ID must be a path-safe identifier.", nameof(gatewayId));
        }
    }

    internal static Uri ValidateRecord(GatewayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateGatewayId(record.Id);
        ValidateFamilyName(record.NativePackageFamilyName);
        if (record.SshTunnel is not null || record.SetupManagedDistroName is not null ||
            !Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "ws" || !uri.IsLoopback || uri.Port is < 1 or > 65535 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
        {
            throw new ArgumentException("A native gateway requires a plain loopback ws endpoint without a tunnel or WSL distro.");
        }
        return uri;
    }

    internal static void ValidateFamilyName(string? familyName)
    {
        if (familyName is null || !Regex.IsMatch(familyName,
            @"\A(?:OpenClaw\.Gateway|OpenClawFoundation\.OpenClawGateway)_[a-z0-9]{13}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("An installed OpenClaw Gateway package family is required.");
        }
    }

    internal static void ValidatePackage(NativeGatewayPackage package, string expectedFamily)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateFamilyName(package.PackageFamilyName);
        if (!string.Equals(package.PackageFamilyName, expectedFamily, StringComparison.Ordinal) ||
            !Version.TryParse(package.Version, out _))
        {
            throw new InvalidOperationException("The installed native gateway package does not match the saved package identity.");
        }
        ValidateAlias(package.OpenClawAliasPath, expectedFamily, "openclaw.exe");
        ValidateAlias(package.ClawCtlAliasPath, expectedFamily, "clawctl.exe");
    }

    private static void ValidateAlias(string path, string family, string alias)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", family, alias);
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The native gateway executable must be its trusted package-qualified alias.");
        }
    }
}
