using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace OpenClawTray.A2UI.DataModel;

/// <summary>
/// Per-surface JsonObject store, mutated via JSON Pointer (RFC 6901) patches.
/// Notifies registered observers when paths change. Thread-affine to a UI dispatcher.
/// </summary>
public sealed class DataModelStore
{
    // Per-update caps. Bounded so an adversarial dataModelUpdate can't drive the
    // UI thread into an OOM or a million-entry loop. Sized to dwarf realistic
    // catalogs while still rejecting obvious abuse.
    internal const int MaxEntriesPerUpdate = 1024;
    internal const int MaxValueMapDepth = 32;
    internal const int MaxKeyLength = 256;
    internal const int MaxStringValueLength = 64 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, SurfaceModel> _surfaces = new(StringComparer.Ordinal);
    private readonly DispatcherQueue _dispatcher;

    public DataModelStore(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public DataModelObservable GetOrCreate(string surfaceId, JsonObject? seed = null)
    {
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var model))
            {
                model = new SurfaceModel(seed != null ? (JsonObject)seed.DeepClone() : new JsonObject());
                _surfaces[surfaceId] = model;
            }
            return new DataModelObservable(model, _dispatcher);
        }
    }

    public void Reset(string surfaceId, JsonObject? seed = null)
    {
        SurfaceModel? model;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out model))
            {
                model = new SurfaceModel(seed != null ? (JsonObject)seed.DeepClone() : new JsonObject());
                _surfaces[surfaceId] = model;
                return;
            }
            model.Replace(seed != null ? (JsonObject)seed.DeepClone() : new JsonObject());
        }
        new DataModelObservable(model, _dispatcher).NotifyAllPaths();
    }

    public void Remove(string surfaceId)
    {
        lock (_lock) { _surfaces.Remove(surfaceId); }
    }

    public void RemoveAll()
    {
        lock (_lock) { _surfaces.Clear(); }
    }

    /// <summary>
    /// Apply a v0.8 dataModelUpdate batch. Each entry's <c>key</c> is appended
    /// to the optional <paramref name="basePath"/> to form the full pointer.
    /// Special case: basePath="/" or null with key="" replaces the whole tree.
    /// Coalesced — observers fire once per affected path after the batch.
    /// </summary>
    public void ApplyDataModelUpdate(string surfaceId, string? basePath, IReadOnlyList<Protocol.DataModelEntry> entries)
    {
        // Drop oversize batches at the boundary. Smaller-than-cap batches still
        // get per-entry sanity checks below.
        if (entries.Count > MaxEntriesPerUpdate)
            return;

        SurfaceModel model;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var existing))
            {
                existing = new SurfaceModel(new JsonObject());
                _surfaces[surfaceId] = existing;
            }
            model = existing;
        }

        var changed = new List<string>(entries.Count);
        var prefix = NormalizePath(basePath ?? "/");
        if (prefix == "/") prefix = "";

        foreach (var entry in entries)
        {
            // Per-entry caps: drop the entry rather than aborting the whole batch
            // (consistent with the existing "skip bad pointer" tolerance).
            if (entry.Key.Length > MaxKeyLength) continue;
            if (entry.ValueString != null && entry.ValueString.Length > MaxStringValueLength) continue;
            if (!IsWithinDepth(entry.ValueMap, depth: 1, max: MaxValueMapDepth)) continue;

            try
            {
                var pointer = string.IsNullOrEmpty(entry.Key)
                    ? (string.IsNullOrEmpty(prefix) ? "/" : prefix)
                    : prefix + "/" + EncodePointerToken(entry.Key);
                // SetByPointer takes the SurfaceModel.Sync lock internally.
                model.SetByPointer(pointer, entry.ToJsonNode());
                changed.Add(NormalizePath(pointer));
            }
            catch (Exception)
            {
                // bad pointer; skip — router logs aggregate.
            }
        }

        if (changed.Count > 0)
            new DataModelObservable(model, _dispatcher).NotifyPaths(changed);
    }

    private static bool IsWithinDepth(IReadOnlyList<Protocol.DataModelEntry>? map, int depth, int max)
    {
        if (map == null) return true;
        if (depth > max) return false;
        foreach (var e in map)
            if (!IsWithinDepth(e.ValueMap, depth + 1, max)) return false;
        return true;
    }

    /// <summary>
    /// RFC 6901 token escape: <c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>. The
    /// caller's <c>entry.Key</c> is treated as a single pointer reference token,
    /// so a key like <c>"users/0/name"</c> escapes to one segment
    /// <c>users~10~1name</c> — it does NOT split into nested path segments.
    /// Use <c>basePath</c> to traverse into nested objects.
    /// </summary>
    private static string EncodePointerToken(string key) =>
        key.Replace("~", "~0").Replace("/", "~1");

    public JsonNode? Read(string surfaceId, string pointer)
    {
        SurfaceModel? model;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out model)) return null;
        }
        try { return model.GetByPointer(pointer); } catch { return null; }
    }

    private static string NormalizePath(string p) =>
        string.IsNullOrEmpty(p) ? "/" : (p[0] == '/' ? p : "/" + p);

    /// <summary>Internal mutable holder; shared between observable views.</summary>
    internal sealed class SurfaceModel
    {
        // Per-model lock guarding Root and any traversal/mutation. JsonObject
        // and JsonArray are not thread-safe, so every read AND every write must
        // go through this lock — including the deep-clone in canvas.a2ui.dump.
        public readonly object Sync = new();
        // Single dictionary keyed by normalized pointer path → list of subscribers.
        public readonly Dictionary<string, List<Action>> Subscribers = new(StringComparer.Ordinal);
        public JsonObject Root { get; private set; }

        public SurfaceModel(JsonObject root) { Root = root; }

        public void Replace(JsonObject newRoot) { lock (Sync) { Root = newRoot; } }

        public JsonNode? GetByPointer(string pointer)
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(pointer) || pointer == "/" || pointer == "")
                    return Root;
                var (parent, key, isIndex, idx) = Resolve(pointer, createMissing: false);
                if (parent == null) return null;
                if (parent is JsonObject po) return po[key!];
                if (parent is JsonArray pa) return isIndex && idx >= 0 && idx < pa.Count ? pa[idx] : null;
                return null;
            }
        }

        public void SetByPointer(string pointer, JsonNode? value)
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(pointer) || pointer == "/")
                {
                    if (value is JsonObject obj) Root = obj;
                    else if (value != null)
                    {
                        // Whole-tree replace requires an object root. Coerce a
                        // scalar/array into { "value": <scalar> } rather than
                        // silently dropping the write — the previous no-op
                        // behaviour masked agent bugs.
                        Root = new JsonObject { ["value"] = value.DeepClone() };
                    }
                    return;
                }
                var (parent, key, isIndex, idx) = Resolve(pointer, createMissing: true);
                if (parent is JsonObject po)
                {
                    po[key!] = value;
                }
                else if (parent is JsonArray pa)
                {
                    while (pa.Count <= idx) pa.Add(null);
                    pa[idx] = value;
                }
            }
        }

        /// <summary>Atomic deep-clone of the root JsonObject. Required for snapshot/dump consumers.</summary>
        public JsonObject CloneRoot()
        {
            lock (Sync) { return (JsonObject)Root.DeepClone(); }
        }

        private (JsonNode? parent, string? key, bool isIndex, int idx) Resolve(string pointer, bool createMissing)
        {
            var tokens = SplitPointer(pointer);
            if (tokens.Count == 0) return (Root, null, false, -1);

            JsonNode? cursor = Root;
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                var tok = tokens[i];
                if (cursor is JsonObject obj)
                {
                    if (obj[tok] == null)
                    {
                        if (!createMissing) return (null, null, false, -1);
                        var nextIsIndex = int.TryParse(tokens[i + 1], out _);
                        obj[tok] = nextIsIndex ? new JsonArray() : new JsonObject();
                    }
                    cursor = obj[tok];
                }
                else if (cursor is JsonArray arr)
                {
                    if (!int.TryParse(tok, out var ai)) return (null, null, false, -1);
                    while (createMissing && arr.Count <= ai) arr.Add(null);
                    if (ai < 0 || ai >= arr.Count) return (null, null, false, -1);
                    cursor = arr[ai];
                }
                else
                {
                    return (null, null, false, -1);
                }
            }

            var last = tokens[^1];
            if (cursor is JsonArray finalArr && int.TryParse(last, out var idx))
                return (finalArr, last, true, idx);
            return (cursor, last, false, -1);
        }

        private static List<string> SplitPointer(string pointer)
        {
            var p = pointer.StartsWith('/') ? pointer.Substring(1) : pointer;
            var parts = p.Split('/');
            var result = new List<string>(parts.Length);
            foreach (var part in parts)
                result.Add(part.Replace("~1", "/").Replace("~0", "~"));
            return result;
        }
    }
}

