using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

// Resolved identity of a single executable token.
// Shape mirrors macOS ExecCommandResolution struct.
public readonly record struct ExecCommandResolution(
    string RawExecutable,
    string? ResolvedPath,
    string ExecutableName,
    string? Cwd);

// The three resolution functions required by the pipeline.
// resolve()               → singular, for state machine
// ResolveForAllowlist()   → multi-segment, fail-closed, for allowlist matching
// ResolveAllowAlwaysPatterns() → UX suggestions for prompt
internal static class ExecCommandResolver
{
    // Windows executable extensions, tried in order for basename search.
    private static readonly string[] s_extensions = [".exe", ".cmd", ".bat", ".com"];

    // ── Public API ───────────────────────────────────────────────────────────

    // Singular resolution of the primary executable for the state machine.
    // Returns null if the command is empty or resolution is impossible.
    // Unwraps transparent env prefixes (no modifiers).
    internal static ExecCommandResolution? Resolve(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        var effective = ExecEnvInvocationUnwrapper.UnwrapForResolution(command);
        if (effective.Count == 0) return null;
        var raw = effective[0].Trim();
        return raw.Length == 0 ? null : ResolveExecutable(raw, cwd, env);
    }

    // Multi-segment resolution for allowlist matching.
    // Detects shell wrappers; splits payload chain; resolves one executable per segment.
    // Returns empty list (fail-closed) on any ambiguity, command substitution, or env manipulation.
    internal static IReadOnlyList<ExecCommandResolution> ResolveForAllowlist(
        IReadOnlyList<string> command,
        string? evaluationRawCommand,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        // Fail-closed: any env invocation with modifiers (flags or VAR=val assignments).
        // The allowlist cannot verify which executable will actually run under a modified env —
        // the resolver uses the original env while execution uses the modified one.
        // Subsumes the previous shell-wrapper-only check (Hanselman review finding #2).
        if (command.Count > 0
            && ExecCommandToken.IsEnv(command[0].Trim())
            && ExecEnvInvocationUnwrapper.HasModifiers(command))
            return [];

        var wrapper = ExecShellWrapperNormalizer.Extract(command);
        if (wrapper.IsWrapper)
        {
            if (wrapper.InlineCommand is null) return [];
            var segments = SplitShellCommandChain(wrapper.InlineCommand);
            if (segments is null) return [];

            var resolutions = new List<ExecCommandResolution>(segments.Count);
            foreach (var segment in segments)
            {
                var token = ParseFirstToken(segment);
                if (token is null) return [];
                // -EncodedCommand and aliases in segment position: fail-closed (research doc 04 S1).
                if (SegmentUsesEncodedCommand(segment, token)) return [];
                var res = ResolveExecutable(token, cwd, env);
                if (res is null) return [];
                resolutions.Add(res.Value);
            }
            return resolutions;
        }

        // Direct exec: fail-closed if powershell/pwsh invoked directly with -EncodedCommand.
        // Covers top-level `["powershell", "-enc", ...]` and transparent `["env", "pwsh", "-enc", ...]`.
        if (DirectExecUsesEncodedCommand(command)) return [];

        var single = ResolveSingle(command, evaluationRawCommand, cwd, env);
        return single is null ? [] : [single.Value];
    }

    // UX suggestions of allowlist patterns for prompting.
    // Unlike ResolveForAllowlist, this unwraps env with modifiers to surface the real executable.
    internal static IReadOnlyList<string> ResolveAllowAlwaysPatterns(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new List<string>();
        CollectPatterns(command, cwd, env, seen, patterns, 0);
        return patterns;
    }

    // ── Resolution helpers ───────────────────────────────────────────────────

    private static ExecCommandResolution? ResolveSingle(
        IReadOnlyList<string> command,
        string? rawCommand,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        // Prefer first token of evaluationRawCommand when present.
        if (!string.IsNullOrWhiteSpace(rawCommand))
        {
            var token = ParseFirstToken(rawCommand);
            if (token is not null) return ResolveExecutable(token, cwd, env);
        }
        return Resolve(command, cwd, env);
    }

