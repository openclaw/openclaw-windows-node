// Port of d3-array/src/group.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Groups and rolls up data by key.
/// Direct port of d3.group() and d3.rollup().
/// </summary>
public static class D3Group
{
    /// <summary>
    /// Groups the given values by a key accessor, returning a dictionary of key → list.
    /// </summary>
    public static Dictionary<TKey, List<T>> Group<T, TKey>(
        IEnumerable<T> values,
        Func<T, TKey> key) where TKey : notnull
    {
        var groups = new Dictionary<TKey, List<T>>();
        foreach (var item in values)
        {
            var k = key(item);
            if (!groups.TryGetValue(k, out var list))
            {
                list = [];
                groups[k] = list;
            }
            list.Add(item);
        }
        return groups;
    }

    /// <summary>
    /// Groups the given values by a key accessor, then reduces each group.
    /// </summary>
    public static Dictionary<TKey, TResult> Rollup<T, TKey, TResult>(
        IEnumerable<T> values,
        Func<T, TKey> key,
        Func<IEnumerable<T>, TResult> reduce) where TKey : notnull
    {
        var groups = Group(values, key);
        var result = new Dictionary<TKey, TResult>();
        foreach (var (k, v) in groups)
        {
            result[k] = reduce(v);
        }
        return result;
    }

    /// <summary>
    /// Returns an index mapping each key to the first matching value.
    /// </summary>
    public static Dictionary<TKey, T> Index<T, TKey>(
        IEnumerable<T> values,
        Func<T, TKey> key) where TKey : notnull
    {
        var index = new Dictionary<TKey, T>();
        foreach (var item in values)
        {
            var k = key(item);
            index.TryAdd(k, item);
        }
        return index;
    }
}
