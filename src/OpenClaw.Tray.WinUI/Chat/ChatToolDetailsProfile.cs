// Revalidates the authenticated profile for the read-only Details view.
// Refresh after connection/profile events; reuse the current connection for users.self.
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>Opaque reader admission. Default is unverified; any revalidation retires the previous revision.</summary>
public readonly record struct ChatToolDetailsProfileState(long Revision, bool IsAvailable);

/// <summary>Same-profile edits recover automatically; an account replacement retires cached details.</summary>
internal sealed class ChatToolDetailsProfile(
    IOperatorGatewayClient client, Action changed, Action<string> reportFailure) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _attempt;
    private long _revision;
    private long? _epoch;
    private string? _device;
    private string? _profileId;
    private bool _available;
    private bool _retired;
    private bool _disposed;

    internal ChatToolDetailsProfileState Snapshot
    {
        get
        {
            var epoch = client.SessionMutationConnectionEpoch;
            var device = client.AuthenticatedSigningDeviceId;
            var connected = client.IsConnectedToGateway;
            lock (_gate)
                return new(_revision,
                    !_disposed && _available && connected && epoch == _epoch && device == _device);
        }
    }
    internal bool IsAvailable => Snapshot.IsAvailable;

    /// <summary>Reads identity only. It never refreshes transcript data or grants mutation authority.</summary>
    internal async Task RefreshAsync()
    {
        var epoch = client.IsConnectedToGateway ? client.SessionMutationConnectionEpoch : null;
        var device = client.AuthenticatedSigningDeviceId;
        CancellationTokenSource? previous;
        CancellationTokenSource? attempt;
        long revision;
        lock (_gate)
        {
            if (_disposed) return;
            if (_epoch != epoch)
            {
                _profileId = null;
                _retired = false;
            }
            else if (_device is not null && _device != device)
            {
                _retired = true;
            }
            _epoch = epoch;
            _device = device;
            _available = false;
            revision = ++_revision;
            previous = _attempt;
            attempt = epoch.HasValue && !string.IsNullOrWhiteSpace(device) && !_retired
                ? new CancellationTokenSource() : null;
            _attempt = attempt;
        }
        Cancel(previous);
        changed();
        if (attempt is null) return;

        Task<JsonElement>? read = null;
        try
        {
            read = client.SendWizardRequestOnConnectionAsync(
                "users.self", new { }, epoch!.Value, WorkspaceIdentitySource.UserSelfTimeoutMs());
            var response = await read.WaitAsync(attempt.Token).ConfigureAwait(false);
            var profileId = WorkspaceIdentitySource.ReadProfile(response).ProfileId;
            var transportCurrent = client.IsConnectedToGateway &&
                client.SessionMutationConnectionEpoch == epoch && client.AuthenticatedSigningDeviceId == device;
            lock (_gate)
            {
                if (_disposed || revision != _revision || !transportCurrent) return;
                if (_profileId is not null && _profileId != profileId) _retired = true;
                _profileId ??= profileId;
                _available = !_retired;
            }
            changed();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
            OperationCanceledException or JsonException or NotSupportedException)
        {
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
                _available = false;
            }
            reportFailure(WorkspaceIdentitySource.Classify(ex).ToString());
            changed();
        }
        finally
        {
            // The Gateway's request owns its deadline; cancellation retires only this consumer.
            if (read is not null)
                _ = read.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            lock (_gate)
                if (ReferenceEquals(_attempt, attempt)) _attempt = null;
            attempt.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource? attempt)
    {
        try { attempt?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _available = false;
            _revision++;
            attempt = _attempt;
            _attempt = null;
        }
        Cancel(attempt);
    }
}