    private static ExecCommandResolution? ResolveExecutable(
        string rawExecutable,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        try
        {
            var expanded = ExpandTilde(rawExecutable);
            var hasSep = expanded.Contains('/') || expanded.Contains('\\');

            string? resolvedPath;
            if (hasSep)
            {
                // Reject paths with ':' in non-volume-separator positions (ADS, non-standard forms).
                if (HasNonStandardColon(expanded)) return null;

                resolvedPath = Path.IsPathFullyQualified(expanded)
                    ? Path.GetFullPath(expanded)
                    : Path.GetFullPath(expanded, string.IsNullOrWhiteSpace(cwd)
                        ? Directory.GetCurrentDirectory()
                        : cwd.Trim());
            }
            else
            {
                resolvedPath = FindInPath(expanded, GetSearchPaths(env), GetPathExtensions(env));
            }

            var name = resolvedPath is not null ? Path.GetFileName(resolvedPath) : expanded;
            return new ExecCommandResolution(expanded, resolvedPath, name, cwd);
        }
        catch { return null; } // fail-closed; intentionally broad — add diagnostic tracing here if needed
    }

    // ── Shell command chain splitting ────────────────────────────────────────

    // Splits a shell command string on ;, &&, ||, |, &, \n.
    // Returns null (fail-closed) on command/process substitution: $(...), `...`, <(...), >(...).
    // Returns null on unclosed quotes or unresolved escapes.
    private static IReadOnlyList<string>? SplitShellCommandChain(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;

        var segments = new List<string>();
        var current = new StringBuilder();
        bool inSingle = false, inDouble = false, escaped = false;
        var chars = trimmed.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            char? next = i + 1 < chars.Length ? chars[i + 1] : null;

            if (escaped) { current.Append(ch); escaped = false; continue; }
            if (ch == '\\' && !inSingle) { current.Append(ch); escaped = true; continue; }
            if (ch == '\'' && !inDouble) { inSingle = !inSingle; current.Append(ch); continue; }
            if (ch == '"' && !inSingle) { inDouble = !inDouble; current.Append(ch); continue; }

            // Fail-closed on command/process substitution.
            if (!inSingle && IsCommandSubstitution(ch, next, inDouble)) return null;

            if (!inSingle && !inDouble)
            {
                var step = DelimiterStep(ch, i > 0 ? chars[i - 1] : (char?)null, next);
                if (step.HasValue)
                {
                    var seg = current.ToString().Trim();
                    if (seg.Length == 0) return null;
                    segments.Add(seg);
                    current.Clear();
                    i += step.Value - 1;
                    continue;
                }
            }

            current.Append(ch);
        }

        if (escaped || inSingle || inDouble) return null;

