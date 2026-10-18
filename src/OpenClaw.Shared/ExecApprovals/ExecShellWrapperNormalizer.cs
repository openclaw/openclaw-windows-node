using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Single-level shell wrapper detection for the V2 exec approval pipeline.
// Differs from the legacy ExecShellWrapperParser.Expand (BFS multi-level, string-based).
// This normalizer operates on argv (IReadOnlyList<string>) and performs one level of
// wrapper detection, with recursive env-prefix unwrapping up to MaxWrapperDepth.
// Step 2 of the approval pipeline: normalize command form.
internal static class ExecShellWrapperNormalizer
{
    private enum WrapperKind { Posix, Cmd, PowerShell }

    private sealed record WrapperSpec(WrapperKind Kind, HashSet<string> Names);

    private static readonly HashSet<string> s_posixInlineFlags =
        new(StringComparer.Ordinal) { "-lc", "-c", "--command" };

    private static readonly HashSet<string> s_powerShellInlineFlags =
        new(StringComparer.OrdinalIgnoreCase) { "-c", "-command", "--command", "/c", "/command" };

    // Switches that take no argument. A prefix that also matches one of these
    // is that switch: -i and -in are Interactive, not InputFormat.
    private static readonly string[] s_powerShellSwitchNames =
    [
        "Interactive",
        "Login",
        "MTA",
        "NoExit",
        "NoLogo",
        "NonInteractive",
        "NoProfile",
        "NoProfileLoadTime",
        "SSHServerMode",
        "STA",
    ];

    // Canonical pwsh parameters that take one following argument. A unique
    // prefix binds the same way (-wo and -wor are -WorkingDirectory), unless
    // that prefix also matches a switch above.
    private static readonly string[] s_powerShellValueOptionNames =
    [
        "WorkingDirectory",
        "ExecutionPolicy",
        "InputFormat",
        "OutputFormat",
        "ConfigurationName",
        "ConfigurationFile",
        "CustomPipeName",
        "EncodedCommand",
        "SettingsFile",
        "PSConsoleFile",
        "WindowStyle",
        "Version",
    ];

