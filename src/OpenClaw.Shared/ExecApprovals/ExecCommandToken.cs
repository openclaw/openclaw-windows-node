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
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            trimmed = trimmed[1..^1];
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

    // A durable allow rule for one of these hosts delegates future meaning to a
    // second command language. The rule would approve the host executable, not the
    // script or command it interprets, so later invocations could execute different
    // commands without another approval.
    private static readonly System.Collections.Generic.HashSet<string> s_indirectCommandHosts =
        new(StringComparer.Ordinal)
        {
            "sh", "bash", "zsh", "dash", "ash", "ksh", "fish",
            "cmd", "powershell", "pwsh",
            "wsl", "cscript", "wscript",
            "py", "pyw", "python", "pythonw", "pypy",
            "node", "nodejs", "deno", "bun", "qjs",
            "ruby", "jruby", "perl", "php", "lua", "luajit",
            "java", "javaw", "jshell", "dotnet", "csi", "fsi", "fsharpi",
            "r", "rscript", "tclsh", "wish", "groovy",
        };

    internal static bool IsIndirectCommandHost(string token)
    {
        var basename = NormalizedBasename(token);
        return s_indirectCommandHosts.Contains(basename)
            || IsVersionedInterpreter(basename, "python")
            || IsVersionedInterpreter(basename, "pythonw")
            || IsVersionedInterpreter(basename, "pypy");
    }

    private static bool IsVersionedInterpreter(string basename, string prefix)
    {
        if (!basename.StartsWith(prefix, StringComparison.Ordinal)
            || basename.Length == prefix.Length)
        {
            return false;
        }

        var suffix = basename.AsSpan(prefix.Length);
        var sawDigit = false;
        foreach (var ch in suffix)
        {
            if (char.IsDigit(ch))
            {
                sawDigit = true;
                continue;
            }

            if (ch != '.')
                return false;
        }

        return sawDigit;
    }

    // Extracts the first shell-tokenized word from a command pattern. Quoted paths
    // remain one token, and a suffix after the closing quote is preserved so
    // `"git".exe` is classified as git.exe.
    internal static string? ParseFirstToken(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        var first = trimmed[0];
        if (first == '"' || first == '\'')
        {
            var rest = trimmed.AsSpan(1);
            var end = rest.IndexOf(first);
            if (end < 0) return null;
            var inner = rest[..end].ToString();
            if (inner.Length == 0) return null;
            var afterClose = rest[(end + 1)..];
            var suffixEnd = afterClose.IndexOfAny(' ', '\t');
            var suffix = suffixEnd >= 0 ? afterClose[..suffixEnd].ToString() : afterClose.ToString();
            return suffix.Length > 0 ? inner + suffix : inner;
        }

        var space = trimmed.AsSpan().IndexOfAny(' ', '\t');
        return space >= 0 ? trimmed[..space] : trimmed;
    }
}
