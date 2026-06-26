using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Pages;

internal sealed class ExecPolicyRule
{
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "deny";
    public int Index { get; set; }
    public string[]? Shells { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}

internal static class ExecPolicyRuleList
{
    public static string NormalizeAction(string? action)
    {
        if (string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase))
            return "allow";
        if (string.Equals(action, "prompt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "ask", StringComparison.OrdinalIgnoreCase))
            return "prompt";
        return "deny";
    }

    public static string NormalizeAction(JsonElement action)
    {
        if (action.ValueKind == JsonValueKind.String)
            return NormalizeAction(action.GetString());

        if (action.ValueKind == JsonValueKind.Number && action.TryGetInt32(out var numeric))
        {
            return numeric switch
            {
                0 => "allow",
                1 => "deny",
                2 => "prompt",
                _ => "deny"
            };
        }

        return "deny";
    }

    public static string? TryGetActionCaseInsensitive(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop))
                return NormalizeAction(prop);
        }

        return null;
    }

    public static bool? PersistedEnabled(bool enabled) => enabled ? null : false;

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

    private static bool PatternEquals(string currentPattern, string newPattern) =>
        string.Equals(currentPattern.Trim(), newPattern.Trim(), StringComparison.OrdinalIgnoreCase);
}
