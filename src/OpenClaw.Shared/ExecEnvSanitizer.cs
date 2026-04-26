using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace OpenClaw.Shared;

internal sealed class ExecEnvSanitizeResult
{
    public Dictionary<string, string>? Allowed { get; init; }
    public string[] Blocked { get; init; } = Array.Empty<string>();
}

internal static class ExecEnvSanitizer
{
    private static readonly FrozenSet<string> _blockedNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH",
            "PATHEXT",
            "ComSpec",
            "PSModulePath",
            "NODE_OPTIONS",
            "NODE_PATH",
            "PYTHONPATH",
            "PYTHONSTARTUP",
            "PYTHONUSERBASE",
            "RUBYOPT",
            "RUBYLIB",
            "PERL5OPT",
            "PERL5LIB",
            "PERLIO",
            "GIT_SSH",
            "GIT_SSH_COMMAND",
            "GIT_EXEC_PATH",
            "GIT_PROXY_COMMAND",
            "GIT_ASKPASS",
            "BASH_ENV",
            "ENV",
            "CDPATH",
            "PROMPT_COMMAND",
            "ZDOTDIR",
            "LD_PRELOAD",
            "LD_LIBRARY_PATH",
            "LD_AUDIT",
            "DYLD_INSERT_LIBRARIES",
            "DYLD_LIBRARY_PATH"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static ExecEnvSanitizeResult Sanitize(Dictionary<string, string>? env)
    {
        if (env is not { Count: > 0 })
        {
            return new ExecEnvSanitizeResult { Allowed = env };
        }

        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new List<string>();

        foreach (var (name, value) in env)
        {
            if (IsBlocked(name))
            {
                blocked.Add(name);
                continue;
            }

            allowed[name] = value;
        }

        return new ExecEnvSanitizeResult
        {
            Allowed = allowed.Count > 0 ? allowed : null,
            Blocked = blocked.ToArray()
        };
    }

    internal static bool IsBlocked(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        if (name.IndexOfAny(['=', '\0', '\r', '\n']) >= 0)
            return true;

        foreach (var c in name)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return true;
        }

        return _blockedNames.Contains(name)
            || name.StartsWith("LD_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase);
    }
}
