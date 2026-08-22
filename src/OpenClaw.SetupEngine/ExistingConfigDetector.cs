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
        IReadOnlyList<string> PreservedGatewayNames);

    /// <summary>
    /// Detect existing local configuration by checking the gateway registry and WSL distros.
    /// </summary>
    public static ExistingConfig Detect(string dataDir, string targetDistroName)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var all = registry.GetAll();

        var localRecord = all.FirstOrDefault(r => r.IsLocal && r.SshTunnel == null);
        var preserved = all.Where(r => !r.IsLocal || r.SshTunnel != null).ToList();

        var logger = new SetupLogger(filePath: null, LogLevel.Warn);
        var result = new CommandRunner(logger)
            .RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        var hasDistro = InterpretDistroList(result, targetDistroName);

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
            DistroName: hasDistro ? targetDistroName : null,
            HasIdentityFiles: hasIdentity,
            PreservedGatewayCount: preserved.Count,
            PreservedGatewayNames: preserved.Select(r => r.FriendlyName ?? r.Url).ToList());
    }

    internal static bool InterpretDistroList(CommandResult result, string targetDistroName)
    {
        if (!result.TimedOut && result.ExitCode == 0)
            return WslInstallSupport.ContainsDistro(result.Stdout, targetDistroName);

        if (!result.TimedOut && WslViabilityInspector.LooksUnavailable(result))
            return false;

        throw new InvalidOperationException(
            "OpenClaw could not safely inspect existing WSL distributions. " +
            "Run `wsl --list --quiet` in PowerShell, resolve the reported problem, and try again.");
    }

    /// <summary>
    /// Build a human-readable summary of what will happen during setup.
    /// </summary>
    public static string BuildReplacementSummary(ExistingConfig config)
    {
        if (!config.HasLocalGateway && !config.HasDistro)
            return "A new local WSL gateway will be created. No existing configuration will be affected.";

        var lines = new List<string>();

        if (config.HasDistro)
            lines.Add($"• WSL distro '{config.DistroName}' will be deleted and recreated");
        if (config.HasLocalGateway)
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
}
