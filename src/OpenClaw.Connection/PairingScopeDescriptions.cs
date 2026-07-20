using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Connection;

/// <summary>
/// Maps gateway operator scope identifiers to short, human-readable descriptions so a person
/// approving a pairing request understands what access they are granting — mirroring the
/// friendly scope list shown by the macOS pairing prompt.
///
/// Returns canonical English fallbacks; the presentation layer may override any entry with a
/// localized resource keyed by scope before falling back to <see cref="Describe"/>.
/// </summary>
public static class PairingScopeDescriptions
{
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operator.admin"] = "Admin access",
            ["operator.read"] = "Read OpenClaw data",
            ["operator.write"] = "Send messages and make changes",
            ["operator.approvals"] = "Manage approvals",
            ["operator.pairing"] = "Pair and repair devices",
            ["operator.talk.secrets"] = "Use Talk credentials",
        };

    /// <summary>True when a friendly description exists for the scope; false for unknown scopes.</summary>
    public static bool IsKnown(string scope) =>
        !string.IsNullOrWhiteSpace(scope) && Map.ContainsKey(scope.Trim());

    /// <summary>
    /// Returns a friendly description for a scope, or the raw scope string (trimmed) when unknown
    /// so the approver still sees exactly what was requested rather than nothing.
    /// </summary>
    public static string Describe(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return string.Empty;
        var trimmed = scope.Trim();
        return Map.TryGetValue(trimmed, out var friendly) ? friendly : trimmed;
    }

    /// <summary>Describes a set of scopes, dropping blanks and de-duplicating while preserving order.</summary>
    public static IReadOnlyList<string> DescribeAll(IEnumerable<string>? scopes)
    {
        if (scopes == null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope)) continue;
            if (!seen.Add(scope.Trim())) continue;
            result.Add(Describe(scope));
        }
        return result;
    }
}
