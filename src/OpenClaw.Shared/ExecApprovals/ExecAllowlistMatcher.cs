using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

// Path-based allowlist matcher.
// Research doc 03 decisions:
//   - target = resolvedPath ?? rawExecutable
//   - * = single segment ([^/]*); ** = any segments (.*); ? = single char no separator ([^/])
//   - case-insensitive via RegexOptions (no ToLowerInvariant); \ → / normalization before matching
//   - basename-only patterns are invalid and fail-closed (no match produced)
//   - matchAll is strict all-or-nothing: any miss returns empty list
internal static class ExecAllowlistMatcher
{
    // Compiled regexes keyed by normalized pattern string.
    // Allowlist patterns are config-defined and bounded; unbounded cache growth is not a concern.
    private static readonly ConcurrentDictionary<string, Regex> s_regexCache = new();

    // Returns the first entry whose pattern matches the resolution's target path, or null.
    // Target is normalized once before iterating - not per entry.
    internal static ExecAllowlistEntry? Match(
        IReadOnlyList<ExecAllowlistEntry> entries,
        ExecCommandResolution resolution)
    {
        var target = NormalizeSeparators(resolution.ResolvedPath ?? resolution.RawExecutable);
        foreach (var entry in entries)
        {
            var pattern = entry.Pattern;
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var normalizedPattern = NormalizeSeparators(pattern);
            if (!IsValidNormalizedPattern(normalizedPattern)) continue;
            if (s_regexCache.GetOrAdd(normalizedPattern, BuildPatternRegex).IsMatch(target))
                return entry;
        }
        return null;
    }

    // Returns one matching entry per resolution in input order.
    // Any resolution with no match causes the entire result to be empty (all-or-nothing).
    internal static IReadOnlyList<ExecAllowlistEntry> MatchAll(
        IReadOnlyList<ExecAllowlistEntry> entries,
        IReadOnlyList<ExecCommandResolution> resolutions)
    {
        if (resolutions.Count == 0) return [];

        var result = new ExecAllowlistEntry[resolutions.Count];
        for (var i = 0; i < resolutions.Count; i++)
        {
            var match = Match(entries, resolutions[i]);
            if (match is null) return [];
            result[i] = match;
        }
        return result;
    }

    // A pattern is valid iff it contains a path separator after normalization.
    // Basename-only patterns (e.g. "rg", "echo") are invalid.
    internal static bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        return IsValidNormalizedPattern(NormalizeSeparators(pattern));
    }

    // Inner check on an already-normalized pattern - single source of truth for the rule.
    private static bool IsValidNormalizedPattern(string normalizedPattern)
        => normalizedPattern.Contains('/') && !HasMalformedDoubleStars(normalizedPattern);

    // ** is valid only at segment boundaries: preceded by start-of-string or '/', followed by '/' or end.
    // e.g. "C:/tools**" and "**suffix" are malformed and must fail-closed.
    private static bool HasMalformedDoubleStars(string normalizedPattern)
    {
        for (var i = 0; i < normalizedPattern.Length - 1; i++)
        {
            if (normalizedPattern[i] != '*' || normalizedPattern[i + 1] != '*') continue;
            var precededByBoundary = i == 0 || normalizedPattern[i - 1] == '/';
            var followedByBoundary = i + 2 >= normalizedPattern.Length || normalizedPattern[i + 2] == '/';
            if (!precededByBoundary || !followedByBoundary) return true;
            i++; // skip second *
        }
        return false;
    }

    // Normalizes path separators only.
    // Case insensitivity is delegated to the regex engine (IgnoreCase | CultureInvariant)
    // so no ToLowerInvariant() allocation is needed here.
    // Safe to apply to paths that are already forward-slash normalized (idempotent).
    private static string NormalizeSeparators(string? value)
        => (value ?? string.Empty).Replace('\\', '/');

    // Converts a separator-normalized glob pattern to an anchored compiled regex.
    // Called at most once per unique pattern - result is stored in s_regexCache by the caller.
    // NonBacktracking prevents catastrophic behavior on adversarial or degenerate patterns.
    private static Regex BuildPatternRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            if (i + 1 < pattern.Length && pattern[i] == '*' && pattern[i + 1] == '*')
            {
                i += 2;
                if (i < pattern.Length && pattern[i] == '/' && i + 1 < pattern.Length)
                {
                    // **/rest - rest must start at a segment boundary, not as a suffix of another name.
                    // (.*\/)? matches zero or more path segments including their trailing separator.
                    sb.Append(@"(.*\/)?");
                    i++;
                }
                else
                {
                    // trailing ** - match anything (no following segment to anchor)
                    sb.Append(".*");
                }
            }
            else if (pattern[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (pattern[i] == '?')
            {
                // Research doc 03 security decision: ? must not cross separators on Windows.
                sb.Append("[^/]");
                i++;
            }
            else
            {
                // Collect consecutive literal characters (including /) and escape as one span
                // to avoid one string allocation per character.
                var literalStart = i;
                while (i < pattern.Length && pattern[i] != '*' && pattern[i] != '?')
                    i++;
                sb.Append(Regex.Escape(pattern[literalStart..i]));
            }
        }
        sb.Append('$');
        return new Regex(
            sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
