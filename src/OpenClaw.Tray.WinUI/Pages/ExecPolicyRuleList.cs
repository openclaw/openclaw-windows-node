using System;
using System.Collections.Generic;

namespace OpenClawTray.Pages;

internal sealed class ExecPolicyRule
{
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "deny";
    public int Index { get; set; }
}

internal static class ExecPolicyRuleList
{
    public static void UpsertByPattern(IList<ExecPolicyRule> rules, string pattern, string action)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var normalizedPattern = pattern.Trim();
        if (normalizedPattern.Length == 0)
            return;

        var firstMatch = -1;
        for (var i = 0; i < rules.Count; i++)
        {
            if (PatternEquals(rules[i].Pattern, normalizedPattern))
            {
                firstMatch = i;
                break;
            }
        }

        if (firstMatch < 0)
        {
            rules.Add(new ExecPolicyRule { Pattern = normalizedPattern, Action = action });
            return;
        }

        rules[firstMatch].Pattern = normalizedPattern;
        rules[firstMatch].Action = action;

        for (var i = rules.Count - 1; i > firstMatch; i--)
        {
            if (PatternEquals(rules[i].Pattern, normalizedPattern))
                rules.RemoveAt(i);
        }
    }

    public static bool CoalesceDuplicatePatterns(IList<ExecPolicyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var changed = false;
        for (var i = 0; i < rules.Count; i++)
        {
            ExecPolicyRule? lastDuplicate = null;
            for (var j = i + 1; j < rules.Count; j++)
            {
                if (PatternEquals(rules[i].Pattern, rules[j].Pattern))
                    lastDuplicate = rules[j];
            }

            if (lastDuplicate is null)
                continue;

            // Loading an existing file must not relax or tighten policy just
            // because duplicate patterns exist; exec policy is first-match-wins.
            rules[i].Pattern = rules[i].Pattern.Trim();

            for (var j = rules.Count - 1; j > i; j--)
            {
                if (PatternEquals(rules[i].Pattern, rules[j].Pattern))
                    rules.RemoveAt(j);
            }

            changed = true;
        }

        return changed;
    }

    private static bool PatternEquals(string currentPattern, string newPattern) =>
        string.Equals(currentPattern.Trim(), newPattern.Trim(), StringComparison.OrdinalIgnoreCase);
}
