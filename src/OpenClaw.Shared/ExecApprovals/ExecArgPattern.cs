using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Single owner of the durable argument binding: how a command's arguments are
/// reduced to the subject a rule is evaluated against, how the stored argPattern is
/// written, and how a stored argPattern is matched back against a candidate command.
///
/// This is the control that replaces a maintained catalog of interpreter basenames.
/// A durable rule for a shell, interpreter, or script host is only sound when it also
/// pins the arguments, because those executables take their meaning from an
/// argument-selected script, expression, or assembly. Binding the arguments makes a
/// durable rule describe one operation rather than one program, which is why a
/// generated rule always carries an argPattern instead of relying on a list of names
/// that can never be complete.
///
/// Wire compatibility is the reason for the exact shapes below. Allowlist files are
/// read back by the gateway and shared with the macOS node, so a pattern written here
/// has to mean the same thing there. Two forms exist:
///
///   NUL-separated regex (written by Windows): "^" + escaped(args joined by NUL) + NUL + "$",
///     or "^\0\0$" for a command with no arguments. The subject is the arguments
///     joined by NUL with a trailing NUL, so an argument that itself contains a space
///     cannot be confused with an argument boundary.
///   Hashed form (written by macOS): "sha256:argv:" + hex digest over a
///     length-prefixed rendering of the arguments. Compared by exact equality.
///
/// Both are recognized here so a rule written on either platform evaluates
/// identically on this one.
/// </summary>
internal static class ExecArgPattern
{
    internal const string HashedArgPatternPrefix = "sha256:argv:";

    private const char Nul = '\0';

    // A stored pattern is remote-influenced input, so matching is bounded in time and
    // a malformed pattern fails closed rather than throwing into the approval path.
    private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Regex?> s_regexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The subject a NUL-separated pattern is evaluated against: every element after
    /// the executable joined by NUL, with a trailing NUL. A command with no arguments
    /// renders as a pair of NULs so it stays distinguishable from a single empty
    /// argument.
    /// </summary>
    internal static string BuildArgSubject(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count <= 1)
            return "\0\0";

        var builder = new StringBuilder();
        for (var i = 1; i < argv.Count; i++)
        {
            builder.Append(argv[i]);
            builder.Append(Nul);
        }
        return builder.ToString();
    }

    /// <summary>
    /// The subject a space-separated pattern is evaluated against. Retained so a
    /// hand-written rule that uses a plain regex keeps working.
    /// </summary>
    internal static string BuildSpaceJoinedArgSubject(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count <= 1)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 1; i < argv.Count; i++)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(argv[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Builds the durable argPattern stored for a newly approved command: a fully
    /// escaped, anchored regex over the NUL-separated argument subject.
    ///
    /// Escaping keeps a regex metacharacter that appears literally in an approved
    /// argument from being reinterpreted as a wildcard. The anchors keep the rule from
    /// matching a longer command that merely starts with the approved one, because the
    /// matcher on the other side of the wire tests without anchoring.
    ///
    /// Separators are normalized to backslashes before escaping so a rule approved as
    /// "dir/script.py" also authorizes the same command spelled "dir\script.py". The
    /// matcher performs the mirror-image retry.
    /// </summary>
    internal static string BuildArgPattern(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count <= 1)
            return "^\0\0$";

        var builder = new StringBuilder("^");
        for (var i = 1; i < argv.Count; i++)
        {
            builder.Append(Regex.Escape(argv[i].Replace('/', '\\')));
            // The separator is appended unescaped so the stored pattern carries a real
            // NUL. That is what tells every matcher to use the NUL-separated subject.
            builder.Append(Nul);
        }
        builder.Append('$');
        return builder.ToString();
    }

    /// <summary>
    /// The hashed form written by the macOS node, reproduced so an entry created there
    /// can be matched here. The digest covers a length-prefixed rendering, so no
    /// combination of argument contents can collide with a different argument list.
    /// </summary>
    internal static string BuildHashedArgPattern(IReadOnlyList<string> argv)
    {
        var count = argv is null ? 0 : Math.Max(0, argv.Count - 1);
        var builder = new StringBuilder();
        builder.Append(count.ToString(CultureInfo.InvariantCulture));
        builder.Append(Nul);
        for (var i = 1; i <= count; i++)
        {
            var arg = argv![i];
            builder.Append(Encoding.UTF8.GetByteCount(arg).ToString(CultureInfo.InvariantCulture));
            builder.Append(Nul);
            builder.Append(arg);
            builder.Append(Nul);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return HashedArgPatternPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static bool IsHashedArgPattern(string? value)
        => value is not null && value.StartsWith(HashedArgPatternPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Matches a stored argPattern against a candidate argv.
    ///
    /// An absent pattern imposes no argument constraint. Whether an entry is permitted
    /// to have no constraint is the caller's decision, not this one.
    /// </summary>
    internal static bool Matches(string? argPattern, IReadOnlyList<string> argv)
    {
        if (argPattern is null)
            return true;

        if (IsHashedArgPattern(argPattern))
            return string.Equals(argPattern, BuildHashedArgPattern(argv), StringComparison.Ordinal);

        var regex = s_regexCache.GetOrAdd(argPattern, TryCompile);
        if (regex is null)
            return false;  // malformed pattern: fail closed

        // The presence of a NUL in the pattern selects the subject rendering, exactly
        // as it does on the other side of the wire.
        var subject = argPattern.IndexOf(Nul) >= 0
            ? BuildArgSubject(argv)
            : BuildSpaceJoinedArgSubject(argv);

        try
        {
            if (regex.IsMatch(subject))
                return true;

            // Retry with separators normalized, mirroring the normalization applied when
            // the pattern was written, so either spelling of a path matches.
            var normalized = subject.Replace('/', '\\');
            return !string.Equals(normalized, subject, StringComparison.Ordinal) && regex.IsMatch(normalized);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex? TryCompile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, s_matchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
