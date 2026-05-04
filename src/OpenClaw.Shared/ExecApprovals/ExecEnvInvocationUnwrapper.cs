using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

// Strips `env [OPTIONS] [VAR=VAL...] COMMAND [ARGS...]` so the true executable can be resolved.
// Fail-closed: returns null when any unknown flag is encountered or the command cannot be safely
// unwrapped. Mirrors ExecEnvInvocationUnwrapper in the windows-app reference.
internal static class ExecEnvInvocationUnwrapper
{
    internal const int MaxWrapperDepth = 4;

    private static readonly Regex s_envAssignment =
        new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    // Strips one level of `env` wrapper.
    // Returns the remaining argv starting at the real COMMAND token, or null on any ambiguity.
    internal static IReadOnlyList<string>? Unwrap(IReadOnlyList<string> command)
    {
        var idx = 1;
        var expectsOptionValue = false;

        while (idx < command.Count)
        {
            var token = command[idx].Trim();
            if (token.Length == 0) { idx++; continue; }

            if (expectsOptionValue) { expectsOptionValue = false; idx++; continue; }

            if (token == "--" || token == "-") { idx++; break; }

            if (s_envAssignment.IsMatch(token)) { idx++; continue; }

            if (token.StartsWith('-') && token != "-")
            {
                var lower = token.ToLowerInvariant();
                var flag = lower.Split('=', 2)[0];

                if (ExecEnvOptions.FlagOnly.Contains(flag)) { idx++; continue; }

                if (ExecEnvOptions.WithValue.Contains(flag))
                {
                    if (!lower.Contains('=')) expectsOptionValue = true;
                    idx++;
                    continue;
                }

                if (ExecEnvOptions.InlineValuePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                {
                    idx++;
                    continue;
                }

                return null; // Unknown flag — fail-closed.
            }

            break; // Executable token found.
        }

        if (idx >= command.Count) return null;
        return command.Skip(idx).ToList();
    }

    // Returns true when the env invocation has flags or VAR=val assignments before the command.
    // `--` ends option processing without modifying the environment → not a modifier.
    // `-` alone replaces the environment entirely → modifier.
    internal static bool HasModifiers(IReadOnlyList<string> command)
    {
        for (var i = 1; i < command.Count; i++)
        {
            var token = command[i].Trim();
            if (token.Length == 0) continue;
            if (token == "--") return false;
            if (token == "-") return true;
            if (token.StartsWith('-')) return true;
            if (s_envAssignment.IsMatch(token)) return true;
            return false; // first non-modifier token is the command
        }
        return false;
    }

    // Iteratively strips env wrappers for executable resolution only.
    internal static IReadOnlyList<string> UnwrapForResolution(IReadOnlyList<string> command)
    {
        var current = command;
        for (var depth = 0; depth < MaxWrapperDepth; depth++)
        {
            if (current.Count == 0) break;
            var token = current[0].Trim();
            if (token.Length == 0) break;
            if (!ExecCommandToken.IsEnv(token)) break;
            var unwrapped = Unwrap(current);
            if (unwrapped is null || unwrapped.Count == 0) break;
            current = unwrapped;
        }
        return current;
    }
}
