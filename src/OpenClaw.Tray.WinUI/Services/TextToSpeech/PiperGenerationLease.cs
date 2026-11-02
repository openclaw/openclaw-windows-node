using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Serializes one Piper generation with the native release that follows it.
/// <see cref="Dispose"/> returns as soon as that release is queued. A voice
/// change on the UI thread can request release while <see cref="RunAsync"/>
/// still holds the gate. Native cleanup runs after that generation returns.
/// The semaphore stays alive so a generation that already queued can observe
/// the request and leave.
/// </summary>
internal sealed class PiperGenerationLease : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action _releaseNative;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _disposeRequested;

    internal PiperGenerationLease(Action releaseNative)
    {
        _releaseNative = releaseNative;
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, T> generate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            return await Task.Run(() => generate(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _ = DisposeAsync();

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= ReleaseAfterGenerationAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task ReleaseAfterGenerationAsync()
    {
        Volatile.Write(ref _disposeRequested, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _releaseNative();
        }
        finally
        {
            _gate.Release();
        }
    }
}
