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
        // Fail-closed: env with flags or VAR=val before a shell wrapper is not a transparent
        // dispatch. The allowlist cannot verify the effective command under an unknown env context.
        // Mirrors macOS hasEnvManipulationBeforeShellWrapper condition (research doc 04, table S).
        if (HasEnvManipulationBeforeShellWrapper(command)) return [];

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

    // True when first token is `env` with modifying options/assignments AND the effective
    // command after stripping the env layer is a shell wrapper.
    private static bool HasEnvManipulationBeforeShellWrapper(IReadOnlyList<string> command)
    {
        if (command.Count == 0) return false;
        if (!ExecCommandToken.IsEnv(command[0].Trim())) return false;
        if (!ExecEnvInvocationUnwrapper.HasModifiers(command)) return false;
        var unwrapped = ExecEnvInvocationUnwrapper.Unwrap(command);
        if (unwrapped is null || unwrapped.Count == 0) return false;
        return ExecShellWrapperNormalizer.Extract(unwrapped).IsWrapper;
    }

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
        var expanded = ExpandTilde(rawExecutable);
        var hasSep = expanded.Contains('/') || expanded.Contains('\\');

        string? resolvedPath;
        if (hasSep)
        {
            // Reject paths with ':' in non-volume-separator positions (ADS, non-standard forms).
            if (HasNonStandardColon(expanded)) return null;

            if (Path.IsPathFullyQualified(expanded))
            {
                resolvedPath = Path.GetFullPath(expanded);
            }
            else
            {
                var effectiveCwd = string.IsNullOrWhiteSpace(cwd)
                    ? Directory.GetCurrentDirectory()
                    : cwd.Trim();
                resolvedPath = Path.GetFullPath(expanded, effectiveCwd);
            }
        }
        else
        {
            resolvedPath = FindInPath(expanded, GetSearchPaths(env), GetPathExtensions(env));
        }

        var name = resolvedPath is not null
            ? Path.GetFileName(resolvedPath)
            : expanded;

        return new ExecCommandResolution(expanded, resolvedPath, name, cwd);
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
            var token = end >= 0 ? rest[..end].ToString() : rest.ToString();
            return token.Length == 0 ? null : token;
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
    // Covers: "-EncodedCommand", "-enc", "-ec", `"-enc"`, `-enc:payload`, `-encod`, etc.
    private static bool IsEncodedCommandFlag(string rawToken)
    {
        var t = rawToken;
        if (t.Length >= 2 && t[0] is '"' or '\'' && t[^1] == t[0])
            t = t[1..^1]; // strip matching outer quotes
        if (t.Length == 0 || t[0] != '-') return false;
        // Strip trailing :value or =value (e.g. -EncodedCommand:base64).
        var sep = t.AsSpan(1).IndexOfAny('=', ':');
        var flag = (sep >= 0 ? t[..(sep + 1)] : t).ToLowerInvariant();
        if (flag is "-ec" or "-enc" or "-encodedcommand") return true;
        // Any unambiguous prefix abbreviation of -encodedcommand longer than -enc.
        const string full = "-encodedcommand";
        return flag.Length > 4 && full.StartsWith(flag, StringComparison.Ordinal);
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
            if (File.Exists(candidate)) return TryNormalizePath(candidate);
            foreach (var ext in extensions)
            {
                var withExt = candidate + ext;
                if (File.Exists(withExt)) return TryNormalizePath(withExt);
            }
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
        // UNC paths (\\server\share) have no colon — fine.
        // Drive-letter prefix: exactly one ':' at index 1 (e.g. C:\...) — fine.
        // Anything else with ':' is rejected.
        var colonIdx = path.IndexOf(':');
        if (colonIdx < 0) return false;                    // no colon — fine
        if (colonIdx == 1) return path.IndexOf(':', 2) >= 0; // C:\ — fine iff no second colon
        return true;
    }

    // Attempt 8.3 → long path normalization for paths that exist on disk.
    // Only applied to resolved paths from PATH search (existence already confirmed).
    // Research doc 04 section canonicalization / 8.3 short names.
    private static string TryNormalizePath(string path)
    {
        // GetFullPath resolves . and .. but does not expand 8.3 short names.
        // A best-effort attempt: if the path contains ~ in a segment (8.3 indicator),
        // try to get the long-path form via the filesystem. We use Path.GetFullPath
        // which on .NET already normalizes separators and relative components.
        // Full GetLongPathName P/Invoke is left as OQ-R1 in the research docs.
        return Path.GetFullPath(path);
    }
}
