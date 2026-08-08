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
    //
    // Only .exe is stripped, and this must stay that way. This helper is shared by
    // IsEnv, the shell-wrapper normalizer, the PowerShell and builtin classifiers,
    // and IsLegacyQuarantinedHost below, so widening it silently changes what every
    // one of those recognizes. An earlier revision of this branch also stripped .com
    // so that a provenance-less entry naming `powershell.com` would be quarantined.
    // That was wrong on its own terms: the historical catalog this quarantine
    // reproduces (e4ff61e7) stripped .exe only and therefore never classified a .com
    // spelling at all, so quarantining one now would invent a denial that never
    // happened. Which images may be bound durably is a separate question, decided
    // solely by ExecReusableCommandBinder.IsBindableExecutable, which is .exe only.
    internal static string NormalizedBasename(string token)
    {
        var b = BasenameLower(token);
        return b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? b[..^4] : b;
    }

    internal static bool IsEnv(string token) =>
        NormalizedBasename(token) == "env";

    // NOTE: an earlier revision kept a catalog of interpreter and script-host
    // basenames here and refused durable approval for each. That control has been
    // removed rather than expanded. It failed in both directions: the list could
    // never enumerate every binary that can proxy execution, and a renamed copy
    // defeated a basename lookup entirely.
    //
    // Its job is now done structurally and unconditionally. Every rule this node
    // generates pins the command's arguments (see ExecArgPattern), so a rule for an
    // interpreter authorizes one script rather than the interpreter itself, and
    // wrapper invocations are classified by shape rather than by name
    // (ExecShellWrapperNormalizer, CanonicalCmdCarrier). Do not reintroduce a
    // name-based list as the security boundary.
    //
    // IsLegacyQuarantinedHost below is NOT that boundary and must not become it. It
    // applies to exactly one thing: an allowlist entry already on disk that carries no
    // provenance and no argument binding, written when this catalog was the rule. Such
    // an entry cannot be reasoned about, because we cannot tell a deliberate operator
    // rule from one this node generated under the old model. For an ordinary program
    // it keeps working. For a name this node once refused outright it goes inert and
    // prompts, so the previously denied case is not silently upgraded to allowed by
    // the change in model. New rules never reach this path; they always carry an
    // argument binding, which is the real control.
    //
    // The set below is therefore not a judgement about which programs are dangerous.
    // It is a verbatim copy of the catalog as it stood immediately before argument
    // binding replaced it (e4ff61e7), because the question it answers is purely
    // historical: would this exact entry have been refused durable approval when it
    // was written? Do not curate, prune, or extend this list. Anything added here
    // would claim to have been denied in a past release when it was not.
    private static readonly System.Collections.Generic.HashSet<string> s_legacyQuarantinedHosts =
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
            "mshta", "regsvr32", "rundll32",
            // Windows binaries that compile, load, or proxy execution of
            // argument-selected code.
            "msbuild", "csc", "vbc", "dnx", "rcsi",
            "installutil", "regasm", "regsvcs", "mavinject",
            "msiexec", "certutil", "bitsadmin", "wmic",
            "forfiles", "scriptrunner", "pcalua", "cmstp", "odbcconf",
            "msdt", "ieexec", "presentationhost", "winrs", "hh", "msxsl", "xwizard",
        };

    /// <summary>
    /// True when a token names a program that the previous model refused to approve
    /// durably. Read the note above before using this: it exists only to keep a
    /// provenance-less legacy allowlist entry from becoming more permissive than it was
    /// when it was written, and it is not a security boundary on its own.
    /// </summary>
    internal static bool IsLegacyQuarantinedHost(string token)
    {
        var basename = NormalizedBasename(token);
        return s_legacyQuarantinedHosts.Contains(basename)
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
