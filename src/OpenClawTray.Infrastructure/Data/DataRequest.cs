namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// Describes a data fetch request with paging, sorting, filtering, and search.
/// </summary>
public record DataRequest
{
    /// <summary>Number of items per page. Default 50.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Token for fetching the next page. Null for the first page.</summary>
    public string? ContinuationToken { get; init; }

    /// <summary>Sort operations to apply.</summary>
    public IReadOnlyList<SortDescriptor>? Sort { get; init; }

    /// <summary>Filter operations to apply (AND-ed together).</summary>
    public IReadOnlyList<FilterDescriptor>? Filters { get; init; }

    /// <summary>Free-text search query.</summary>
    public string? SearchQuery { get; init; }

    /// <summary>Fields to include in the result (projection). Null = all fields.</summary>
    public IReadOnlyList<string>? Select { get; init; }
}
