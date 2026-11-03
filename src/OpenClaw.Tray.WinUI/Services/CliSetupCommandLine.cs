using System.Globalization;

namespace OpenClawTray.Services;

internal sealed record CliSetupRequest(string? ConfigPath, bool Force, bool ReplaceExisting);

/// <summary>
/// Strict command line for the tray's <c>--headless-setup</c> CLI mode, which runs the
/// WSL SetupEngine pipeline without UI. Distro name and port are supplied by the app
/// identity, matching the interactive setup window and <c>--uninstall</c>.
/// </summary>
internal static class CliSetupCommandLine
{
    internal const string HeadlessSetupFlag = "--headless-setup";
    internal const string ConfigOption = "--config";
    internal const string ForceFlag = "--force-setup";
    internal const string ReplaceExistingFlag = "--replace-existing";
    private const string ReplacementHeader = "Setup would replace existing configuration:";

    /// <param name="processArguments">
    /// <see cref="Environment.GetCommandLineArgs"/>; index 0 (the executable path) is skipped.
    /// </param>
    internal static bool TryParse(string[] processArguments, out CliSetupRequest request, out string? error)
    {
        string? configPath = null;
        var force = false;
        var replaceExisting = false;
        for (var i = 1; i < processArguments.Length; i++)
        {
            var token = processArguments[i];
            if (string.Equals(token, HeadlessSetupFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(token, ForceFlag, StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (string.Equals(token, ReplaceExistingFlag, StringComparison.OrdinalIgnoreCase))
            {
                replaceExisting = true;
                continue;
            }

            if (string.Equals(token, ConfigOption, StringComparison.OrdinalIgnoreCase))
            {
                if (configPath is not null)
                    return Fail($"{ConfigOption} may only be specified once.", out request, out error);

                var value = i < processArguments.Length - 1 ? processArguments[i + 1] : null;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                    return Fail($"{ConfigOption} requires a value.", out request, out error);

                configPath = value;
                i++;
                continue;
            }

            return Fail($"Unknown option '{token}'.", out request, out error);
        }

        request = new CliSetupRequest(configPath, force, replaceExisting);
        error = null;
        return true;
    }

    internal static string[] BuildEngineArguments(
        CliSetupRequest request, string dataDir, string localDataDir, string distroName, int gatewayPort)
    {
        var arguments = new List<string>
        {
            "--headless",
            "--rollback-on-failure",
            "--data-dir", dataDir,
            "--local-data-dir", localDataDir,
            "--distro-name", distroName,
            "--gateway-port", gatewayPort.ToString(CultureInfo.InvariantCulture),
        };
        if (!request.ReplaceExisting)
            arguments.Add("--preserve-existing");
        if (request.ConfigPath is { } configPath)
        {
            arguments.Add(ConfigOption);
            arguments.Add(configPath);
        }

        return [.. arguments];
    }

    /// <summary>
    /// Headless replacement admission: null when setup may run, otherwise the refusal message.
    /// State not proven to be app-owned is never replaced headlessly, even with --replace-existing.
    /// Interactive setup requires an explicit permanent-replacement confirmation for it (#1229).
    /// </summary>
    internal static string? GetReplacementRefusal(
        CliSetupRequest request,
        bool hasExistingState,
        bool ownershipUnproven,
        bool hasDistro,
        string distroName,
        string distroDataDirectory,
        string replacementSummary)
    {
        if (!hasExistingState)
            return null;

        if (ownershipUnproven)
        {
            var target = hasDistro ? $"WSL distro '{distroName}'" : $"WSL data for '{distroName}'";
            var removal = hasDistro ? $"`wsl --unregister {distroName}`" : $"delete '{distroDataDirectory}'";
            return $"{ReplacementHeader}\n{replacementSummary}\n" +
                $"{target} is not proven to be managed by OpenClaw and was preserved. " +
                "To replace it, run OpenClaw setup interactively and confirm its permanent replacement, " +
                $"or remove it yourself (for example, {removal}).";
        }

        return request.ReplaceExisting
            ? null
            : $"{ReplacementHeader}\n{replacementSummary}\nPass {ReplaceExistingFlag} to continue.";
    }

    private static bool Fail(string message, out CliSetupRequest request, out string? error)
    {
        request = new CliSetupRequest(null, false, false);
        error = message;
        return false;
    }
}
