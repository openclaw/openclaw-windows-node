using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// A cached page entry: the mounted control, element snapshot, and access time.
/// </summary>
internal struct CachedPage
{
    public UIElement MountedControl;
    public Element? LastElement;
    public DateTime LastAccessed;
    public NavigationCacheMode CacheMode;
}

/// <summary>
/// LRU page cache for NavigationHost. Stores mounted controls keyed by route
/// (using structural equality) so navigating back restores the exact visual state.
/// </summary>
internal sealed class NavigationCache
{
    private readonly Dictionary<object, CachedPage> _cache = new();
    private readonly Action<UIElement>? _onEvict;
    private readonly object _lock = new();

    public int MaxSize { get; set; }

    /// <param name="maxSize">Maximum number of entries before LRU eviction.</param>
    /// <param name="onEvict">Called when an entry is evicted so the reconciler can unmount it.</param>
    public NavigationCache(int maxSize, Action<UIElement>? onEvict = null)
    {
        MaxSize = maxSize;
        _onEvict = onEvict;
    }

    /// <summary>
    /// Tries to retrieve a cached page for the given route.
    /// Updates <see cref="CachedPage.LastAccessed"/> on hit.
    /// </summary>
    public bool TryGet(object route, out CachedPage page)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(route, out page))
            {
                page.LastAccessed = DateTime.UtcNow;
                _cache[route] = page;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Adds a page to the cache. Evicts the LRU entry if the cache is full.
    /// </summary>
    public void Add(object route, CachedPage page)
    {
        lock (_lock)
        {
            page.LastAccessed = DateTime.UtcNow;
            _cache[route] = page;

            while (_cache.Count > MaxSize)
            {
                if (!EvictLocked())
                    break;
            }
        }
    }

    /// <summary>
    /// Removes the least recently accessed entry that is not <see cref="NavigationCacheMode.Required"/>.
    /// Returns false if all entries are Required and nothing was evicted.
    /// </summary>
    public bool Evict()
    {
        lock (_lock) { return EvictLocked(); }
    }

    private bool EvictLocked()
    {
        object? lruKey = null;
        var lruTime = DateTime.MaxValue;

        foreach (var (key, entry) in _cache)
        {
            if (entry.CacheMode == NavigationCacheMode.Required)
                continue;
            if (entry.LastAccessed < lruTime)
            {
                lruTime = entry.LastAccessed;
                lruKey = key;
            }
        }

        if (lruKey is not null)
        {
            var evicted = _cache[lruKey];
            _cache.Remove(lruKey);
            _onEvict?.Invoke(evicted.MountedControl);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a specific route from the cache without invoking the eviction callback.
    /// Used when a cached page is restored to the grid.
    /// </summary>
    public bool Remove(object route)
    {
        lock (_lock) { return _cache.Remove(route); }
    }

    /// <summary>
    /// Evicts all entries, invoking the eviction callback for each.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var entry in _cache.Values)
                _onEvict?.Invoke(entry.MountedControl);
            _cache.Clear();
        }
    }

    public int Count { get { lock (_lock) { return _cache.Count; } } }
}
