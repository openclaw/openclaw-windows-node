namespace OpenClaw.Shared.Browser;

/// <summary>One request is settled only after its server handler closes the connection.</summary>
public static class BrowserBootstrapPipeClient
{
    public static async Task<byte[]> ExchangeAsync(Stream pipe, byte[] request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completeRequest = false;
        try
        {
            await BrowserNativeProtocol.WriteAsync(pipe, request, ct);
            completeRequest = true;
            var response = await BrowserNativeProtocol.ReadAsync(pipe, BrowserNativeProtocol.ResponseLimit, ct);
            if (await pipe.ReadAsync(new byte[1], ct) != 0) throw new InvalidDataException("Unexpected extra response");
            ct.ThrowIfCancellationRequested();
            return response;
        }
        catch
        {
            // The existing server treats extra input after a frame as cancellation. Keep
            // the connection open so its EOF acknowledges that the handler has settled.
            // The server owns its existing request deadline and handler cleanup. A client
            // timer cannot grant retirement while that handler is still running. An
            // unsettled peer keeps activation held (management remains busy), not successful.
            // No new timeout or force-unlock substitutes for observing its closure.
            try
            {
                await pipe.WriteAsync(new byte[] { 0 }, CancellationToken.None);
                await pipe.FlushAsync(CancellationToken.None);
                var buffer = new byte[4096];
                // The response cap above still rejects invalid responses. Discarding
                // invalid/late bytes uses fixed memory and must not release ownership
                // before EOF merely because the rejected stream is oversized.
                while (await pipe.ReadAsync(buffer, CancellationToken.None) != 0) { }
            }
            catch (Exception error)
            {
                // Never turn a partial send or unconfirmed cleanup into successful work.
                throw new IOException("Pipe settlement was not confirmed", error);
            }
            if (!completeRequest) throw new IOException("Pipe request completion was not confirmed");
            throw;
        }
    }
}
