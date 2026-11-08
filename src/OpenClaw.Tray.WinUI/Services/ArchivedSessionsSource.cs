using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// UI-thread-owned snapshot of the gateway's archived session list
/// (<c>sessions.list</c> with <c>archived:true</c>). Collapses concurrent
/// refreshes like <see cref="WorkspaceIdentitySource"/>: one load in flight,
/// later requests set a re-run flag, and stale results are dropped by
/// generation. Continuations stay on the UI thread on purpose.
/// </summary>
internal sealed class ArchivedSessionsSource(Action changed, Action<Exception> reportFailure)
{
    private int _generation;
    private Task? _pending;
    private bool _refreshAgain;
    private IOperatorGatewayClient? _client;
    private long? _connectionEpoch;

    public bool IsExpanded { get; private set; }
    public bool HasLoaded { get; private set; }
    public bool IsUnavailable { get; private set; }
    public IReadOnlyList<SessionInfo> Sessions { get; private set; } = [];

    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        changed();
    }

    /// <summary>Forgets the snapshot on disconnect or page unload.</summary>
    public void Clear()
    {
        _generation++;
        _client = null;
        _connectionEpoch = null;
        _pending = null;
        _refreshAgain = false;
        Sessions = [];
        HasLoaded = false;
        IsUnavailable = false;
        changed();
    }

    public Task RefreshAsync(IOperatorGatewayClient? client)
    {
        if (!IsExpanded || client is not { IsConnectedToGateway: true })
            return Task.CompletedTask;

        if (!ReferenceEquals(client, _client) || client.SessionMutationConnectionEpoch != _connectionEpoch)
        {
            Clear();
            _client = client;
            _connectionEpoch = client.SessionMutationConnectionEpoch;
        }

        if (_pending is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _pending;
        }

        return _pending = LoadAsync(client, _generation, _connectionEpoch);
    }

    private async Task LoadAsync(IOperatorGatewayClient client, int generation, long? connectionEpoch)
    {
        do
        {
            _refreshAgain = false;
            try
            {
                var result = await client.ListArchivedSessionsAsync();
                if (!IsCurrent()) return;
                if (_refreshAgain) continue;
                Sessions = result.Sessions;
                IsUnavailable = !result.IsSupported;
                HasLoaded = true;
                changed();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                OperationCanceledException)
            {
                if (!IsCurrent()) return;
                if (_refreshAgain) continue;
                Sessions = [];
                IsUnavailable = true;
                HasLoaded = true;
                reportFailure(ex);
                changed();
                return;
            }
        } while (IsCurrent() && _refreshAgain);

        bool IsCurrent() => generation == _generation &&
            ReferenceEquals(client, _client) && client.IsConnectedToGateway &&
            client.SessionMutationConnectionEpoch == connectionEpoch;
    }
}
