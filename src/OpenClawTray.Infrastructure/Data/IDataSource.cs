namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// A read-only data source that provides paged access to items.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public interface IDataSource<T>
{
    /// <summary>Fetches a page of data matching the request.</summary>
    Task<DataPage<T>> GetPageAsync(DataRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets the stable key for an item.</summary>
    RowKey GetRowKey(T item);

    /// <summary>Declares what this data source can do server-side.</summary>
    DataSourceCapabilities Capabilities { get; }
}

/// <summary>
/// Capabilities flags for data sources.
/// </summary>
[Flags]
public enum DataSourceCapabilities
{
    None = 0,
    ServerSort = 1 << 0,
    ServerFilter = 1 << 1,
    ServerSearch = 1 << 2,
    ServerCount = 1 << 3,
    ServerSelect = 1 << 4,
    Mutate = 1 << 5,
    Refresh = 1 << 6,
}

/// <summary>
/// A mutable data source that supports CRUD operations.
/// </summary>
public interface IMutableDataSource<T> : IDataSource<T>
{
    Task<T> CreateAsync(T item, CancellationToken cancellationToken = default);
    Task<T> UpdateAsync(RowKey key, T item, CancellationToken cancellationToken = default);
    Task DeleteAsync(RowKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// A data source that fires events when data changes.
/// </summary>
public interface IObservableDataSource<T> : IDataSource<T>
{
    event Action? DataChanged;
}

/// <summary>
/// A data source that supports fetching individual items by key.
/// </summary>
public interface IKeyedDataSource<T> : IDataSource<T>
{
    Task<T?> GetByKeyAsync(RowKey key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> GetByKeysAsync(IEnumerable<RowKey> keys, CancellationToken cancellationToken = default);
}
