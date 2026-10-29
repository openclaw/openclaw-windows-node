using System.Collections.Immutable;
using System.Globalization;

namespace OpenClaw.Shared.Dashboards;

public enum DashboardGallerySort
{
    Updated,
    Title,
}

/// <summary>User-selected gallery filters. An empty author id means all authors.</summary>
public sealed record DashboardGalleryFilters(string Query = "", string AuthorId = "", DashboardGallerySort Sort = DashboardGallerySort.Updated);

public sealed record DashboardAuthor(string Id, string Label)
{
    /// <summary>Avatar initial: first letter of the label, upper-cased, or <c>?</c>.</summary>
    public string Initial
    {
        get
        {
            var label = Label.Trim();
            return label.Length == 0
                ? "?"
                : StringInfo.GetNextTextElement(label).ToUpper(CultureInfo.CurrentCulture);
        }
    }
}

/// <summary>
/// Gallery projection matching upstream <c>pages/dashboards/view.tsx</c>:
/// author resolution, search, author filter, and ordering.
/// </summary>
public static class DashboardGalleryPolicy
{
    /// <summary>
    /// Author of a dashboard: the creating actor, else the current owner, with
    /// the row's agent (then the selected agent) as the id fallback.
    /// </summary>
    public static DashboardAuthor ResolveAuthor(DashboardSessionRow row, string fallbackAgentId)
    {
        var actor = row.CreatedActor ?? row.OwnerActor;
        var id = FirstNonBlank(actor?.Id, row.AgentId, fallbackAgentId) ?? "";
        var label = FirstNonBlank(actor?.Label, id) ?? "";
        return new DashboardAuthor(id, label);
    }

    /// <summary>Distinct authors of all rows, ordered by label for the author filter.</summary>
    public static ImmutableArray<DashboardAuthor> Authors(IEnumerable<DashboardSessionRow> rows, string fallbackAgentId)
    {
        var byId = new Dictionary<string, DashboardAuthor>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var author = ResolveAuthor(row, fallbackAgentId);
            byId[author.Id] = author;
        }
        return [.. byId.Values.OrderBy(author => author.Label, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Applies the filters. <paramref name="title"/> supplies the localized
    /// display title used for search and title ordering.
    /// </summary>
    public static ImmutableArray<DashboardSessionRow> Apply(
        IEnumerable<DashboardSessionRow> rows,
        DashboardGalleryFilters filters,
        string fallbackAgentId,
        Func<DashboardSessionRow, string> title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var query = filters.Query.Trim();
        var matches = rows.Where(row =>
        {
            var author = ResolveAuthor(row, fallbackAgentId);
            if (filters.AuthorId.Length > 0 && !string.Equals(author.Id, filters.AuthorId, StringComparison.Ordinal))
                return false;
            return query.Length == 0 ||
                Contains(title(row), query) ||
                Contains(author.Label, query) ||
                Contains(row.LastMessagePreview, query) ||
                Contains(row.Key, query);
        });

        return filters.Sort == DashboardGallerySort.Title
            ? [.. matches.OrderBy(title, StringComparer.CurrentCulture)]
            : [.. matches.OrderByDescending(row => row.UpdatedAtMs ?? 0)];
    }

    private static bool Contains(string? value, string query) =>
        value is not null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(value, query, CompareOptions.IgnoreCase) >= 0;

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
