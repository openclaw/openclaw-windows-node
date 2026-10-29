using System.Collections.Immutable;

namespace OpenClawTray.Dashboards;

/// <summary>
/// Cap on live gallery preview widgets. Each live preview owns a WebView2, so
/// only a bounded number run at once; other previews show their native
/// outline until a slot frees. <see cref="Shared"/> is the process-wide pool;
/// override its limit with <c>OPENCLAW_DASHBOARD_PREVIEW_LIMIT</c>.
/// </summary>
internal sealed class BoardPreviewSlots(int limit)
{
    public const int DefaultLimit = 8;

    public static BoardPreviewSlots Shared { get; } = new(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_DASHBOARD_PREVIEW_LIMIT"), out var configured) && configured >= 0
            ? configured
            : DefaultLimit);

    private readonly HashSet<object> _holders = new(ReferenceEqualityComparer.Instance);

    public int Limit { get; } = limit;

    internal int InUse
    {
        get
        {
            lock (_holders)
                return _holders.Count;
        }
    }

    /// <summary>Raised when a slot is released so waiting previews can retry.</summary>
    public event Action? Released;

    /// <summary>Claims a slot for <paramref name="holder"/>; idempotent per holder.</summary>
    public bool TryAcquire(object holder)
    {
        lock (_holders)
        {
            if (_holders.Contains(holder))
                return true;
            if (_holders.Count >= Limit)
                return false;
            _holders.Add(holder);
            return true;
        }
    }

    public void Release(object holder)
    {
        bool removed;
        lock (_holders)
            removed = _holders.Remove(holder);
        if (removed)
            Released?.Invoke();
    }
}

/// <summary>
/// One preview's claim on <see cref="BoardPreviewSlots"/> for its live HTML
/// widgets. It retries through <c>post</c> (the UI dispatcher) when another
/// preview frees a slot, and a retry that runs after <see cref="Dispose"/>
/// claims nothing, so closing the gallery never strands slots.
/// </summary>
internal sealed class BoardPreviewSlotLease : IDisposable
{
    private readonly BoardPreviewSlots _slots;
    private readonly ImmutableArray<string> _names;
    private readonly Action<Action> _post;
    private readonly Action<ImmutableHashSet<string>> _granted;
    private readonly Dictionary<string, object> _holders = new(StringComparer.Ordinal);
    private bool _disposed;

    public BoardPreviewSlotLease(
        BoardPreviewSlots slots,
        ImmutableArray<string> names,
        Action<Action> post,
        Action<ImmutableHashSet<string>> granted)
    {
        _slots = slots;
        _names = names;
        _post = post;
        _granted = granted;
        _slots.Released += OnReleased;
        Acquire();
    }

    private void OnReleased() => _post(Acquire);

    private void Acquire()
    {
        if (_disposed)
            return;
        var granted = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var name in _names)
        {
            if (!_holders.TryGetValue(name, out var holder))
                _holders[name] = holder = new object();
            if (_slots.TryAcquire(holder))
                granted.Add(name);
        }
        _granted(granted.ToImmutable());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _slots.Released -= OnReleased;
        foreach (var holder in _holders.Values)
            _slots.Release(holder);
        _holders.Clear();
    }
}