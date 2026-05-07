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
            "DYLD_LIBRARY_PATH",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AZURE_CLIENT_SECRET",
            "GITHUB_TOKEN",
            "GH_TOKEN",
            "NPM_TOKEN",
            "OPENAI_API_KEY"
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

        // Vectorized scan: any char in [0x00, 0x20] covers all ASCII control characters
        // (0x01–0x1F) plus space (0x20) in a single SIMD pass — the common fast path for
        // the ASCII-only names that make up virtually all environment variable keys.
        var span = name.AsSpan();
        if (span.IndexOfAnyInRange('\x00', '\x20') >= 0)
            return true;
        // DEL (0x7F) — control char outside the range above.
        if (span.IndexOf('\x7F') >= 0)
            return true;
        // Non-ASCII Unicode control / whitespace (rare; UTF-8 env var names are uncommon).
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c > '\x7F' && (char.IsControl(c) || char.IsWhiteSpace(c)))
                return true;
        }

        return _blockedNames.Contains(name)
            || HasCredentialMarker(name)
            || name.StartsWith("LD_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCredentialMarker(string name)
    {
        return HasSegment(name, "TOKEN")
            || HasSegment(name, "SECRET")
            || HasSegment(name, "PASSWORD")
            || HasSegment(name, "PASSWD")
            || HasCompoundMarker(name, "API", "KEY")
            || HasCompoundMarker(name, "ACCESS", "KEY")
            || HasCompoundMarker(name, "PRIVATE", "KEY")
            || HasCompoundMarker(name, "CLIENT", "SECRET")
            || HasCompoundMarker(name, "CONNECTION", "STRING")
            || HasSegment(name, "CREDENTIAL")
            || HasSegment(name, "CREDENTIALS")
            || name.Contains("CONNSTR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCompoundMarker(string name, string first, string second)
    {
        var span = name.AsSpan();
        var firstSpan = first.AsSpan();
        var secondSpan = second.AsSpan();
        var start = 0;
        var previousMatched = false;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] is not ('_' or '-' or '.'))
                continue;

            var current = span[start..i];
            if (previousMatched && current.Equals(secondSpan, StringComparison.OrdinalIgnoreCase))
                return true;

            previousMatched = current.Equals(firstSpan, StringComparison.OrdinalIgnoreCase);
            start = i + 1;
        }

        return false;
    }

    private static bool HasSegment(string name, string segment)
    {
        var span = name.AsSpan();
        var segmentSpan = segment.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] is not ('_' or '-' or '.'))
                continue;

            if (span[start..i].Equals(segmentSpan, StringComparison.OrdinalIgnoreCase))
                return true;

            start = i + 1;
        }

        return false;
    }
}
