using System.Collections.Concurrent;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// In-memory cache for state that survives component unmount/remount.
/// Keyed by developer-provided string keys. Process-lifetime.
/// Keys should be stable and bounded (e.g., component-instance keys).
/// Dynamic or unbounded key patterns will cause memory growth.
/// </summary>
internal static class PersistedStateCache
{
    private const int MaxEntries = 4096;
    private static readonly ConcurrentDictionary<string, object?> _cache = new();

    internal static bool TryGet<T>(string key, out T value)
    {
        if (_cache.TryGetValue(key, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    internal static void Set<T>(string key, T value)
    {
        if (_cache.Count >= MaxEntries && !_cache.ContainsKey(key))
            return;
        _cache[key] = value;
    }

    internal static void Remove(string key)
    {
        _cache.TryRemove(key, out _);
    }

    internal static void Clear()
    {
        _cache.Clear();
    }
}
