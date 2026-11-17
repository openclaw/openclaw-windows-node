namespace OpenClaw.Shared.Mxc;

/// <summary>A detached, committed Sandbox policy. It is not a second settings store.</summary>
public sealed class NodeSandboxPolicy
{
    private readonly SettingsData _settings;
    internal NodeSandboxPolicy(long revision, SettingsData settings)
    {
        Revision = revision;
        _settings = MxcRequestBuilder.Snapshot(settings);
        Fingerprint = MxcRequestBuilder.Fingerprint(_settings);
    }

    public long Revision { get; }
    public string Fingerprint { get; }
    internal SettingsData Settings => MxcRequestBuilder.Snapshot(_settings);
}

/// <summary>
/// Settings persistence and final launch submission share an ordered dispatcher.
/// No native/process API or external callback runs under the queue lock.
/// </summary>
public sealed class NodeSandboxPolicyStore
{
    private readonly object _gate = new();
    private NodeSandboxPolicy _current;
    private readonly Func<SettingsData>? _legacyProvider;
    private readonly Queue<Func<Task>> _pending = new();
    private bool _dispatching;

    public NodeSandboxPolicyStore(SettingsData initial) => _current = new(0, initial);

    // Compatibility for standalone command runners. The application always supplies the
    // SettingsManager-owned committed store, never a live settings callback.
    internal NodeSandboxPolicyStore(Func<SettingsData> provider) : this(provider()) => _legacyProvider = provider;

    public NodeSandboxPolicy Capture()
    {
        RefreshLegacyProvider();
        lock (_gate) return _current;
    }

    /// <summary>
    /// Atomically replace persisted settings, then publish the exact detached snapshot.
    /// The persistence callback must not notify observers or reenter this store.
    /// </summary>
    public void Persist(SettingsData snapshot, Action persist)
    {
        var detached = MxcRequestBuilder.Snapshot(snapshot);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                persist();
                lock (_gate) Publish(detached);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        completion.Task.GetAwaiter().GetResult();
    }

    private void Publish(SettingsData settings)
    {
        var next = new NodeSandboxPolicy(checked(_current.Revision + 1), settings);
        if (next.Fingerprint != _current.Fingerprint) _current = next;
    }

    private void RefreshLegacyProvider()
    {
        if (_legacyProvider is null) return;
        var settings = _legacyProvider();
        lock (_gate)
            if (MxcRequestBuilder.Fingerprint(settings) == _current.Fingerprint) return;
        Persist(settings, () => { });
    }

    internal Task<T> Submit<T>(NodeSandboxPolicy prepared, Func<Task<T>> launch, CancellationToken stop,
        Func<ValueTask>? revalidate = null)
    {
        RefreshLegacyProvider();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                stop.ThrowIfCancellationRequested();
                if (revalidate is not null) await revalidate().ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
                lock (_gate)
                    if (!ReferenceEquals(prepared, _current))
                        throw new StaleNodeSandboxPolicyException();
                // Invoke the synchronous launch prefix before the next commit can run.
                // A synchronously blocking SDK start holds this dispatcher, never a lock.
                // The returned task remains owned through late launch and cleanup.
                var owned = launch();
                _ = CompleteAsync(owned);
            }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;

        async Task CompleteAsync(Task<T> owned)
        {
            try { completion.TrySetResult(await owned.ConfigureAwait(false)); }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
    }

    private void Enqueue(Action action)
        => Enqueue(() => { action(); return Task.CompletedTask; });

    private void Enqueue(Func<Task> action)
    {
        lock (_gate)
        {
            _pending.Enqueue(action);
            if (_dispatching) return;
            _dispatching = true;
        }
        _ = Task.Factory.StartNew(DispatchAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task DispatchAsync()
    {
        while (true)
        {
            Func<Task> next;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out next!))
                {
                    _dispatching = false;
                    return;
                }
            }
            await next().ConfigureAwait(false);
        }
    }
}

internal sealed class StaleNodeSandboxPolicyException : Exception;
