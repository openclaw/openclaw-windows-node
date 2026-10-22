using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal enum ChatConversationAccountStatus
{
    Waiting,
    Loading,
    Known,
    Profileless,
    Unavailable,
    Failed,
    RequiresReconnect
}

internal sealed record ChatConversationAccountFact(string UserId, long ConnectionEpoch, long Revision);

/// <summary>
/// Current authenticated users.self fact for one binding. Display identities and
/// signing devices are never substitutes for the Gateway's canonical profile ID.
/// </summary>
internal sealed class ChatConversationAccountIdentity(
    IOperatorGatewayClient client,
    Func<bool> isCurrentClient,
    Func<bool> isAdvertised) : IDisposable
{
    private readonly object _gate = new();
    private long? _epoch;
    private long _revision;
    private ChatConversationAccountStatus _status;
    private ChatConversationAccountFact? _fact;
    private TaskCompletionSource? _pending;
    private bool _disposed;
    private string? _knownUserId;
    private long? _retiredEpoch;

    internal event Action? Changed;

    internal ChatConversationAccountStatus Status
    {
        get
        {
            lock (_gate)
                return !_disposed && _retiredEpoch.HasValue ? ChatConversationAccountStatus.RequiresReconnect
                    : IsCurrentConnection() ? _status : ChatConversationAccountStatus.Waiting;
        }
    }

    internal ChatConversationAccountFact? Current
    {
        get
        {
            lock (_gate)
                return IsCurrentConnection() && _status == ChatConversationAccountStatus.Known ? _fact : null;
        }
    }

    internal Task RefreshAsync(bool invalidate = false)
    {
        TaskCompletionSource? pending = null;
        bool changed;
        long epoch = 0;
        long revision = 0;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var readyEpoch = isCurrentClient() && client.IsConnectedToGateway
                ? client.SessionMutationConnectionEpoch : null;
            changed = _epoch != readyEpoch || invalidate;
            if (changed)
            {
                if (_epoch != readyEpoch)
                {
                    _knownUserId = null;
                    if (readyEpoch.HasValue && readyEpoch != _retiredEpoch) _retiredEpoch = null;
                }
                _epoch = readyEpoch;
                _revision++;
                _fact = null;
                _pending = null;
                _status = readyEpoch.HasValue
                    ? ChatConversationAccountStatus.Loading : ChatConversationAccountStatus.Waiting;
            }
            if (readyEpoch.HasValue && _retiredEpoch == readyEpoch)
            {
                _status = ChatConversationAccountStatus.RequiresReconnect;
                changed = true;
            }
            if (readyEpoch.HasValue && _status == ChatConversationAccountStatus.Loading)
            {
                if (_pending is not null) return _pending.Task;
                if (!isAdvertised() || !CanReadSelf(client.GrantedOperatorScopes))
                {
                    _status = ChatConversationAccountStatus.Unavailable;
                    changed = true;
                }
                else
                {
                    pending = _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    epoch = readyEpoch.Value;
                    revision = _revision;
                }
            }
        }
        if (changed) Changed?.Invoke();
        if (pending is null) return Task.CompletedTask;

        var completion = pending;
        _ = ReadAsync(epoch, revision).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Complete(epoch, revision, ChatConversationAccountStatus.Failed);
                Logger.Warn($"[ChatAccount] Unexpected profile lookup failure ({task.Exception!.GetBaseException().GetType().Name}).");
                completion.TrySetException(task.Exception.InnerExceptions);
            }
            else if (task.IsCanceled) completion.TrySetCanceled();
            else completion.TrySetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private async Task ReadAsync(long epoch, long revision)
    {
        try
        {
            // This is read-only. The original epoch and profile revision are
            // checked before dispatch and again before accepting the response.
            lock (_gate)
                if (!IsCurrent(epoch, revision)) return;
            var response = await client.SendWizardRequestAsync("users.self", new { }).ConfigureAwait(false);
            if (response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("profile", out var profile))
                throw new JsonException("users.self did not return a profile field.");
            if (profile.ValueKind == JsonValueKind.Null)
            {
                Complete(epoch, revision, ChatConversationAccountStatus.Profileless);
                return;
            }
            if (profile.ValueKind != JsonValueKind.Object ||
                !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
                throw new JsonException("users.self did not return a canonical profile ID.");
            Complete(epoch, revision, ChatConversationAccountStatus.Known, id.GetString());
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase))
        {
            if (Complete(epoch, revision, ChatConversationAccountStatus.Unavailable))
                Logger.Warn("[ChatAccount] users.self is unavailable on this Gateway.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
            OperationCanceledException or JsonException)
        {
            if (Complete(epoch, revision, ChatConversationAccountStatus.Failed))
                Logger.Warn($"[ChatAccount] Profile lookup failed ({ex.GetType().Name}).");
        }
    }

    private bool Complete(long epoch, long revision, ChatConversationAccountStatus status, string? userId = null)
    {
        lock (_gate)
        {
            if (!IsCurrent(epoch, revision)) return false;
            if (status == ChatConversationAccountStatus.Known &&
                (_retiredEpoch == epoch || _knownUserId is not null && _knownUserId != userId))
            {
                // Existing provider snapshots have no account provenance.
                // Do not attach them to another account on the same socket.
                _retiredEpoch = epoch;
                _status = ChatConversationAccountStatus.RequiresReconnect;
                _fact = null;
            }
            else
            {
                _status = status;
                _fact = status == ChatConversationAccountStatus.Known
                    ? new(userId!, epoch, revision) : null;
                if (status == ChatConversationAccountStatus.Known) _knownUserId = userId;
            }
        }
        Changed?.Invoke();
        return true;
    }

    private bool IsCurrentConnection() => !_disposed && _epoch.HasValue &&
        isCurrentClient() && client.IsConnectedToGateway && client.SessionMutationConnectionEpoch == _epoch;

    private bool IsCurrent(long epoch, long revision) =>
        IsCurrentConnection() && _epoch == epoch && _revision == revision;

    private static bool CanReadSelf(IReadOnlyList<string> scopes) => scopes.Any(scope => scope is
        "operator.admin" or "operator.read" or "operator.write" or
        "operator.sessions.read" or "operator.sessions.write");

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _revision++;
            _fact = null;
            _pending = null;
            Changed = null;
        }
    }
}
