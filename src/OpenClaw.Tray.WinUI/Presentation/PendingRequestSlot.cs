using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Presentation;

/// <summary>
/// Holds one one-shot request until its destination renders. Setting a new request or clearing the
/// slot cancels the token handed out for the previous request, so a waiter for a superseded request
/// fails promptly and a later visit to the same destination can never consume it. Consuming the
/// request deliberately does not cancel it: its waiters keep waiting for the render itself.
/// </summary>
internal sealed class PendingRequestSlot<T> where T : class
{
    private T? _request;
    private CancellationTokenSource? _superseded;

    public T? Current => _request;

    /// <summary>Stores the request and returns a token cancelled when it is no longer pending.</summary>
    public CancellationToken Set(T request)
    {
        Clear();
        _request = request;
        _superseded = new CancellationTokenSource();
        return _superseded.Token;
    }

    /// <summary>Returns the pending token only while <paramref name="request"/> is still pending.</summary>
    public CancellationToken? TokenFor(T request) =>
        ReferenceEquals(_request, request) && _superseded is { } cts ? cts.Token : null;

    /// <summary>
    /// Waits for <paramref name="rendered"/> while <paramref name="request"/> stays pending. A
    /// superseded request throws <see cref="InvalidOperationException"/>. Cancellation happens in the
    /// middle of a render and the awaiter may resume inline inside <see cref="Clear"/>; the waiter
    /// (whose caller may open recovery UI) therefore yields before it reports, and never re-enters
    /// that render.
    /// </summary>
    public async Task WaitForRenderAsync(T request, Task rendered, CancellationToken ct)
    {
        if (TokenFor(request) is not { } superseded)
            throw new InvalidOperationException("The selected setup destination changed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, superseded);
        try
        {
            await rendered.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            await Task.Yield();
            if (!ct.IsCancellationRequested && superseded.IsCancellationRequested)
                throw new InvalidOperationException("The selected setup destination changed.");
            throw;
        }
    }

    /// <summary>Takes the request for rendering; its waiters keep waiting on the render itself.</summary>
    public T? Consume()
    {
        var request = _request;
        _request = null;
        _superseded = null;
        return request;
    }

    public void Clear()
    {
        var cts = _superseded;
        _request = null;
        _superseded = null;
        cts?.Cancel();
    }
}
