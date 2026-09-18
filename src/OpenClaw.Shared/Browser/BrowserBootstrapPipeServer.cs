using System.IO.Pipes;

namespace OpenClaw.Shared.Browser;

/// <summary>Current-user authenticated, single-flight bootstrap IPC. No port, token file, or gateway transport.</summary>
public sealed class BrowserBootstrapPipeServer(
    Func<byte[], CancellationToken, Task<byte[]>> handle,
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
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    4096, 16384);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                byte[] response;
                try
                {
                    var request = await BrowserNativeProtocol.ReadAsync(pipe, BrowserNativeProtocol.RequestLimit, deadline.Token);
                    response = await handle(request, deadline.Token);
                }
                catch (InvalidDataException ex) when (ex.Message is "invalid_frame" or "invalid_utf8" or "invalid_request")
                {
                    response = BrowserNativeProtocol.Failure(ex.Message);
                }
                catch (Exception) when (!_stop.IsCancellationRequested)
                {
                    response = BrowserNativeProtocol.Failure("pairing_unavailable");
                }
                await BrowserNativeProtocol.WriteAsync(pipe, response, deadline.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // Do not log requests, responses, or exception messages containing pairing material.
                await Task.Delay(250, _stop.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
