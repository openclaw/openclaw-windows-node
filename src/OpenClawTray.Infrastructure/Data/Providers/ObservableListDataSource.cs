using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace OpenClawTray.Infrastructure.Data.Providers;

/// <summary>
/// An observable data source that watches an ObservableCollection for changes
/// and fires DataChanged when items are added, removed, or modified.
/// The collection is the authoritative source — mutations go through the collection,
/// not through the data source's CRUD methods.
/// </summary>
public class ObservableListDataSource<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T> : ListDataSource<T>, IObservableDataSource<T>, IDisposable
{
    private readonly ObservableCollection<T> _collection;
    private readonly HashSet<T> _subscribedItems = new();
    private bool _disposed;

    public ObservableListDataSource(ObservableCollection<T> collection, Func<T, RowKey> getRowKey)
        : base(new List<T>(collection), getRowKey, directReference: true)
    {
        _collection = collection;
        _collection.CollectionChanged += OnCollectionChanged;

        // Subscribe to INPC on initial items
        foreach (var item in collection)
            SubscribeItem(item);
    }

    public event Action? DataChanged;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sync internal list with the ObservableCollection
        SyncInternalList();

        // Unsubscribe removed items
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
                if (item is T typed)
                    UnsubscribeItem(typed);
        }

        // Subscribe new items
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
                if (item is T typed)
                    SubscribeItem(typed);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            UnsubscribeAll();
            foreach (var item in _collection)
                SubscribeItem(item);
        }

        DataChanged?.Invoke();
    }

    private void SyncInternalList()
    {
        // Replace internal list contents with the collection's current state
        Items.Clear();
        foreach (var item in _collection)
            Items.Add(item);
    }

    private void SubscribeItem(T item)
    {
        if (item is INotifyPropertyChanged inpc && _subscribedItems.Add(item))
            inpc.PropertyChanged += OnItemPropertyChanged;
    }

    private void UnsubscribeItem(T item)
    {
        if (item is INotifyPropertyChanged inpc && _subscribedItems.Remove(item))
            inpc.PropertyChanged -= OnItemPropertyChanged;
    }

    private void UnsubscribeAll()
    {
        foreach (var item in _subscribedItems)
            if (item is INotifyPropertyChanged inpc)
                inpc.PropertyChanged -= OnItemPropertyChanged;
        _subscribedItems.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DataChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _collection.CollectionChanged -= OnCollectionChanged;
        UnsubscribeAll();
    }
}
