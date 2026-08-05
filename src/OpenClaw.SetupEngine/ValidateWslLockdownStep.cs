using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class ValidateWslLockdownStep : SetupStep
{
    private const int MaxWslConfReadAttempts = 3;

    public override string Id => "validate-wsl-lockdown";
    public override string DisplayName => "Validate WSL lockdown";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wsl = ctx.Config.Wsl;

        var readConf = await ReadWslConfWithStartupRetryAsync(ctx, distro, ct);
        if (readConf.ExitCode != 0)
            return StepResult.Terminal("Cannot read /etc/wsl.conf - WSL configuration may not have been applied");

        var errors = ValidateWslConf(readConf.Stdout, wsl);
        if (errors.Count > 0)
        {
            var msg = "WSL lockdown validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}"));
            return StepResult.Terminal(msg);
        }

        var requiredDirs = new[]
        {
            $"/home/{wsl.User}/.openclaw",
            "/var/lib/openclaw",
            "/var/log/openclaw",
            "/opt/openclaw"
        };

        // Generate per-directory checks inline (no bash variables).
        // wsl.exe argv variable-expansion pitfall: see docs/WSL_EXE_ARGV_PITFALL.md.
        // `wsl.exe -- bash -c <script>` performs shell-variable expansion on argv
        // before bash sees it, so any $var that isn't defined in the Windows env
        // gets dropped. This step works around the issue by C#-interpolating every
        // value into the script string (no bash variables) — that pattern is fine
        // for short scripts with a small fixed value set and no spaces in values.
        // New multi-line callers should prefer the stdin path:
        //   ctx.Commands.RunInWslAsync(..., inputViaStdin: true)
        // which pipes the script via `bash -s` stdin and bypasses the issue entirely.
        var dirChecks = new System.Text.StringBuilder();
        foreach (var d in requiredDirs)
        {
            dirChecks.AppendLine($"test -d {d} || {{ echo DIR_MISSING:{d}; exit 1; }}");
            dirChecks.AppendLine($"test $(stat -c %U {d} 2>/dev/null) = {wsl.User} || {{ echo OWNER_MISMATCH:{d}:$(stat -c %U {d} 2>/dev/null); exit 1; }}");
        }

        var verifyScript = "set -e\n"
            + $"id -u {wsl.User} &>/dev/null || {{ echo USER_MISSING; exit 1; }}\n"
            + dirChecks
            + "echo LOCKDOWN_VALID\n";

        var verify = await ctx.Commands.RunInWslAsync(distro, verifyScript, TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Debug($"Lockdown verify exit={verify.ExitCode} stdout={verify.Stdout.Trim()} stderr={verify.Stderr.Trim()}");

        if (verify.Stdout.Contains("USER_MISSING", StringComparison.Ordinal))
            return StepResult.Terminal($"User '{wsl.User}' does not exist in distro '{distro}'");

        if (verify.Stdout.Contains("DIR_MISSING:", StringComparison.Ordinal))
        {
            var line = verify.Stdout.Split('\n').FirstOrDefault(l => l.Contains("DIR_MISSING:")) ?? "";
            var dir = line.Trim().Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "unknown";
            return StepResult.Terminal($"Required directory missing: {dir}");
        }

        if (verify.Stdout.Contains("OWNER_MISMATCH:", StringComparison.Ordinal))
        {
            var line = verify.Stdout.Split('\n').FirstOrDefault(l => l.Contains("OWNER_MISMATCH:")) ?? "";
            var parts = line.Trim().Split(':');
            return StepResult.Terminal($"Directory {parts.ElementAtOrDefault(1)} owned by '{parts.ElementAtOrDefault(2)}', expected '{wsl.User}'");
        }

        if (!verify.Stdout.Contains("LOCKDOWN_VALID", StringComparison.Ordinal))
        {
            var detail = string.IsNullOrWhiteSpace(verify.Stderr) ? verify.Stdout.Trim() : verify.Stderr.Trim();
            return StepResult.Terminal($"WSL lockdown validation failed: {detail}");
        }

        if (!string.IsNullOrEmpty(wsl.Memory))
            ctx.Logger.Warn($"Wsl.Memory='{wsl.Memory}' is set but requires host-level .wslconfig, not per-distro wsl.conf");
        if (!string.IsNullOrEmpty(wsl.Swap))
            ctx.Logger.Warn($"Wsl.Swap='{wsl.Swap}' is set but requires host-level .wslconfig, not per-distro wsl.conf");

        ctx.Logger.Info("WSL lockdown validated: all invariants verified");
        return StepResult.Ok("WSL lockdown validated");
    }

    private static async Task<CommandResult> ReadWslConfWithStartupRetryAsync(
        SetupContext ctx,
        string distro,
        CancellationToken ct)
    {
        CommandResult? last = null;
        for (var attempt = 1; attempt <= MaxWslConfReadAttempts; attempt++)
        {
            last = await ctx.Commands.RunInWslAsync(
                distro,
                "cat /etc/wsl.conf",
                TimeSpan.FromSeconds(30),
                ct: ct);

            if (last.ExitCode == 0)
                return last;

            if (attempt == MaxWslConfReadAttempts)
                break;

            ctx.Logger.Warn(
                $"Reading /etc/wsl.conf failed after WSL restart (attempt {attempt}/{MaxWslConfReadAttempts}, timedOut={last.TimedOut}); retrying");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return last ?? new CommandResult(-1, "", "No WSL config read attempts were made.", TimeSpan.Zero, TimedOut: false);
    }

    internal static List<string> ValidateWslConf(string conf, WslConfig wsl)
    {
        var values = ParseWslConf(conf);
        var errors = new List<string>();

        ValidateConfValue(values, "boot", "systemd", wsl.Systemd, errors);
        ValidateConfValue(values, "interop", "enabled", wsl.Interop, errors);
        ValidateConfValue(values, "interop", "appendWindowsPath", wsl.AppendWindowsPath, errors);
        ValidateConfValue(values, "automount", "enabled", wsl.Automount, errors);
        ValidateConfValue(values, "automount", "mountFsTab", wsl.MountFsTab, errors);
        ValidateConfValue(values, "user", "default", wsl.User, errors);

        return errors;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseWslConf(string conf)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        using var reader = new StringReader(conf);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                if (!values.ContainsKey(currentSection))
                    values[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (currentSection is null)
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            values[currentSection][key] = value;
        }

        return values;
    }

    private static void ValidateConfValue(Dictionary<string, Dictionary<string, string>> conf, string section, string key, bool expected, List<string> errors) =>
        ValidateConfValue(conf, section, key, expected.ToString().ToLowerInvariant(), errors);

    private static void ValidateConfValue(Dictionary<string, Dictionary<string, string>> conf, string section, string key, string expected, List<string> errors)
    {
        if (!conf.TryGetValue(section, out var sectionValues) ||
            !sectionValues.TryGetValue(key, out var actual) ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Expected [{section}] {key}={expected} in wsl.conf");
        }
    }
}
