using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class WslKeepAlivePolicy
{
    public static bool ShouldStart(GatewayRecord? activeRecord, string? legacyGatewayUrl)
    {
        if (activeRecord is not null)
        {
            if (activeRecord.SshTunnel is not null)
                return false;

            return activeRecord.IsLocal
                || !string.IsNullOrWhiteSpace(activeRecord.SetupManagedDistroName)
                || LocalGatewayUrlClassifier.IsLocalGatewayUrl(activeRecord.Url);
        }

        return LocalGatewayUrlClassifier.IsLocalGatewayUrl(legacyGatewayUrl ?? string.Empty);
    }

    public static string? ResolveDistroName(
        GatewayRecord? activeRecord,
        string? setupStateDistroName,
        string? environmentOverride)
    {
        if (!string.IsNullOrWhiteSpace(activeRecord?.SetupManagedDistroName))
            return activeRecord.SetupManagedDistroName;

        if (!string.IsNullOrWhiteSpace(setupStateDistroName))
            return setupStateDistroName;

#if DEBUG || OPENCLAW_TRAY_TESTS
        if (!string.IsNullOrWhiteSpace(environmentOverride))
            return environmentOverride;
#endif

        return "OpenClawGateway";
    }

    public static IReadOnlyList<string> FindStaleSetupManagedDistroNames(
        IEnumerable<GatewayRecord> records,
        IEnumerable<string> markerDistroNames,
        string? setupStateDistroName)
    {
        var managedLocalDistros = records
            .Where(IsSetupManagedLocalRecord)
            .Select(r => r.SetupManagedDistroName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = markerDistroNames
            .Append(setupStateDistroName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(distro => !managedLocalDistros.Contains(distro!))
            .Select(distro => distro!)
            .ToArray();
    }

    public static bool IsKeepaliveCommandLine(string? commandLine, string distroName)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(distroName))
            return false;

        return commandLine.Contains(distroName, StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains("sleep", StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains("infinity", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetMarkerDistroName(string markerJson, out string distroName)
    {
        distroName = string.Empty;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(markerJson);
            if (!doc.RootElement.TryGetProperty("DistroName", out var distroElement))
                return false;

            distroName = distroElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(distroName);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsSetupManagedLocalRecord(GatewayRecord record)
    {
        if (record.SshTunnel is not null)
            return false;

        if (string.IsNullOrWhiteSpace(record.SetupManagedDistroName))
            return false;

        return record.IsLocal || LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url);
    }
}
