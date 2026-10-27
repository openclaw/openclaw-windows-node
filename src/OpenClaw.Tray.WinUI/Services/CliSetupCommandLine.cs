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
        if (request.ConfigPath is { } configPath)
        {
            arguments.Add(ConfigOption);
            arguments.Add(configPath);
        }

        return [.. arguments];
    }

    private static bool Fail(string message, out CliSetupRequest request, out string? error)
    {
        request = new CliSetupRequest(null, false, false);
        error = message;
        return false;
    }
}
