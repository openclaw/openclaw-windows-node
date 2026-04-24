namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// Status of a cached block.
/// </summary>
public enum BlockStatus
{
    /// <summary>Block is currently being loaded from the data source.</summary>
    Loading,
    /// <summary>Block data is available.</summary>
    Loaded,
    /// <summary>Block load failed.</summary>
    Failed,
}

/// <summary>
/// A cached block of data from a paged data source.
/// </summary>
public record CacheBlock<T>(
    int BlockIndex,
    IReadOnlyList<T> Items,
    BlockStatus Status,
    string? Error = null)
{
    /// <summary>Empty loading placeholder block.</summary>
    public static CacheBlock<T> LoadingBlock(int index) =>
        new(index, Array.Empty<T>(), BlockStatus.Loading);
}

/// <summary>
/// Caches pages of data from IDataSource, keyed by sort/filter state + page index.
/// Evicts blocks outside the viewport via LRU when max capacity is reached.
///
/// Follows a pull model (matching Compose Paging 3): accessing a row index that
/// falls in an unloaded block triggers the fetch. The virtualizer tells the cache
/// which blocks are needed; the cache returns immediately for loaded blocks and
/// initiates async loads for missing ones.
/// </summary>
public class DataPageCache<T>
{
    private readonly IDataSource<T> _source;
    private readonly int _blockSize;
    private readonly int _maxBlocks;
    private readonly object _lock = new();

    // LRU tracking: most recently accessed block index at the end.
    private readonly LinkedList<int> _lruOrder = new();
    private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();
    private readonly Dictionary<int, CacheBlock<T>> _blocks = new();
    private readonly HashSet<int> _inflight = new();

    private DataRequest _currentState = new();
    private int? _totalCount;

    /// <summary>Fires when a block finishes loading (success or failure).</summary>
    public event Action<int>? BlockLoaded;

    /// <summary>
    /// Creates a new DataPageCache.
    /// </summary>
    /// <param name="source">The data source to cache.</param>
    /// <param name="blockSize">Number of items per block (page size). Default 50.</param>
    /// <param name="maxBlocks">Maximum number of blocks to keep in cache. Default 20.</param>
    public DataPageCache(IDataSource<T> source, int blockSize = 50, int maxBlocks = 20)
    {
        _source = source;
        _blockSize = blockSize;
        _maxBlocks = maxBlocks;
    }

    /// <summary>Number of items per block.</summary>
    public int BlockSize => _blockSize;

    /// <summary>Maximum number of blocks in cache before LRU eviction.</summary>
    public int MaxBlocks => _maxBlocks;

    /// <summary>Total known row count from the last data source response.</summary>
    public int? TotalCount
    {
        get { lock (_lock) return _totalCount; }
    }

    /// <summary>Number of blocks currently in the cache.</summary>
    public int CachedBlockCount
    {
        get { lock (_lock) return _blocks.Count; }
    }

    /// <summary>
    /// Gets the current sort/filter state. Changing this via <see cref="SetState"/>
    /// invalidates all cached blocks.
    /// </summary>
    public DataRequest CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    /// <summary>
    /// Update the sort/filter state. Invalidates all cached blocks.
    /// </summary>
    public void SetState(DataRequest state)
    {
        lock (_lock)
        {
            _currentState = state;
            InvalidateInternal();
        }
    }

