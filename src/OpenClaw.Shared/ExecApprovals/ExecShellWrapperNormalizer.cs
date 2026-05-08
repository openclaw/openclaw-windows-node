using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Single-level shell wrapper detection for the V2 exec approval pipeline.
// Differs from the legacy ExecShellWrapperParser.Expand (BFS multi-level, string-based).
// This normalizer operates on argv (IReadOnlyList<string>) and performs one level of
// wrapper detection, with recursive env-prefix unwrapping up to MaxWrapperDepth.
// Rail 18 step 2: normalize command form.
internal static class ExecShellWrapperNormalizer
{
    private enum WrapperKind { Posix, Cmd, PowerShell }

    private sealed record WrapperSpec(WrapperKind Kind, HashSet<string> Names);

    private static readonly HashSet<string> s_posixInlineFlags =
        new(StringComparer.OrdinalIgnoreCase) { "-lc", "-c", "--command" };

    private static readonly HashSet<string> s_powerShellInlineFlags =
        new(StringComparer.OrdinalIgnoreCase) { "-c", "-command", "--command" };

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
    // rawCommand is always null in Windows v1 (not in system.run protocol; research doc 05 OQ-V4).
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
        if (command.Count < 2) return null;
        var flag = command[1].Trim();
        if (!s_posixInlineFlags.Contains(flag)) return null;
        if (command.Count < 3) return null;
        var payload = command[2].Trim();
        return payload.Length == 0 ? null : payload;
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
        for (var i = 1; i < command.Count; i++)
        {
            var t = command[i].Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            if (t == "--") break;
            if (s_powerShellInlineFlags.Contains(t))
            {
                if (i + 1 >= command.Count) return null;
                var payload = command[i + 1].Trim();
                return payload.Length == 0 ? null : payload;
            }
        }
        return null;
    }
}
