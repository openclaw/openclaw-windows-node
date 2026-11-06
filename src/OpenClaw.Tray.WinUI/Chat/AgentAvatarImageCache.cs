using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Loads chat avatar images through one bounded source for a mounted chat tree.
/// Pass the source to avatar badges so repeated assistant rows share in-flight
/// and completed image loads; dispose it when the chat tree is unmounted.
/// </summary>
public interface IChatAgentAvatarImageSource
{
    long Revision { get; }

    Task<ImageSource?> LoadImageAsync(string source, CancellationToken cancellationToken);
}

internal sealed class AgentAvatarImageCache : IChatAgentAvatarImageSource, IDisposable
{
    internal const int MaximumEntries = 4;

    private sealed record Entry(string Source, Task<ImageSource?> Load);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _leastRecentlyUsed = [];
    private readonly MediaResolver? _resolver;
    private readonly Func<string, CancellationToken, Task<ImageSource?>> _load;
    private readonly CancellationTokenSource _lifetime = new();
    private long _revision;
    private bool _disposed;

    internal AgentAvatarImageCache()
        : this(new MediaResolver(new AppLogger()), null)
    {
    }

    internal AgentAvatarImageCache(Func<string, CancellationToken, Task<ImageSource?>> load)
        : this(null, load)
    {
    }

    private AgentAvatarImageCache(
        MediaResolver? resolver,
        Func<string, CancellationToken, Task<ImageSource?>>? load)
    {
        _resolver = resolver;
        _load = load ?? LoadWithResolver;
    }

    public long Revision => Interlocked.Read(ref _revision);

    public Task<ImageSource?> LoadImageAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Task<ImageSource?> load;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(source, out var existing))
            {
                _leastRecentlyUsed.Remove(existing);
                _leastRecentlyUsed.AddFirst(existing);
                load = existing.Value.Load;
            }
            else
            {
                if (_resolver is not null &&
                    Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                {
                    _resolver.AllowHost(uri.Host);
                }

                load = _load(source, _lifetime.Token);
                var node = _leastRecentlyUsed.AddFirst(new Entry(source, load));
                _entries.Add(source, node);
                if (_entries.Count > MaximumEntries)
                {
                    var oldest = _leastRecentlyUsed.Last!;
                    _leastRecentlyUsed.RemoveLast();
                    _entries.Remove(oldest.Value.Source);
                }
            }
        }

        return load.WaitAsync(cancellationToken);
    }

    public void Clear()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.Clear();
            _leastRecentlyUsed.Clear();
            Interlocked.Increment(ref _revision);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _entries.Clear();
            _leastRecentlyUsed.Clear();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _resolver?.Dispose();
    }

    private Task<ImageSource?> LoadWithResolver(string source, CancellationToken cancellationToken) =>
        _resolver!.LoadImageAsync(source, cancellationToken);
}