        var last = current.ToString().Trim();
        if (last.Length == 0) return null;
        segments.Add(last);
        return segments;
    }

    private static bool IsCommandSubstitution(char ch, char? next, bool inDouble)
    {
        if (inDouble) return ch == '`' || (ch == '$' && next == '(');
        return ch == '`' ||
               (ch == '$' && next == '(') ||
               (ch == '<' && next == '(') ||
               (ch == '>' && next == '(');
    }

    private static int? DelimiterStep(char ch, char? prev, char? next)
    {
        if (ch == ';' || ch == '\n') return 1;
        if (ch == '&')
        {
            if (next == '&') return 2;
            return (prev == '>' || next == '>') ? null : (int?)1;
        }
        if (ch == '|')
        {
            if (next == '|' || next == '&') return 2;
            return 1;
        }
        return null;
    }

    // Extracts the first shell-tokenized word from a command string.
    private static string? ParseFirstToken(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        var first = trimmed[0];
        if (first == '"' || first == '\'')
        {
            var rest = trimmed.AsSpan(1);
            var end = rest.IndexOf(first);
            if (end < 0) return null; // unclosed quote — fail-closed; do not guess the token
            var inner = rest[..end].ToString();
            if (inner.Length == 0) return null;
            // Preserve any suffix after the closing quote up to the next whitespace.
            // Handles `"git".exe` → "git.exe" and `"C:\Program Files\Git\bin\git".exe` → *.exe.
            var afterClose = rest[(end + 1)..];
            var suffixEnd = afterClose.IndexOfAny(' ', '\t');
            var suffix = suffixEnd >= 0 ? afterClose[..suffixEnd].ToString() : afterClose.ToString();
            return suffix.Length > 0 ? inner + suffix : inner;
        }
        var space = trimmed.AsSpan().IndexOfAny(' ', '\t');
        return space >= 0 ? trimmed[..space] : trimmed;
    }

    // ── allowAlwaysPatterns collection ───────────────────────────────────────

    private static void CollectPatterns(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        HashSet<string> seen,
        List<string> patterns,
        int depth)
    {
        if (depth >= 3 || command.Count == 0) return;

        var wrapper = ExecShellWrapperNormalizer.Extract(command);
        if (wrapper.IsWrapper && wrapper.InlineCommand is not null)
        {
            var segments = SplitShellCommandChain(wrapper.InlineCommand);
            if (segments is null) return;
            foreach (var seg in segments)
            {
                // allowAlwaysPatterns does NOT fail-closed on -EncodedCommand: it's UX only.
                var token = ParseFirstToken(seg);
                if (token is null) continue;
                var res = ResolveExecutable(token, cwd, env);
                if (res is null) continue;
                var pattern = res.Value.ResolvedPath ?? res.Value.RawExecutable;
                if (seen.Add(pattern)) patterns.Add(pattern);
            }
            return;
        }

        // For direct exec, unwrap env including with-modifier cases for pattern discovery.
        var effective = ExecEnvInvocationUnwrapper.UnwrapForResolution(command);
        if (effective.Count == 0) return;
        var rawToken = effective[0].Trim();
        if (rawToken.Length == 0) return;
        var resolution = ResolveExecutable(rawToken, cwd, env);
        if (resolution is null) return;
        var pat = resolution.Value.ResolvedPath ?? resolution.Value.RawExecutable;
        if (seen.Add(pat)) patterns.Add(pat);
    }

    // ── -EncodedCommand detection ─────────────────────────────────────────────

    // Research doc 04 S1: if a chain segment invokes PowerShell with -EncodedCommand (or any
    // alias / unambiguous prefix abbreviation), the payload is opaque base64 — fail-closed.
    // Only triggers when the first token IS a PowerShell binary AND the segment contains the flag.
    // `powershell -c 'Get-Date'` (no -enc) must NOT be fail-closed.
    private static bool SegmentUsesEncodedCommand(string segment, string firstToken)
    {
        var b = ExecCommandToken.NormalizedBasename(firstToken);
        if (b is not ("powershell" or "pwsh")) return false;

        var rest = segment.AsSpan();
        while (rest.Length > 0)
        {
            var i = 0;
            while (i < rest.Length && char.IsWhiteSpace(rest[i])) i++;
            rest = rest[i..];
            if (rest.Length == 0) break;

            // Extract next token — quoted strings count as one unit so `"-enc"` is detected.
            int end;
            if (rest[0] is '"' or '\'')
            {
                var q = rest[0];
                end = 1;
                while (end < rest.Length && rest[end] != q) end++;
                if (end < rest.Length) end++; // include closing quote
            }
            else
            {
                end = 0;
                while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
            }

            var token = rest[..end].ToString();
            rest = rest[end..];

            if (IsEncodedCommandFlag(token)) return true;
            if (token == "--") break;
        }
        return false;
    }

    // Returns true when a raw flag token (possibly quoted, possibly with colon/equals value suffix)
    // represents -EncodedCommand or any of its unambiguous prefix abbreviations.
    // Covers: "-EncodedCommand", "-enc", "-ec", "-e", `"-enc"`, `-enc:payload`, `-encod`, etc.
    private static bool IsEncodedCommandFlag(string rawToken)
    {
        var t = rawToken;
        if (t.Length >= 2 && t[0] is '"' or '\'' && t[^1] == t[0])
            t = t[1..^1]; // strip matching outer quotes
        if (t.Length == 0 || t[0] != '-') return false;
        // Strip trailing :value or =value (e.g. -EncodedCommand:base64).
        var sep = t.AsSpan(1).IndexOfAny('=', ':');
        var flag = (sep >= 0 ? t[..(sep + 1)] : t).ToLowerInvariant();
        // -e is accepted by Windows PowerShell as a short alias for -EncodedCommand.
        if (flag is "-e" or "-ec" or "-enc" or "-encodedcommand") return true;
        // Any unambiguous prefix abbreviation of -encodedcommand beginning at -en.
        const string full = "-encodedcommand";
        return flag.Length >= 3 && full.StartsWith(flag, StringComparison.Ordinal);
    }

    // True when direct exec (no shell wrapper) is a PowerShell invocation with -EncodedCommand.
    // Unwraps transparent env prefixes so `["env", "pwsh", "-enc", ...]` is also caught.
    private static bool DirectExecUsesEncodedCommand(IReadOnlyList<string> command)
    {
        var effective = ExecEnvInvocationUnwrapper.UnwrapForResolution(command);
        if (effective.Count < 2) return false;
        var b = ExecCommandToken.NormalizedBasename(effective[0].Trim());
        if (b is not ("powershell" or "pwsh")) return false;
        for (var i = 1; i < effective.Count; i++)
        {
            var t = effective[i].Trim();
            if (t == "--") break;
            if (IsEncodedCommandFlag(t)) return true;
        }
        return false;
    }

    // ── PATH search ───────────────────────────────────────────────────────────

    private static string? GetEnvValueIgnoreCase(IReadOnlyDictionary<string, string>? env, string key)
    {
        if (env is null) return null;
        foreach (var kvp in env)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    private static string? FindInPath(
        string name,
        IReadOnlyList<string> searchPaths,
        IReadOnlyList<string> extensions)
    {
        foreach (var dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, name);
            // PATHEXT extensions first — matches Windows CreateProcess resolution order.
            // A no-extension shadow in PATH must not shadow a PATHEXT binary of the same stem.
            // Note: PATHEXT is probed even when `name` already carries an extension (git.exe →
            // tries git.exe.exe, git.exe.cmd, …). This matches CreateProcess behavior — the extra
            // File.Exists calls are harmless and avoiding them would require extension detection here.
            foreach (var ext in extensions)
            {
                var withExt = candidate + ext;
                if (File.Exists(withExt)) return TryNormalizePath(withExt);
            }
            // Bare name as final fallback (covers names that already have an explicit extension).
            if (File.Exists(candidate)) return TryNormalizePath(candidate);
        }
        return null;
    }

    private static IReadOnlyList<string> GetSearchPaths(IReadOnlyDictionary<string, string>? env)
    {
        var rawPath = GetEnvValueIgnoreCase(env, "PATH");
        if (!string.IsNullOrEmpty(rawPath))
        {
            var parts = rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts;
        }
        // Fallback to process PATH.
        var processPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(processPath))
        {
            var parts = processPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts;
        }
        return WellKnownPaths();
    }

    private static IReadOnlyList<string> GetPathExtensions(IReadOnlyDictionary<string, string>? env)
    {
        var rawPathExt = GetEnvValueIgnoreCase(env, "PATHEXT");
        if (!string.IsNullOrEmpty(rawPathExt))
        {
            var parts = rawPathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts;
        }
        var processPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (!string.IsNullOrEmpty(processPathExt))
        {
            var parts = processPathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts;
        }
        return s_extensions;
    }

    private static IReadOnlyList<string> WellKnownPaths()
    {
        var sys32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            sys32,
            sys,
            Path.Combine(sys32, "OpenSSH"),
            Path.Combine(pf, "Git", "usr", "bin"),
            Path.Combine(pf, "Git", "bin"),
        ];
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    private static string ExpandTilde(string path)
    {
        if (!path.StartsWith('~')) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : home + path[1..];
    }

    // Paths with ':' outside the volume-separator position are rejected (ADS, non-standard forms).
    // Research doc 04 section 3 / S3.
    private static bool HasNonStandardColon(string path)
    {
        // Extended-length prefix — strip it and evaluate the remainder (\\?\C:\ is valid).
        var effective = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

        // UNC paths (\\server\share) and extended UNC (\\?\UNC\...) have no drive colon — fine.
        if (effective.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        var colonIdx = effective.IndexOf(':');
        if (colonIdx < 0) return false; // no colon — fine
        // Drive-letter form: single ASCII letter at index 0 followed by ':' — fine if no second colon.
        // '1', '!' etc. at index 0 are not valid drive letters and must be rejected.
        if (colonIdx == 1 && char.IsAsciiLetter(effective[0]))
            return effective.IndexOf(':', 2) >= 0;
        return true;
    }

    // Attempt 8.3 → long path normalization for paths that exist on disk.
    // Only applied to resolved paths from PATH search (existence already confirmed).
    // Research doc 04 section canonicalization / 8.3 short names.
    private static string TryNormalizePath(string path)
    {
        // GetFullPath resolves . and .. but does not expand 8.3 short names.
        // Full GetLongPathName P/Invoke is left as OQ-R1 in the research docs.
        try { return Path.GetFullPath(path); }
        catch { return path; } // hostile path must not throw out of resolution
    }
}
