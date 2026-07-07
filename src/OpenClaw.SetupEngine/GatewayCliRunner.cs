using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

internal static partial class GatewayCliRunner
{
    private const string CliPathEnvironmentVariable = "OPENCLAW_SETUP_CLI_PATH";
    private const string NpmPathEnvironmentVariable = "OPENCLAW_SETUP_NPM_PATH";
    internal static readonly TimeSpan NativeMinimumCommandTimeout = TimeSpan.FromSeconds(30);
    internal static IReadOnlyDictionary<string, string> GetManagedNativeEnvironmentDefaults(SetupConfig config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profile = GetManagedNativeProfile(config);
        var stateDir = GetManagedNativeStateDir(config);
        return new Dictionary<string, string>
        {
            ["OPENCLAW_PROFILE"] = profile,
            ["OPENCLAW_STATE_DIR"] = stateDir,
            ["OPENCLAW_CONFIG_PATH"] = Path.Combine(stateDir, "openclaw.json"),
            ["OPENCLAW_HOME"] = home,
            ["OPENCLAW_WINDOWS_TASK_NAME"] = GetManagedNativeTaskName(config),
            ["OPENCLAW_GATEWAY_PORT"] = "",
            ["OPENCLAW_GATEWAY_URL"] = "",
            ["OPENCLAW_WRAPPER"] = "",
        };
    }

    internal static string GetManagedNativeStateDir(SetupConfig config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profile = GetManagedNativeProfile(config);
        return string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(home, ".openclaw")
            : Path.Combine(home, $".openclaw-{profile}");
    }

    internal static string GetManagedNativeConfigPath(SetupConfig config) =>
        Path.Combine(GetManagedNativeStateDir(config), "openclaw.json");

    internal static string GetManagedNativeCliPrefix(string localDataDir) =>
        Path.Combine(localDataDir, "native-cli");

    internal static string GetManagedNativeTaskName(SetupConfig config)
    {
        var profile = GetManagedNativeProfile(config);
        return $"OpenClaw Gateway ({profile})";
    }

