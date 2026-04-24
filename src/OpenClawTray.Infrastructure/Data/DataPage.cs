namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// A page of data from a data source.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items in this page.</param>
/// <param name="ContinuationToken">
/// Token for fetching the next page. Null means this is the last page.
/// </param>
/// <param name="TotalCount">
/// Total number of items matching the current filters (if the source supports counting).
/// Null if the source doesn't support server-side counting.
/// </param>
public record DataPage<T>(
    IReadOnlyList<T> Items,
    string? ContinuationToken = null,
    int? TotalCount = null);