    // Forms that are not a unique prefix of one canonical name. -wd is the
    // WorkingDirectory alias. -w is WindowStyle, which also prefixes
    // WorkingDirectory. -ep and -if are the short ExecutionPolicy and
    // InputFormat aliases. -config matches both ConfigurationName and
    // ConfigurationFile.
    private static readonly HashSet<string> s_powerShellValueAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "-wd", "/wd",
            "-w", "/w",
            "-ep", "/ep",
            "-e", "/e",
            "-ec", "/ec",
            "-if", "/if",
            "-config", "/config",
            "-of", "/of",
        };

    private static readonly WrapperSpec[] s_specs =
    [
        new(WrapperKind.Posix,      new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ash", "sh", "bash", "zsh", "dash", "ksh", "fish" }),
        new(WrapperKind.Cmd,        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cmd", "cmd.exe" }),
        new(WrapperKind.PowerShell, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "powershell", "powershell.exe", "pwsh", "pwsh.exe" }),
    ];

    internal sealed record ParsedWrapper(bool IsWrapper, string? InlineCommand);

    internal static readonly ParsedWrapper NotWrapper = new(false, null);

    // Detects a single-level shell wrapper in argv.
    // rawCommand is always null in Windows v1 (not in the system.run protocol).
    // Detection is on argv only; rawCommand is accepted for API compatibility with future use.
    internal static ParsedWrapper Extract(IReadOnlyList<string> command, string? rawCommand = null)
        => ExtractInner(command, rawCommand, 0);

    private static ParsedWrapper ExtractInner(
        IReadOnlyList<string> command, string? rawCommand, int depth)
    {
        if (depth >= ExecEnvInvocationUnwrapper.MaxWrapperDepth) return NotWrapper;
        if (command.Count == 0) return NotWrapper;

        var token0 = command[0].Trim();
        if (token0.Length == 0) return NotWrapper;

        // Recursively unwrap transparent env prefixes.
        if (ExecCommandToken.IsEnv(token0))
        {
            var unwrapped = ExecEnvInvocationUnwrapper.Unwrap(command);
            if (unwrapped is null) return NotWrapper;
            return ExtractInner(unwrapped, rawCommand, depth + 1);
        }

        var basename = ExecCommandToken.NormalizedBasename(token0);
        var spec = Array.Find(s_specs, s => s.Names.Contains(basename));
        if (spec is null) return NotWrapper;

        var payload = ExtractPayload(command, spec);
        if (payload is null) return NotWrapper;

        return new ParsedWrapper(true, payload);
    }

    private static string? ExtractPayload(IReadOnlyList<string> command, WrapperSpec spec) =>
        spec.Kind switch
        {
            WrapperKind.Posix      => ExtractPosixPayload(command),
            WrapperKind.Cmd        => ExtractCmdPayload(command),
            WrapperKind.PowerShell => ExtractPowerShellPayload(command),
            _                      => null,
        };

    private static string? ExtractPosixPayload(IReadOnlyList<string> command)
    {
        var fish = IsFishShell(command[0]);
        for (var i = 1; i < command.Count; i++)
        {
            var flag = command[i].Trim();
            if (flag.Length == 0) continue;
            if (flag == "--") return null;
            if (s_posixInlineFlags.Contains(flag) || IsPosixInlineCluster(flag) || (fish && IsFishInitCommand(flag)))
            {
                if (i + 1 >= command.Count) return null;
                var payload = command[i + 1].Trim();
                return payload.Length == 0 ? null : payload;
            }

            if (!flag.StartsWith('-'))
                return null;
        }
        return null;
    }

    private static string? ExtractCmdPayload(IReadOnlyList<string> command)
    {
        for (var i = 1; i < command.Count; i++)
        {
            if (string.Equals(command[i].Trim(), "/c", StringComparison.OrdinalIgnoreCase))
            {
                var tail = string.Join(" ", command.Skip(i + 1)).Trim();
                return tail.Length == 0 ? null : tail;
            }
        }
        return null;
    }

    private static string? ExtractPowerShellPayload(IReadOnlyList<string> command)
    {
        var windowsPowerShell = IsWindowsPowerShellHost(command[0]);
        for (var i = 1; i < command.Count; i++)
        {
            var t = command[i].Trim();
            if (t.Length == 0) continue;
            if (t == "--") return null;
            if (IsPowerShellValueOption(t, windowsPowerShell))
            {
                i++;
                continue;
            }

            if (IsPowerShellFileSwitch(t))
                return null;
            if (TryReadPowerShellColonPayload(t, out var inline))
                return inline.Length == 0 ? null : inline;
            if (IsPowerShellInlineFlag(t))
            {
                if (i + 1 >= command.Count) return null;
                var payload = command[i + 1].Trim();
                return payload.Length == 0 ? null : payload;
            }

            if (!t.StartsWith('-') && !t.StartsWith('/'))
            {
                // Windows PowerShell defaults to -Command for positional text,
                // including a lone script name. Explicit -File stays a script.
                return windowsPowerShell ? t : null;
            }
        }
        return null;
    }

    private static bool IsWindowsPowerShellHost(string executable) =>
        ExecCommandToken.NormalizedBasename(executable).Equals("powershell", StringComparison.Ordinal);

    private static bool IsFishShell(string token)
        => ExecCommandToken.NormalizedBasename(token).Equals("fish", StringComparison.OrdinalIgnoreCase);

    private static bool IsFishInitCommand(string flag)
        => flag == "-C" || flag.Equals("--init-command", StringComparison.Ordinal);

    private static bool IsPowerShellValueOption(string token, bool windowsPowerShell)
    {
        if (token.IndexOf(':') > 0)
            return false;
        if (s_powerShellValueAliases.Contains(token))
            return true;
        if (!TryGetPowerShellSwitchBody(token, out var body))
            return false;
        if (IsPowerShellValueAliasBody(body))
            return true;

        var switchMatches = CountPrefixMatches(body, s_powerShellSwitchNames);
        var valueMatches = CountPrefixMatches(body, s_powerShellValueOptionNames);
        if (switchMatches > 0 &&
            !(windowsPowerShell && IsInteractivePrefix(body) && valueMatches == 1))
        {
            return false;
        }

        return valueMatches == 1;
    }

    private static bool IsInteractivePrefix(string body) =>
        "interactive".StartsWith(body, StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellValueAliasBody(string body)
    {
        foreach (var alias in s_powerShellValueAliases)
        {
            if (!TryGetPowerShellSwitchBody(alias, out var aliasBody))
                continue;
            if (aliasBody.Equals(body, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int CountPrefixMatches(string body, string[] names)
    {
        var matches = 0;
        foreach (var name in names)
        {
            if (name.StartsWith(body, StringComparison.OrdinalIgnoreCase))
                matches++;
        }

        return matches;
    }

    private static bool IsPowerShellInlineFlag(string token)
    {
        if (s_powerShellInlineFlags.Contains(token))
            return true;
        if (!TryGetPowerShellSwitchBody(token, out var body))
            return false;
        if (body.Equals("c", StringComparison.OrdinalIgnoreCase))
            return true;
        if (body.Length < 2)
            return false;

        return "command".StartsWith(body, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPowerShellSwitchBody(string token, out string body)
    {
        body = "";
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            body = token[2..];
            return body.Length > 0;
        }

        if (token.Length < 2 || (token[0] != '-' && token[0] != '/'))
            return false;

        body = token[1..];
        return body.Length > 0;
    }

    private static bool IsPosixInlineCluster(string flag)
    {
        if (flag.Length < 3 || flag[0] != '-' || flag[1] == '-')
            return false;
        var sawCommand = false;
        for (var i = 1; i < flag.Length; i++)
        {
            if (!char.IsLetter(flag[i]))
                return false;
            if (flag[i] == 'c')
                sawCommand = true;
        }

        return sawCommand;
    }

    private static bool IsPowerShellFileSwitch(string token)
    {
        var name = token;
        var colon = token.IndexOf(':');
        if (colon > 0)
            name = token[..colon];
        if (!TryGetPowerShellSwitchBody(name, out var body))
            return false;

        return body.Length > 0 &&
            "file".StartsWith(body, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadPowerShellColonPayload(string token, out string payload)
    {
        payload = "";
        var colon = token.IndexOf(':');
        if (colon <= 0) return false;
        var flag = token[..colon];
        if (!s_powerShellInlineFlags.Contains(flag)) return false;
        payload = token[(colon + 1)..].Trim();
        return true;
    }
}
