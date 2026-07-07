using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Detects existing local gateway configuration to show accurate replacement summaries.
/// </summary>
public sealed class ExistingConfigDetector
{
    public sealed record ExistingConfig(
        bool HasLocalGateway,
        string? LocalGatewayId,
        string? LocalGatewayUrl,
        bool HasDistro,
        string? DistroName,
        bool HasIdentityFiles,
        int PreservedGatewayCount,
        IReadOnlyList<string> PreservedGatewayNames,
        GatewayInstallMode? LocalGatewayMode = null);

    /// <summary>
    /// Detect existing local configuration by checking the gateway registry and WSL distros.
    /// </summary>
    public static ExistingConfig Detect(string dataDir, string localDataDir, SetupConfig config)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var all = registry.GetAll();

        var localRecord = all.FirstOrDefault(r => r.IsLocal && r.SshTunnel == null);
        var preserved = all.Where(r => !r.IsLocal || r.SshTunnel != null).ToList();

        var hasDistro = false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe", "--list --quiet")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                hasDistro = WslInstallSupport.ContainsDistro(output, config.DistroName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WSL distro detection failed: {ex.Message}");
        }

        var hasIdentity = false;
        if (localRecord != null)
        {
            var identityDir = registry.GetIdentityDirectory(localRecord.Id);
            hasIdentity = Directory.Exists(identityDir) && Directory.EnumerateFiles(identityDir).Any();
        }

        return new ExistingConfig(
            HasLocalGateway: localRecord != null,
            LocalGatewayId: localRecord?.Id,
            LocalGatewayUrl: localRecord?.Url,
            HasDistro: hasDistro,
            DistroName: hasDistro ? config.DistroName : null,
            HasIdentityFiles: hasIdentity,
            PreservedGatewayCount: preserved.Count,
            PreservedGatewayNames: preserved.Select(r => r.FriendlyName ?? r.Url).ToList(),
            LocalGatewayMode: GatewayInstallModeDetector.HasManagedNativeInstallation(dataDir, localDataDir, config)
                ? GatewayInstallMode.NativeWindows
                : DetectLocalGatewayMode(localRecord));
    }

    /// <summary>
    /// Build a human-readable summary of what will happen during setup.
    /// </summary>
    public static string BuildReplacementSummary(
        ExistingConfig config,
        GatewayInstallMode installMode = GatewayInstallMode.Wsl)
    {
        if (installMode == GatewayInstallMode.NativeWindows)
            return BuildNativeReplacementSummary(config);

        if (!config.HasLocalGateway
            && !config.HasDistro
            && config.LocalGatewayMode != GatewayInstallMode.NativeWindows)
            return "A new local WSL gateway will be created. No existing configuration will be affected.";

        var lines = new List<string>();

        if (config.HasDistro)
            lines.Add($"• WSL distro '{config.DistroName}' will be deleted and recreated");
        if (config.LocalGatewayMode == GatewayInstallMode.NativeWindows)
            lines.Add("• The native Windows gateway service, setup-managed configuration, and local connection record will be removed");
        else if (config.HasLocalGateway)
            lines.Add("• Local gateway record will be replaced");
        if (config.HasIdentityFiles)
            lines.Add("• Device identity files for the local gateway will be regenerated");

        if (config.PreservedGatewayCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"The following {config.PreservedGatewayCount} gateway(s) will NOT be affected:");
            foreach (var name in config.PreservedGatewayNames)
                lines.Add($"  • {name}");
        }

        return string.Join("\n", lines);
    }

    public static string BuildReplacementTitle(ExistingConfig config, GatewayInstallMode installMode) =>
        installMode == GatewayInstallMode.NativeWindows
            ? "Install a native Windows gateway?"
            : config.LocalGatewayMode == GatewayInstallMode.NativeWindows
                ? "Replace existing native Windows gateway?"
                : config.HasLocalGateway || config.HasDistro
                    ? "Replace existing WSL gateway?"
                    : "Install a new WSL gateway?";

    private static GatewayInstallMode? DetectLocalGatewayMode(GatewayRecord? record)
    {
        if (record is null)
            return null;
        if (!string.IsNullOrWhiteSpace(record.SetupManagedDistroName))
            return GatewayInstallMode.Wsl;
        return string.Equals(record.FriendlyName, "Local (Windows)", StringComparison.Ordinal)
            ? GatewayInstallMode.NativeWindows
            : null;
    }

    private static string BuildNativeReplacementSummary(ExistingConfig config)
    {
        var lines = new List<string>
        {
            "OpenClaw and a private gateway will be installed directly on Windows. WSL is not required."
        };

        if (config.HasLocalGateway)
            lines.Add("• The current local gateway connection will be replaced");
        if (config.HasIdentityFiles)
            lines.Add("• Device identity files for the local gateway will be regenerated");
        if (config.HasDistro)
            lines.Add($"• Existing WSL gateway '{config.DistroName}' will be stopped but its files will be preserved");

        if (config.PreservedGatewayCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"The following {config.PreservedGatewayCount} gateway(s) will NOT be affected:");
            foreach (var name in config.PreservedGatewayNames)
                lines.Add($"  • {name}");
        }

        return string.Join("\n", lines);
    }
}