/// <summary>
/// View on a SurfaceModel that exposes per-path INotifyPropertyChanged-style
/// callbacks for binding into XAML. Multiple instances may share the same model.
/// </summary>
public sealed class DataModelObservable
{
    private readonly DataModelStore.SurfaceModel _model;
    private readonly DispatcherQueue _dispatcher;

    internal DataModelObservable(DataModelStore.SurfaceModel model, DispatcherQueue dispatcher)
    {
        _model = model;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Direct reference to the root object. Callers MUST NOT mutate or
    /// enumerate concurrently with writes; prefer <see cref="CloneRoot"/> for
    /// any consumer that needs a stable view.
    /// </summary>
    public JsonObject Root => _model.Root;

    /// <summary>Atomic deep-clone of the root object. Safe to enumerate off-dispatcher.</summary>
    public JsonObject CloneRoot() => _model.CloneRoot();

    public JsonNode? Read(string pointer) => _model.GetByPointer(pointer);

    public string? ReadString(string pointer)
    {
        var node = Read(pointer);
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return node?.ToString();
    }

    /// <summary>
    /// Two-way write: updates the data model, notifies subscribers on the dispatcher.
    /// </summary>
    public void Write(string pointer, JsonNode? value)
    {
        try
        {
            _model.SetByPointer(pointer, value);
            NotifyPaths(new[] { Normalize(pointer) });
        }
        catch { /* swallow; bad pointer */ }
    }

    /// <summary>
    /// Subscribe to changes on a specific JSON Pointer path. Returns disposable
    /// that unsubscribes. Callbacks run on the dispatcher thread.
    /// </summary>
    public IDisposable Subscribe(string pointer, Action callback)
    {
        var key = Normalize(pointer);
        lock (_model.Subscribers)
        {
            if (!_model.Subscribers.TryGetValue(key, out var list))
            {
                list = new List<Action>();
                _model.Subscribers[key] = list;
            }
            list.Add(callback);
        }
        return new Subscription(_model, key, callback);
    }

    internal void NotifyPaths(IEnumerable<string> paths)
    {
        var fired = new HashSet<Action>();
        foreach (var raw in paths)
        {
            var key = Normalize(raw);
            // Notify exact path + all ancestor paths.
            var current = key;
            while (true)
            {
                List<Action>? subs;
                lock (_model.Subscribers)
                {
                    _model.Subscribers.TryGetValue(current, out subs);
                    subs = subs == null ? null : new List<Action>(subs);
                }
                if (subs != null)
                {
                    foreach (var s in subs)
                        if (fired.Add(s)) Dispatch(s);
                }
                if (current == "/" || string.IsNullOrEmpty(current)) break;
                var slash = current.LastIndexOf('/');
                current = slash <= 0 ? "/" : current.Substring(0, slash);
            }
        }
    }

    internal void NotifyAllPaths()
    {
        List<Action> all;
        lock (_model.Subscribers)
        {
            all = new List<Action>();
            foreach (var subs in _model.Subscribers.Values) all.AddRange(subs);
        }
        foreach (var s in all) Dispatch(s);
    }

    private void Dispatch(Action callback)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess) { try { callback(); } catch { } return; }
        _dispatcher.TryEnqueue(() => { try { callback(); } catch { } });
    }

    private static string Normalize(string p) =>
        string.IsNullOrEmpty(p) ? "/" : (p[0] == '/' ? p : "/" + p);

    private sealed class Subscription : IDisposable
    {
        private readonly DataModelStore.SurfaceModel _model;
        private readonly string _key;
        private readonly Action _cb;
        public Subscription(DataModelStore.SurfaceModel m, string k, Action c) { _model = m; _key = k; _cb = c; }
        public void Dispose()
        {
            lock (_model.Subscribers)
            {
                if (_model.Subscribers.TryGetValue(_key, out var list))
                {
                    list.Remove(_cb);
                    if (list.Count == 0) _model.Subscribers.Remove(_key);
                }
            }
        }
    }
}
