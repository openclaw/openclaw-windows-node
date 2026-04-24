using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Manages recursive INPC subscriptions for a single UseObservableTree call.
/// Walks the object graph from a root, subscribes to PropertyChanged on every
/// reachable INotifyPropertyChanged object, and re-renders on any change.
/// Automatically handles cycle detection, nested object replacement, and cleanup.
/// </summary>
internal class ObservableTreeTracker : IDisposable
{
    private readonly Action _requestRerender;
    private readonly Dictionary<INotifyPropertyChanged, PropertyChangedEventHandler> _subscriptions = new();
    private readonly HashSet<INotifyPropertyChanged> _visiting = new();
    private INotifyPropertyChanged? _root;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _inpcPropertyCache = new();

    public ObservableTreeTracker(Action requestRerender)
    {
        _requestRerender = requestRerender;
        try { _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { /* No WinUI runtime (e.g. unit tests) */ }
    }

    /// <summary>
    /// Per-type cache of properties that could hold INPC values.
    /// Filters to: public instance properties, getter accessible,
    /// property type is class or interface (value types can't be INPC).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ObservableTreeTracker uses reflection to discover INPC-candidate properties.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "ObservableTreeTracker uses reflection to discover INPC-candidate properties.")]
    internal static PropertyInfo[] GetInpcCandidateProperties(Type type)
        => _inpcPropertyCache.GetOrAdd(type, t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && !p.PropertyType.IsValueType)
             .ToArray());

    /// <summary>
    /// Synchronize subscriptions to match the current object graph.
    /// Called on mount and whenever the source reference changes.
    /// </summary>
    public void SyncSubscriptions(INotifyPropertyChanged root)
    {
        _root = root;
        var desiredSet = new HashSet<INotifyPropertyChanged>(ReferenceEqualityComparer.Instance);
        _visiting.Clear();
        Walk(root, desiredSet);

        // Unsubscribe from objects no longer in the graph
        var toRemove = new List<INotifyPropertyChanged>();
        foreach (var kvp in _subscriptions)
        {
            if (!desiredSet.Contains(kvp.Key))
            {
                kvp.Key.PropertyChanged -= kvp.Value;
                toRemove.Add(kvp.Key);
            }
        }
        foreach (var obj in toRemove)
            _subscriptions.Remove(obj);

        // Subscribe to new objects in the graph
        foreach (var obj in desiredSet)
        {
            if (!_subscriptions.ContainsKey(obj))
            {
                PropertyChangedEventHandler handler = OnNestedPropertyChanged;
                obj.PropertyChanged += handler;
                _subscriptions[obj] = handler;
            }
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _subscriptions)
            kvp.Key.PropertyChanged -= kvp.Value;
        _subscriptions.Clear();
    }

    private void Walk(INotifyPropertyChanged? node, HashSet<INotifyPropertyChanged> desiredSet)
    {
        if (node is null || !_visiting.Add(node))
            return; // null or cycle detected

        desiredSet.Add(node);

        foreach (var prop in GetInpcCandidateProperties(node.GetType()))
        {
            try
            {
                var value = prop.GetValue(node);
                if (value is INotifyPropertyChanged inpc)
                    Walk(inpc, desiredSet);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Debug.WriteLine($"[Reactor.ObservableTreeTracker] Walk: property {prop.Name} threw: {ex.Message}");
            }
        }

        _visiting.Remove(node);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ObservableTreeTracker uses reflection to inspect property changes.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "ObservableTreeTracker uses reflection to inspect property changes.")]
    private void OnNestedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _requestRerender();

        if (sender is null || string.IsNullOrEmpty(e.PropertyName))
            return;

        var senderType = sender.GetType();
        var prop = senderType.GetProperty(e.PropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType.IsValueType)
            return;

        // SyncSubscriptions mutates non-thread-safe _subscriptions and _visiting.
        // PropertyChanged can fire from any thread, so marshal to the UI thread.
        Microsoft.UI.Dispatching.DispatcherQueue? currentDispatcher = null;
        try { currentDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { /* No WinUI runtime (e.g. unit tests) */ }
        if (currentDispatcher is not null || _dispatcherQueue is null)
        {
            // Already on the UI thread, or no dispatcher available (test environment) — sync directly
            SyncFromRoot();
        }
        else
        {
            // Background thread — enqueue on the UI dispatcher
            _dispatcherQueue.TryEnqueue(() => SyncFromRoot());
        }

        void SyncFromRoot()
        {
            try
            {
                var root = FindRoot();
                if (root is not null)
                    SyncSubscriptions(root);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Debug.WriteLine($"[Reactor.ObservableTreeTracker] OnNestedPropertyChanged: property access failed: {ex.Message}");
            }
        }
    }

    private INotifyPropertyChanged? FindRoot() => _root;
}
