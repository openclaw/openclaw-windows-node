namespace OpenClaw.SetupEngine;

/// <summary>One AI page owns the artwork cache and cancels all its controls before closing.</summary>
public sealed class ProviderArtworkSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    internal ProviderArtworkLoader Loader { get; } = new();
    internal CancellationToken Token => _lifetime.Token;
    internal event Action? Closed;

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _lifetime.Cancel();
        try { Closed?.Invoke(); }
        finally
        {
            Closed = null;
            Loader.Dispose();
        }
    }
}

/// <summary>UI-thread owned. Every rebind and unload invalidates earlier completions.</summary>
internal sealed class ProviderArtworkGeneration
{
    private CancellationTokenSource? _request;
    private int _generation;

    internal (int Generation, CancellationToken Token) Begin(CancellationToken lifetime)
    {
        Stop();
        _request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _request.CancelAfter(TimeSpan.FromSeconds(10));
        return (_generation, _request.Token);
    }

    internal bool IsCurrent(int generation) =>
        generation == _generation && _request is { IsCancellationRequested: false };

    internal bool Matches(int generation) => generation == _generation && _request is not null;

    internal void Stop()
    {
        ++_generation;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }
}
