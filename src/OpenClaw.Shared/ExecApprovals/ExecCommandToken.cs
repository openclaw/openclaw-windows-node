using System;
using System.IO;

namespace OpenClaw.Shared.ExecApprovals;

// Utility helpers for command token classification.
internal static class ExecCommandToken
{
    // Returns the lowercased last path component (basename) of a token, without extension.
    internal static string BasenameLower(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0) return string.Empty;
        var name = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (name.Length == 0) name = trimmed;
        return name.ToLowerInvariant();
    }

    // Returns the basename without .exe suffix (lowercased).
    internal static string NormalizedBasename(string token)
    {
        var b = BasenameLower(token);
        return b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? b[..^4] : b;
    }

    internal static bool IsEnv(string token) =>
        NormalizedBasename(token) == "env";

    // Shell interpreters re-parse their own argument tail (metacharacters like &, |, ;),
    // so an approved argv passed to one of these does NOT reach a single process verbatim:
    // the shell can split it into additional commands the user never saw. Any executable
    // in this set therefore cannot honor the approve-what-you-see guarantee.
    private static readonly System.Collections.Generic.HashSet<string> s_shellInterpreters =
        new(StringComparer.Ordinal)
        {
            "sh", "bash", "zsh", "dash", "ash", "ksh", "fish",
            "cmd", "powershell", "pwsh",
        };

    internal static bool IsShellInterpreter(string token) =>
        s_shellInterpreters.Contains(NormalizedBasename(token));
}
