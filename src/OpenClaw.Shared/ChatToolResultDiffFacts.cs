// Bounded line-count facts from a tool result's `details` object, shared by live events and history.
// Usage: var facts = ChatToolResultDiffFacts.Read(resultOrMessage); // reads its "details" object
//        if (facts?.HasDiff == true) show facts.Added/facts.Removed (null when the diff was truncated).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// Facts from a successful result's <c>details</c>; present only when the result carried details.
/// <see cref="Added"/> and <see cref="Removed"/> are exact only for a complete <c>details.diff</c>;
/// <see cref="DiffTruncated"/> marks an authoritative diff whose counts are unknown.
/// </summary>
public sealed record ChatToolResultDiffFacts(
    long? Added = null, long? Removed = null, bool DiffTruncated = false, bool? Created = null, bool? Changed = null)
{
    // Matches the web chat's MAX_DIFF_RENDER_LINES, so both clients show the same counts or none.
    private const int MaxDiffRenderLines = 400;
    private static readonly Regex DetailsLine = new(@"^([+\- ])\s*(\d+) ?(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex DetailsTruncated = new(@"^\s*\.\.\.\(truncated\)\.\.\.\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex DetailsSkip = new(@"^\s*\.\.\.\s*$", RegexOptions.CultureInvariant);

    /// <summary>True when <c>details.diff</c> was present: it is then authoritative, even without counts.</summary>
    public bool HasDiff => Added is not null || DiffTruncated;

    /// <summary>Reads the <c>details</c> object of a result or tool-result message; null without one.</summary>
    public static ChatToolResultDiffFacts? Read(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
            return null;
        long? added = null, removed = null;
        var truncated = false;
        if (details.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String &&
            TryParseDetailsDiff(diff.GetString(), out var counts))
        {
            (added, removed) = counts is { } exact ? (exact.Added, exact.Removed) : ((long?)null, (long?)null);
            truncated = counts is null;
        }
        return new(added, removed, truncated, ReadBool(details, "created"), ReadBool(details, "changed"));
    }

    /// <summary>
    /// Parses the edit tool's numbered display diff (<c>+12 text</c>, <c>-11 text</c>, <c> 13 text</c>,
    /// <c>...</c>). False when it is not that format or has no changes; null counts when truncated.
    /// Skip markers count toward the row bound, as on the web.
    /// </summary>
    public static bool TryParseDetailsDiff(string? diff, out (long Added, long Removed)? counts)
    {
        counts = null;
        if (string.IsNullOrWhiteSpace(diff))
            return false;
        long added = 0, removed = 0;
        var rows = 0;
        var truncated = false;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            if (DetailsTruncated.IsMatch(raw))
            {
                truncated = true;
                rows++;
                continue;
            }
            if (DetailsSkip.IsMatch(raw))
            {
                rows++;
                continue;
            }
            var match = DetailsLine.Match(raw);
            if (!match.Success)
                return false;
            if (match.Groups[1].Value == "+") added++;
            else if (match.Groups[1].Value == "-") removed++;
            if (++rows > MaxDiffRenderLines)
            {
                truncated = true;
                break;
            }
        }
        if (added == 0 && removed == 0)
            return false;
        counts = truncated ? null : (added, removed);
        return true;
    }

    private static bool? ReadBool(JsonElement source, string key) =>
        source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
}
