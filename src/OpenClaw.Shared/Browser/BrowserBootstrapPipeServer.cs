using System.IO.Pipes;

namespace OpenClaw.Shared.Browser;

/// <summary>Current-user authenticated, single-flight bootstrap IPC with client-disconnect cancellation.</summary>
public sealed class BrowserBootstrapPipeServer(Func<byte[], CancellationToken, Task<byte[]>> handle,
    string pipeName = BrowserNativeProtocol.PipeName) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("Already started.");
        _loop = RunAsync();
    }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 16384);
                await pipe.WaitForConnectionAsync(_stop.Token);
                await HandleConnectionAsync(pipe);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // Requests, responses and exception messages may contain pairing material. Never log them.
                await Task.Delay(250, _stop.Token);
            }
        }
    }
    private async Task HandleConnectionAsync(Stream pipe)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        Task? disconnected = null;
        try
        {
            byte[] response;
            try
            {
                var request = await BrowserNativeProtocol.ReadAsync(pipe, BrowserNativeProtocol.RequestLimit, lifetime.Token);
                disconnected = WatchDisconnectAsync(pipe, lifetime);
                lifetime.Token.ThrowIfCancellationRequested();
                response = await handle(request, lifetime.Token);
            }
            catch (InvalidDataException ex) when (ex.Message is "invalid_frame" or "invalid_utf8" or "invalid_request")
            { response = BrowserNativeProtocol.Failure(ex.Message); }
            catch (Exception) when (!lifetime.IsCancellationRequested)
            { response = BrowserNativeProtocol.Failure("pairing_unavailable"); }
            lifetime.Token.ThrowIfCancellationRequested();
            await BrowserNativeProtocol.WriteAsync(pipe, response, lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            if (disconnected is not null)
                _ = disconnected.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try { var count = await pipe.ReadAsync(new byte[1], lifetime.Token); _ = count; }
        finally { await lifetime.CancelAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _stop.Dispose();
    }
}
