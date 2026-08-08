using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

// Path-based allowlist matcher, extended with the durable argument binding.
// Research doc 03 decisions:
//   - target = resolvedPath ?? rawExecutable
//   - * = single segment ([^/]*); ** = any segments (.*); ? = single char no separator ([^/])
//   - case-insensitive via RegexOptions (no ToLowerInvariant); \ → / normalization before matching
//   - basename-only patterns are invalid and fail-closed (no match produced)
//   - matchAll is strict all-or-nothing: any miss returns empty list
//
// An entry authorizes a candidate only when the path pattern matches AND the
// argument binding is satisfied. See ExecArgPattern for why the binding exists.
internal static class ExecAllowlistMatcher
{
    // Marks an entry this node generated from an Allow always decision, as opposed to
    // one a user wrote by hand. The distinction is load-bearing: see MatchInternal.
    internal const string AllowAlwaysSource = "allow-always";

    // Compiled regexes keyed by normalized pattern string.
    // Allowlist patterns are config-defined and bounded; unbounded cache growth is not a concern.
    private static readonly ConcurrentDictionary<string, Regex> s_regexCache = new();

    private static readonly string[] s_noArgs = [];

    // Returns the first entry whose pattern matches the resolution's target path, or null.
    // Target is normalized once before iterating — not per entry.
    internal static ExecAllowlistEntry? Match(
        IReadOnlyList<ExecAllowlistEntry> entries,
        ExecCommandResolution resolution)
        => MatchInternal(entries, resolution, argv: null);

    internal static ExecAllowlistEntry? Match(
        IReadOnlyList<ExecAllowlistEntry> entries,
        ExecCommandResolution resolution,
        IReadOnlyList<string>? argv)
        => MatchInternal(entries, resolution, argv);

    // Mirrors the shared matcher the gateway and the macOS node use, so one allowlist
    // file authorizes the same commands everywhere it is read.
    //
    // Two kinds of entry with no argPattern exist, and they are not equivalent:
    //   - A hand-written entry has no source. It is a deliberate path-only rule and
    //     authorizes the executable whatever its arguments, with one carve-out: if it
    //     resolves to a program the previous model refused to approve durably (an
    //     interpreter, shell, or script host), it goes inert and the command prompts.
    //     Without that, moving from a name catalog to argument binding would silently
    //     turn a case that used to be denied outright into one that is allowed with
    //     any arguments, which is a loosening nobody asked for. The entry is left on
    //     disk untouched and is not migrated; only an explicit Allow always writes an
    //     argument-bound sibling, and that sibling then matches normally.
    //   - A generated entry carries a source, normally "allow-always". Generated
    //     entries have bound their arguments since argument binding was introduced, so
    //     one that lacks an argPattern is an older record whose arguments were never
    //     pinned. Honoring it would let a rule approved for one command authorize any
    //     later command that reuses the same executable, so it is skipped instead.
    //     Any non-empty source is treated this way, not just the exact spelling this
    //     node writes. A source that is cased differently, padded, or otherwise
    //     unrecognized still means "a generator produced this entry", so falling
    //     through to the path-only branch would let a corrupted or foreign marker
    //     widen a rule that was never meant to be path-only. Provenance is only
    //     absent when the source is genuinely empty.
    //
    // A path-only match is only returned when no argument-bound entry matched, so a
    // precise rule always wins over a broad one.
    private static ExecAllowlistEntry? MatchInternal(
        IReadOnlyList<ExecAllowlistEntry> entries,
        ExecCommandResolution resolution,
        IReadOnlyList<string>? argv)
    {
        var target = NormalizeSeparators(resolution.ResolvedPath ?? resolution.RawExecutable);
        ExecAllowlistEntry? pathOnlyMatch = null;

        foreach (var entry in entries)
        {
            var pattern = entry.Pattern;
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var normalizedPattern = NormalizeSeparators(pattern);
            if (!IsValidNormalizedPattern(normalizedPattern)) continue;
            if (!s_regexCache.GetOrAdd(normalizedPattern, BuildPatternRegex).IsMatch(target))
                continue;

            if (string.IsNullOrEmpty(entry.ArgPattern))
            {
                // Fail closed on any provenance marker: an entry that records where it
                // came from is a generated entry, and a generated entry with no
                // argPattern lost its binding.
                if (!string.IsNullOrWhiteSpace(entry.Source))
                    continue;
                if (ExecCommandToken.IsLegacyQuarantinedHost(target))
                    continue;
                pathOnlyMatch ??= entry;
                continue;
            }

            if (ExecArgPattern.Matches(entry.ArgPattern, argv ?? s_noArgs))
                return entry;
        }

        return pathOnlyMatch;
    }

    // Returns one matching entry per resolution in input order.
    // Any resolution with no match causes the entire result to be empty (all-or-nothing).
    internal static IReadOnlyList<ExecAllowlistEntry> MatchAll(
        IReadOnlyList<ExecAllowlistEntry> entries,
        IReadOnlyList<ExecCommandResolution> resolutions)
        => MatchAll(entries, resolutions, reusableCommand: null);

    // The reusable command supplies the argv the argument binding is evaluated
    // against. It is the same object the resolutions were derived from, so the path
    // and argument sides of a rule are always evaluated against one identity.
    internal static IReadOnlyList<ExecAllowlistEntry> MatchAll(
        IReadOnlyList<ExecAllowlistEntry> entries,
        IReadOnlyList<ExecCommandResolution> resolutions,
        ExecReusableCommand? reusableCommand)
    {
        if (resolutions.Count == 0) return [];

        var argv = reusableCommand?.Argv;

        var result = new ExecAllowlistEntry[resolutions.Count];
        for (var i = 0; i < resolutions.Count; i++)
        {
            var match = MatchInternal(entries, resolutions[i], argv);
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

    // Inner check on an already-normalized pattern — single source of truth for the rule.
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
    // Called at most once per unique pattern — result is stored in s_regexCache by the caller.
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
                    // **/rest — rest must start at a segment boundary, not as a suffix of another name.
                    // (.*\/)? matches zero or more path segments including their trailing separator.
                    sb.Append(@"(.*\/)?");
                    i++;
                }
                else
                {
                    // trailing ** — match anything (no following segment to anchor)
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
