using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Forces an explicit approval for commands that change the security-audit suppression list, so
/// an agent cannot silently disable security findings via <c>system.run</c>. Mirrors the macOS
/// <c>commandRequiresSecurityAuditSuppressionApproval</c> gate: a command that references
/// <c>security.audit.suppressions</c> (exact or obfuscated) requires approval unless it is a
/// read-only inspection (<c>openclaw config get|schema|validate</c>).
/// </summary>
internal static class ExecApprovalAuditSuppressionGate
{
    // Obfuscated forms: quotes/separators spliced between the three segments (bounded to avoid
    // catastrophic backtracking). Matches the macOS fuzzy detector.
    private static readonly Regex FuzzyReference = new(
        "[\"']?security[\"']?[\\s\\S]{0,200}[\"']?audit[\"']?[\\s\\S]{0,200}[\"']?suppressions[\"']?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool RequiresExtraApproval(IReadOnlyList<string>? argv, string? displayCommand)
    {
        var normalized = Normalize(argv, displayCommand);
        if (normalized.Length == 0)
            return false;

        var references =
            normalized.Contains("security.audit.suppressions", StringComparison.OrdinalIgnoreCase)
            || FuzzyReference.IsMatch(normalized);
        if (!references)
            return false;

        // Read-only inspections are exempt (direct-argv forms). Shell-wrapped reads fall through
        // to requiring approval, which is safe: it only ever prompts more, never less.
        return !IsReadOnlyInspection(argv);
    }

    private static string Normalize(IReadOnlyList<string>? argv, string? displayCommand)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(displayCommand))
            sb.Append(displayCommand).Append(' ');
        if (argv is not null)
        {
            foreach (var token in argv)
                sb.Append(token).Append(' ');
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> FlagGlobalOptions =
        new(StringComparer.OrdinalIgnoreCase) { "--dev", "--no-color" };

    private static readonly HashSet<string> ValueGlobalOptions =
        new(StringComparer.OrdinalIgnoreCase) { "--profile", "--container", "--log-level" };

    private static bool IsReadOnlyInspection(IReadOnlyList<string>? argv)
    {
        if (argv is null || argv.Count == 0)
            return false;

        var i = 0;
        if (Basename(argv[i]) == "pnpm")
            i++;
        if (argv.Count <= i || Basename(argv[i]) != "openclaw")
            return false;
        i++;

        i = SkipGlobalOptions(argv, i);
        if (argv.Count <= i || !argv[i].Equals("config", StringComparison.OrdinalIgnoreCase))
            return false;
        i++;

        i = SkipGlobalOptions(argv, i);
        if (argv.Count <= i)
            return false;

        return argv[i].ToLowerInvariant() is "get" or "schema" or "validate";
    }

    // Skips known global CLI options (and their values) so a read-only inspection is still
    // recognized when flags precede the subcommand. Stops at the first non-option token or an
    // unknown option, which then fails the verb check (safe: it only ever prompts more).
    private static int SkipGlobalOptions(IReadOnlyList<string> argv, int i)
    {
        while (i < argv.Count)
        {
            var token = argv[i];
            if (FlagGlobalOptions.Contains(token))
            {
                i++;
                continue;
            }
            if (ValueGlobalOptions.Contains(token))
            {
                i += 2; // option plus its value
                continue;
            }
            // "--option=value" form carries its own value.
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Contains('='))
            {
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    private static string Basename(string token)
    {
        var normalized = token.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }
        return name.ToLowerInvariant();
    }
}
