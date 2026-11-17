// Shared lazy-read contract and lifetime for inline or panel tool details.
// Call LoadAsync only after a reader action; Close cancels and retires that view's result.
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

public sealed record ChatToolOutputRequest(
    string SessionKey, long TimelineGeneration, long HistoryRevision, long ConnectionEpoch,
    string EntryId, string? RunId, string ToolCallId, string SessionId, string ResultMessageId,
    ChatToolDetailsProfileState Profile = default);

public enum ChatToolOutputStatus
{
    Loaded, Limited, Unsupported, NotFound, NotVisible, Oversized, Stale, Mismatched, Error,
}

public sealed record ChatToolOutputResult(ChatToolOutputStatus Status, string? Output = null);

/// <summary>Owns one view's cancellable read, not transcript state or Gateway admission.</summary>
public sealed class ChatToolOutputOperation : IDisposable
{
    private readonly object _gate = new();
    private ChatToolOutputRequest? _target;
    private Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>>? _loader;
    private CancellationTokenSource? _pending;
    private ChatToolOutputResult? _result;
    private long _version;
    private bool _disposed;

    public event Action? Changed;

    /// <summary>Returns state only for the exact current view and provider binding.</summary>
    public (bool Loading, ChatToolOutputResult? Result) Snapshot(
        ChatToolOutputRequest? target,
        Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>>? loader,
        ChatToolDetailsProfileState currentProfile)
    {
        lock (_gate)
            return currentProfile.IsAvailable && target?.Profile == currentProfile &&
                _target == target && Equals(_loader, loader) ? (_pending is not null, _result) : (false, null);
    }

    /// <summary>Retires an old selection without fetching the new one.</summary>
    public void SetTarget(ChatToolOutputRequest? target,
        Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>>? loader)
    {
        lock (_gate)
        {
            if (_disposed || _target == target && Equals(_loader, loader))
                return;
            RetireLocked();
            _target = target;
            _loader = loader;
        }
        Changed?.Invoke();
    }

    /// <summary>Starts an explicit reader action; concurrent clicks share the pending operation.</summary>
    public async Task LoadAsync(ChatToolOutputRequest requested,
        Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>> requestedLoader)
    {
        SetTarget(requested, requestedLoader);
        ChatToolOutputRequest target;
        Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>> loader;
        CancellationTokenSource cancellation;
        long version;
        lock (_gate)
        {
            if (_disposed || _pending is not null || _target != requested || !Equals(_loader, requestedLoader))
                return;
            target = requested;
            loader = requestedLoader;
            cancellation = _pending = new CancellationTokenSource();
            version = ++_version;
            _result = null;
        }
        Changed?.Invoke();
        try
        {
            ChatToolOutputResult result;
            try
            {
                result = await loader(target, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
                or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or GatewayRequestException)
            {
                Logger.Warn($"Tool output recovery failed ({ex.GetType().Name}).");
                result = new(ChatToolOutputStatus.Error);
            }
            lock (_gate)
            {
                if (!_disposed && version == _version && !cancellation.IsCancellationRequested)
                    _result = result;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cancellation))
                    _pending = null;
                cancellation.Dispose();
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Cancels a read and prevents even cancellation-ignoring completions from publishing.</summary>
    public void Close()
    {
        lock (_gate)
            RetireLocked();
        Changed?.Invoke();
    }

    private void RetireLocked()
    {
        _version++;
        _result = null;
        _pending?.Cancel();
        _pending = null;
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Close();
    }
}