    internal static string GetManagedNativeProfile(SetupConfig config)
    {
        var profile = Regex.Replace(config.DistroName, "[^A-Za-z0-9._-]", "-").Trim('-');
        return string.IsNullOrWhiteSpace(profile)
            || string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase)
                ? $"companion-{config.GatewayPort}"
                : profile;
    }

    public static Task<CommandResult> RunAsync(
        SetupContext ctx,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? environmentArgument = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? trailingArguments = null)
    {
        if (environmentArgument is not null && !EnvironmentVariablePattern().IsMatch(environmentArgument))
            throw new ArgumentException("Invalid environment-variable argument name.", nameof(environmentArgument));

        return ctx.Config.InstallMode == GatewayInstallMode.NativeWindows
            ? RunNativeAsync(ctx, arguments, timeout, environment, environmentArgument, ct, trailingArguments)
            : RunWslAsync(ctx, arguments, timeout, environment, environmentArgument, ct, trailingArguments);
    }

    public static Task<CommandResult> RunNativeAsync(
        SetupContext ctx,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? environmentArgument = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? trailingArguments = null)
    {
        if (timeout < NativeMinimumCommandTimeout)
            timeout = NativeMinimumCommandTimeout;

        var cliPath = ctx.NativeCliPath ?? TryResolveNativeCliPath(ctx.LocalDataDir);
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return Task.FromResult(new CommandResult(
                -1,
                "",
                "OpenClaw CLI was not found in the current user PATH or standard install locations.",
                TimeSpan.Zero,
                false));
        }

        cliPath = PreferPowerShellShim(cliPath);
        ctx.NativeCliPath = cliPath;
        var commandEnvironment = environment is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(environment);
        if (environmentArgument is not null
            && commandEnvironment.TryGetValue(environmentArgument, out var transportedValue))
        {
            // Windows PowerShell 5 removes embedded quote characters when it builds the
            // native node.exe command line. Backslash-escape them for that single hop,
            // whether or not the CLI will parse the value as strict JSON.
            commandEnvironment[environmentArgument] = transportedValue.Replace("\"", "\\\"");
        }
        // The Windows app owns one predictable default-profile gateway. Never let
        // ambient CLI selectors redirect its config or Scheduled Task lifecycle.
        foreach (var (selector, defaultValue) in GetManagedNativeEnvironmentDefaults(ctx.Config))
        {
            if (!commandEnvironment.ContainsKey(selector))
                commandEnvironment[selector] = defaultValue;
        }
        commandEnvironment["PATH"] = GetRefreshedNativePath();
        commandEnvironment[CliPathEnvironmentVariable] = cliPath;

        var command = "& $env:" + CliPathEnvironmentVariable;
        if (arguments.Count > 0)
            command += " " + string.Join(' ', arguments.Select(PowerShellQuote));
        if (environmentArgument is not null)
            command += " $env:" + environmentArgument;
        if (trailingArguments is { Count: > 0 })
            command += " " + string.Join(' ', trailingArguments.Select(PowerShellQuote));

        return ctx.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            timeout,
            commandEnvironment,
            ct: ct);
    }

    internal static string MergeNativePaths(params string?[] values) =>
        string.Join(
            ';',
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string GetRefreshedNativePath()
    {
        var processPath = Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsWindows())
            return processPath ?? "";

        try
        {
            // Installers update the registry-backed machine/user PATH, but the
            // long-running tray process retains its startup environment.
            return MergeNativePaths(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                processPath);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.SecurityException)
        {
            return processPath ?? "";
        }
    }

    public static string? TryResolveNativeCliPath(string? localDataDir = null)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_SETUP_NATIVE_CLI") is { Length: > 0 } configured)
        {
            return File.Exists(configured)
                ? PreferPowerShellShim(Path.GetFullPath(configured))
                : null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(localDataDir))
        {
            AddCandidate(candidates, GetManagedNativeCliPrefix(localDataDir), "openclaw.ps1");
            AddCandidate(candidates, GetManagedNativeCliPrefix(localDataDir), "openclaw.cmd");
        }
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "openclaw.ps1");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "openclaw.cmd");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "openclaw.ps1");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "openclaw.cmd");

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory, "openclaw.ps1"));
            candidates.Add(Path.Combine(directory, "openclaw.cmd"));
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? TryResolveNativeCliPathFromNpmPrefix(TryResolveNpmPrefix());
    }

    internal static string? TryResolveNativeCliPathFromNpmPrefix(string? npmPrefix)
    {
        if (string.IsNullOrWhiteSpace(npmPrefix))
            return null;

        var powerShellShim = Path.Combine(npmPrefix.Trim(), "openclaw.ps1");
        if (File.Exists(powerShellShim))
            return powerShellShim;

        var cmdShim = Path.Combine(npmPrefix.Trim(), "openclaw.cmd");
        return File.Exists(cmdShim) ? cmdShim : null;
    }

    private static string? TryResolveNpmPrefix()
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("& $env:" + NpmPathEnvironmentVariable + " config get prefix");
        startInfo.Environment[NpmPathEnvironmentVariable] = npmPath;

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            Task.WaitAll(outputTask, errorTask);
            if (process.ExitCode != 0)
                return null;

            return outputTask.Result
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? FindNpmPath()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory, "npm.cmd"));
            candidates.Add(Path.Combine(directory, "npm.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    internal static string PreferPowerShellShim(string cliPath)
    {
        if (!string.Equals(Path.GetExtension(cliPath), ".cmd", StringComparison.OrdinalIgnoreCase))
            return cliPath;

        // Prefer one native-command parser instead of adding cmd.exe to the path.
        var powerShellShim = Path.ChangeExtension(cliPath, ".ps1");
        return File.Exists(powerShellShim) ? powerShellShim : cliPath;
    }

    private static Task<CommandResult> RunWslAsync(
        SetupContext ctx,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        string? environmentArgument,
        CancellationToken ct,
        IReadOnlyList<string>? trailingArguments)
    {
        var command = $"{ctx.WslPathPrefix} && openclaw";
        if (arguments.Count > 0)
            command += " " + string.Join(' ', arguments.Select(ShellQuote));
        if (environmentArgument is not null)
            command += " \"$" + environmentArgument + "\"";
        if (trailingArguments is { Count: > 0 })
            command += " " + string.Join(' ', trailingArguments.Select(ShellQuote));

        return ctx.Commands.RunInWslAsync(ctx.DistroName!, command, timeout, environment, ct);
    }

    private static void AddCandidate(List<string> candidates, string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        candidates.Add(parts.Aggregate(root, (current, part) => Path.Combine(current, part)));
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)]
    private static partial Regex EnvironmentVariablePattern();
}
