using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum GatewayHeartbeatReadState { Ready, Disconnected, Loading, Empty, Recorded, Unsupported, Denied, Failed }

/// <summary>Page-owned read lifetime. Replacement clients and authenticated epochs cannot adopt an old result.</summary>
internal sealed class GatewayHeartbeatDiagnostics(Func<IOperatorGatewayClient?> currentClient) : IDisposable
{
    private IOperatorGatewayClient? _client;
    private long? _epoch;
    private CancellationTokenSource? _request;
    private bool _active;

    public GatewayHeartbeatReadState State { get; private set; } = GatewayHeartbeatReadState.Disconnected;
    public GatewayHeartbeatEvent? Heartbeat { get; private set; }
    public event EventHandler? Changed;

    public void Activate()
    {
        _active = true;
        Synchronize();
    }

    public void Synchronize()
    {
        if (!_active) return;
        var client = currentClient();
        var epoch = client?.SessionMutationConnectionEpoch;
        if (ReferenceEquals(_client, client) && _epoch == epoch) return;
        CancelRequest();
        _client = client;
        _epoch = epoch;
        SetState(epoch.HasValue ? GatewayHeartbeatReadState.Ready : GatewayHeartbeatReadState.Disconnected);
    }

    public async Task RefreshAsync()
    {
        if (!_active) return;
        Synchronize();
        if (_client is null || !_epoch.HasValue || _request is not null) return;
        var client = _client;
        var epoch = _epoch.Value;
        using var request = new CancellationTokenSource();
        _request = request;
        SetState(GatewayHeartbeatReadState.Loading);
        bool IsCurrent() => _active && !request.IsCancellationRequested &&
            ReferenceEquals(client, currentClient()) && client.SessionMutationConnectionEpoch == epoch;
        try
        {
            var heartbeat = await client.GetLastHeartbeatAsync(epoch, request.Token);
            if (IsCurrent())
            {
                Heartbeat = heartbeat;
                State = heartbeat is null ? GatewayHeartbeatReadState.Empty : GatewayHeartbeatReadState.Recorded;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (NotSupportedException) { if (IsCurrent()) SetState(GatewayHeartbeatReadState.Unsupported); }
        catch (UnauthorizedAccessException) { if (IsCurrent()) SetState(GatewayHeartbeatReadState.Denied); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            if (IsCurrent()) SetState(GatewayHeartbeatReadState.Failed);
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            Synchronize();
        }
    }

    public static string OutcomeResourceKey(string status) => status switch
    {
        "sent" => "Heartbeat_OutcomeSent",
        "ok-empty" => "Heartbeat_OutcomeEmpty",
        "ok-token" => "Heartbeat_OutcomeToken",
        "skipped" => "Heartbeat_OutcomeSkipped",
        "failed" => "Heartbeat_OutcomeFailed",
        _ => "Heartbeat_OutcomeUnknown"
    };

    private void SetState(GatewayHeartbeatReadState state)
    {
        Heartbeat = null;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request = null;
    }

    public void Dispose()
    {
        _active = false;
        CancelRequest();
        _client = null;
        _epoch = null;
        SetState(GatewayHeartbeatReadState.Disconnected);
    }
}