    /// <summary>
    /// Invalidate all cached blocks (e.g., after sort/filter change).
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            InvalidateInternal();
        }
    }

    private void InvalidateInternal()
    {
        _blocks.Clear();
        _lruOrder.Clear();
        _lruNodes.Clear();
        _inflight.Clear();
        _totalCount = null;
    }

    /// <summary>
    /// Get the block containing the given block index.
    /// Returns cached data immediately for loaded blocks, or initiates
    /// an async fetch for missing blocks and returns a loading placeholder.
    /// </summary>
    public CacheBlock<T> GetBlock(int blockIndex)
    {
        lock (_lock)
        {
            if (_blocks.TryGetValue(blockIndex, out var block))
            {
                TouchLru(blockIndex);
                return block;
            }

            // Not in cache — initiate fetch if not already in-flight
            if (!_inflight.Contains(blockIndex))
            {
                _inflight.Add(blockIndex);
                _ = FetchBlockAsync(blockIndex);
            }

            return CacheBlock<T>.LoadingBlock(blockIndex);
        }
    }

    /// <summary>
    /// Get the block containing the given block index asynchronously.
    /// If the block is cached, returns immediately. Otherwise waits for the fetch.
    /// </summary>
    public async ValueTask<CacheBlock<T>> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        CacheBlock<T>? cached;
        lock (_lock)
        {
            if (_blocks.TryGetValue(blockIndex, out cached))
            {
                TouchLru(blockIndex);
                return cached;
            }
        }

        // Fetch the block
        return await FetchBlockAsync(blockIndex, cancellationToken);
    }

    /// <summary>
    /// Get the item at a specific row index, or default if not loaded.
    /// Triggers an async fetch for missing blocks.
    /// </summary>
    public T? GetItem(int rowIndex)
    {
        var blockIndex = rowIndex / _blockSize;
        var offsetInBlock = rowIndex % _blockSize;

        var block = GetBlock(blockIndex);
        if (block.Status != BlockStatus.Loaded) return default;
        if (offsetInBlock >= block.Items.Count) return default;
        return block.Items[offsetInBlock];
    }

    /// <summary>
    /// Read-only peek: returns the item if its block is already loaded,
    /// or default if not. Does NOT trigger a fetch for missing blocks.
    /// Safe to call during a render pass.
    /// </summary>
    public T? PeekItem(int rowIndex)
    {
        var blockIndex = rowIndex / _blockSize;
        var offsetInBlock = rowIndex % _blockSize;

        lock (_lock)
        {
            if (_blocks.TryGetValue(blockIndex, out var block)
                && block.Status == BlockStatus.Loaded
                && offsetInBlock < block.Items.Count)
            {
                TouchLru(blockIndex);
                return block.Items[offsetInBlock];
            }
            return default;
        }
    }

    /// <summary>
    /// Request that a block be loaded if it isn't already cached or in-flight.
    /// The fetch is deferred via Task.Yield so it never runs synchronously
    /// in the caller's stack frame, but stays on the caller's thread (UI thread).
    /// Fires <see cref="BlockLoaded"/> on completion.
    /// </summary>
    public void RequestBlock(int blockIndex)
    {
        lock (_lock)
        {
            if (_blocks.ContainsKey(blockIndex)) return;
            if (_inflight.Contains(blockIndex)) return;
            _inflight.Add(blockIndex);
        }
        _ = RequestBlockAsync(blockIndex);
    }

    private async Task RequestBlockAsync(int blockIndex)
    {
        // Yield to the message loop so the fetch doesn't execute synchronously
        // inside the current render pass. On the UI thread this posts to the
        // DispatcherQueue; the fetch + BlockLoaded callback fire on the next tick.
        await Task.Yield();
        await FetchBlockAsync(blockIndex);
    }

    /// <summary>
    /// Check if a row index is loaded.
    /// </summary>
    public bool IsLoaded(int rowIndex)
    {
        var blockIndex = rowIndex / _blockSize;
        lock (_lock)
        {
            return _blocks.TryGetValue(blockIndex, out var block)
                   && block.Status == BlockStatus.Loaded;
        }
    }

    /// <summary>
    /// Get the status of a block.
    /// </summary>
    public BlockStatus GetBlockStatus(int blockIndex)
    {
        lock (_lock)
        {
            if (_blocks.TryGetValue(blockIndex, out var block))
                return block.Status;
            if (_inflight.Contains(blockIndex))
                return BlockStatus.Loading;
            return BlockStatus.Loading; // will be loaded on access
        }
    }

    // ── Internal fetch + LRU ──────────────────────────────────────

    private async Task<CacheBlock<T>> FetchBlockAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        DataRequest request;
        lock (_lock)
        {
            _inflight.Add(blockIndex);
            request = _currentState with
            {
                PageSize = _blockSize,
                ContinuationToken = (blockIndex * _blockSize).ToString(),
            };
        }

        try
        {
            var page = await _source.GetPageAsync(request, cancellationToken);

            var block = new CacheBlock<T>(blockIndex, page.Items, BlockStatus.Loaded);

            lock (_lock)
            {
                _inflight.Remove(blockIndex);
                StoreBlock(blockIndex, block);

                if (page.TotalCount.HasValue)
                    _totalCount = page.TotalCount;
            }

            BlockLoaded?.Invoke(blockIndex);
            return block;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _inflight.Remove(blockIndex);
            throw;
        }
        catch (Exception ex)
        {
            var block = new CacheBlock<T>(blockIndex, Array.Empty<T>(), BlockStatus.Failed, ex.Message);

            lock (_lock)
            {
                _inflight.Remove(blockIndex);
                StoreBlock(blockIndex, block);
            }

            BlockLoaded?.Invoke(blockIndex);
            return block;
        }
    }

    private void StoreBlock(int blockIndex, CacheBlock<T> block)
    {
        // Evict LRU if at capacity
        while (_blocks.Count >= _maxBlocks && _lruOrder.Count > 0)
        {
            var evict = _lruOrder.First!.Value;
            _lruOrder.RemoveFirst();
            _lruNodes.Remove(evict);
            _blocks.Remove(evict);
        }

        _blocks[blockIndex] = block;
        TouchLru(blockIndex);
    }

    private void TouchLru(int blockIndex)
    {
        if (_lruNodes.TryGetValue(blockIndex, out var node))
        {
            _lruOrder.Remove(node);
        }
        var newNode = _lruOrder.AddLast(blockIndex);
        _lruNodes[blockIndex] = newNode;
    }
}
