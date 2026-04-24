using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace OpenClawTray.Infrastructure.Data.Providers;

/// <summary>
/// In-memory data source backed by a list. Supports client-side sorting,
/// filtering, text search, paging, and CRUD mutations.
/// </summary>
public class ListDataSource<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T> : IDataSource<T>, IMutableDataSource<T>
{
    private readonly List<T> _items;
    private readonly Func<T, RowKey> _getRowKey;
    private readonly object _lock = new();

    public ListDataSource(IEnumerable<T> items, Func<T, RowKey> getRowKey)
    {
        _items = new List<T>(items);
        _getRowKey = getRowKey;
    }

    /// <summary>For subclasses that manage their own item list.</summary>
    protected ListDataSource(List<T> items, Func<T, RowKey> getRowKey, bool directReference)
    {
        _items = items;
        _getRowKey = getRowKey;
    }

    /// <summary>Direct access to the backing list for subclasses.</summary>
    protected List<T> Items => _items;

    public DataSourceCapabilities Capabilities =>
        DataSourceCapabilities.ServerSort |
        DataSourceCapabilities.ServerFilter |
        DataSourceCapabilities.ServerSearch |
        DataSourceCapabilities.ServerCount |
        DataSourceCapabilities.Mutate;

    public RowKey GetRowKey(T item) => _getRowKey(item);

    public Task<DataPage<T>> GetPageAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<T> snapshot;
        lock (_lock)
        {
            snapshot = new List<T>(_items);
        }

        IEnumerable<T> query = snapshot;

        // Apply filters
        if (request.Filters is { Count: > 0 })
        {
            foreach (var filter in request.Filters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                query = ApplyFilter(query, filter);
            }
        }

        // Apply search
        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            var searchQuery = request.SearchQuery;
            query = ApplySearch(query, searchQuery);
        }

        var filtered = query.ToList();
        var totalCount = filtered.Count;

        // Apply sort
        if (request.Sort is { Count: > 0 })
        {
            filtered = ApplySort(filtered, request.Sort);
        }

        // Apply paging
        var offset = 0;
        if (request.ContinuationToken is not null && int.TryParse(request.ContinuationToken, out var parsed))
            offset = parsed;

        var pageSize = request.PageSize;
        var pageItems = filtered.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + pageItems.Count;
        var continuationToken = nextOffset < filtered.Count ? nextOffset.ToString() : null;

        return Task.FromResult(new DataPage<T>(pageItems, continuationToken, totalCount));
    }

    public Task<T> CreateAsync(T item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _items.Add(item);
        }
        return Task.FromResult(item);
    }

    public Task<T> UpdateAsync(RowKey key, T item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var index = _items.FindIndex(x => _getRowKey(x).Equals(key));
            if (index < 0) throw new KeyNotFoundException($"Row key '{key}' not found.");
            _items[index] = item;
        }
        return Task.FromResult(item);
    }

    public Task DeleteAsync(RowKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var index = _items.FindIndex(x => _getRowKey(x).Equals(key));
            if (index < 0) throw new KeyNotFoundException($"Row key '{key}' not found.");
            _items.RemoveAt(index);
        }
        return Task.CompletedTask;
    }

    // ── Filter application ──────────────────────────────────────

    private static IEnumerable<T> ApplyFilter(IEnumerable<T> items, FilterDescriptor filter)
    {
        var prop = typeof(T).GetProperty(filter.Field, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return items;

        return filter.Operator switch
        {
            FilterOperator.Equals => items.Where(x => Equals(prop.GetValue(x), filter.Value)),
            FilterOperator.NotEquals => items.Where(x => !Equals(prop.GetValue(x), filter.Value)),
            FilterOperator.Contains => items.Where(x => StringContains(prop.GetValue(x), filter.Value)),
            FilterOperator.StartsWith => items.Where(x => StringStartsWith(prop.GetValue(x), filter.Value)),
            FilterOperator.EndsWith => items.Where(x => StringEndsWith(prop.GetValue(x), filter.Value)),
            FilterOperator.GreaterThan => items.Where(x => Compare(prop.GetValue(x), filter.Value) > 0),
            FilterOperator.GreaterThanOrEqual => items.Where(x => Compare(prop.GetValue(x), filter.Value) >= 0),
            FilterOperator.LessThan => items.Where(x => Compare(prop.GetValue(x), filter.Value) < 0),
            FilterOperator.LessThanOrEqual => items.Where(x => Compare(prop.GetValue(x), filter.Value) <= 0),
            FilterOperator.Between => items.Where(x =>
                Compare(prop.GetValue(x), filter.Value) >= 0 &&
                Compare(prop.GetValue(x), filter.ValueTo) <= 0),
            FilterOperator.In => items.Where(x => InCollection(prop.GetValue(x), filter.Value)),
            FilterOperator.IsNull => items.Where(x => prop.GetValue(x) is null),
            FilterOperator.IsNotNull => items.Where(x => prop.GetValue(x) is not null),
            _ => items,
        };
    }

    private static bool StringContains(object? value, object? search)
        => value?.ToString()?.Contains(search?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true;

    private static bool StringStartsWith(object? value, object? search)
        => value?.ToString()?.StartsWith(search?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true;

    private static bool StringEndsWith(object? value, object? search)
        => value?.ToString()?.EndsWith(search?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true;

    private static int Compare(object? a, object? b)
    {
        if (a is IComparable ca && b is not null)
            return ca.CompareTo(Convert.ChangeType(b, a.GetType()));
        return 0;
    }

    private static bool InCollection(object? value, object? collection)
    {
        if (collection is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (Equals(value, item)) return true;
        }
        return false;
    }

    // ── Search ──────────────────────────────────────────────────

    private static IEnumerable<T> ApplySearch(IEnumerable<T> items, string query)
    {
        var stringProps = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead)
            .ToArray();

        if (stringProps.Length == 0) return items;

        return items.Where(item =>
            stringProps.Any(p =>
                p.GetValue(item)?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
    }

    // ── Sort ────────────────────────────────────────────────────

    private static List<T> ApplySort(List<T> items, IReadOnlyList<SortDescriptor> sorts)
    {
        if (sorts.Count == 0) return items;

        IOrderedEnumerable<T>? ordered = null;
        foreach (var sort in sorts)
        {
            var prop = typeof(T).GetProperty(sort.Field, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;

            if (ordered is null)
            {
                ordered = sort.Direction == SortDirection.Ascending
                    ? items.OrderBy(x => prop.GetValue(x))
                    : items.OrderByDescending(x => prop.GetValue(x));
            }
            else
            {
                ordered = sort.Direction == SortDirection.Ascending
                    ? ordered.ThenBy(x => prop.GetValue(x))
                    : ordered.ThenByDescending(x => prop.GetValue(x));
            }
        }

        return ordered?.ToList() ?? items;
    }
}
