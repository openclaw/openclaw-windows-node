using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayHeartbeatDiagnosticsTests
{
    public class HeartbeatProxy : DispatchProxy
    {
        public long? Epoch = 1;
        public int Calls;
        public CancellationToken Token;
        public Func<Task<GatewayHeartbeatEvent?>> Response = () => Task.FromResult<GatewayHeartbeatEvent?>(null);
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == "get_SessionMutationConnectionEpoch") return Epoch;
            if (method?.Name == "GetLastHeartbeatAsync")
            {
                Assert.Equal(Epoch, arguments![0]);
                Token = (CancellationToken)arguments[1]!;
                Calls++;
                return Response();
            }
            throw new NotSupportedException(method?.Name);
        }
    }

    private static (IOperatorGatewayClient Client, HeartbeatProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, HeartbeatProxy>();
        return (client, (HeartbeatProxy)client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedAndNullAreDistinct_RefreshDoesNotRunOnActivation(bool empty)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult<GatewayHeartbeatEvent?>(empty ? null :
            new GatewayHeartbeatEvent { Timestamp = 1791257718360, Status = "failed", Preview = "not presentation data" });
        using var owner = new GatewayHeartbeatDiagnostics(() => client);
        owner.Activate();
        Assert.Equal(GatewayHeartbeatReadState.Ready, owner.State);
        Assert.Equal(0, proxy.Calls);
        await owner.RefreshAsync();
        Assert.Equal(empty ? GatewayHeartbeatReadState.Empty : GatewayHeartbeatReadState.Recorded, owner.State);
        Assert.Equal(empty, owner.Heartbeat is null);
        Assert.Equal(1, proxy.Calls);
    }

    [Theory]
    [InlineData("unsupported", GatewayHeartbeatReadState.Unsupported)]
    [InlineData("denied", GatewayHeartbeatReadState.Denied)]
    [InlineData("error", GatewayHeartbeatReadState.Failed)]
    [InlineData("timeout", GatewayHeartbeatReadState.Failed)]
    [InlineData("malformed", GatewayHeartbeatReadState.Failed)]
    [InlineData("transport", GatewayHeartbeatReadState.Failed)]
    public async Task ReadFailuresAreNotNoRecord(string failure, object expected)
    {
        var (client, proxy) = Gateway();
        Exception error = failure switch
        {
            "unsupported" => new NotSupportedException(),
            "denied" => new UnauthorizedAccessException(),
            "timeout" => new TimeoutException(),
            "malformed" => new JsonException(),
            "transport" => new System.Net.WebSockets.WebSocketException(),
            _ => new InvalidOperationException()
        };
        proxy.Response = () => Task.FromException<GatewayHeartbeatEvent?>(error);
        using var owner = new GatewayHeartbeatDiagnostics(() => client);
        owner.Activate();
        await owner.RefreshAsync();
        Assert.Equal((GatewayHeartbeatReadState)expected, owner.State);
        Assert.Null(owner.Heartbeat);
    }

    [Theory]
    [InlineData("replace", false)]
    [InlineData("reconnect", false)]
    [InlineData("disconnect", false)]
    [InlineData("close", false)]
    [InlineData("replace", true)]
    [InlineData("reconnect", true)]
    [InlineData("disconnect", true)]
    [InlineData("close", true)]
    public async Task DelayedSuccessAndErrorCannotPaintChangedOwner(string change, bool error)
    {
        var (client, proxy) = Gateway();
        IOperatorGatewayClient? current = client;
        var response = new TaskCompletionSource<GatewayHeartbeatEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => response.Task;
        using var owner = new GatewayHeartbeatDiagnostics(() => current);
        owner.Activate();
        var pending = owner.RefreshAsync();
        await owner.RefreshAsync();
        Assert.Equal(1, proxy.Calls);
        Assert.Equal(GatewayHeartbeatReadState.Loading, owner.State);
        if (change == "replace") current = Gateway().Client;
        if (change == "reconnect") proxy.Epoch++;
        if (change == "disconnect") proxy.Epoch = null;
        if (change == "close") owner.Dispose();
        else owner.Synchronize();
        Assert.True(proxy.Token.IsCancellationRequested);
        if (error) response.SetException(new InvalidOperationException("old Gateway"));
        else response.SetResult(new GatewayHeartbeatEvent { Timestamp = 1791257718360, Status = "sent" });
        await pending;
        Assert.Null(owner.Heartbeat);
        Assert.Equal(change is "close" or "disconnect" ? GatewayHeartbeatReadState.Disconnected : GatewayHeartbeatReadState.Ready, owner.State);
        Assert.Equal(1, proxy.Calls);
    }

    [Theory]
    [InlineData("sent", "Heartbeat_OutcomeSent")]
    [InlineData("ok-empty", "Heartbeat_OutcomeEmpty")]
    [InlineData("ok-token", "Heartbeat_OutcomeToken")]
    [InlineData("skipped", "Heartbeat_OutcomeSkipped")]
    [InlineData("failed", "Heartbeat_OutcomeFailed")]
    [InlineData("future-status", "Heartbeat_OutcomeUnknown")]
    public void OutcomeProjectionDoesNotTreatUnknownAsSuccess(string status, string key)
        => Assert.Equal(key, GatewayHeartbeatDiagnostics.OutcomeResourceKey(status));

    [Fact]
    public async Task ReplacementClearsRecordedState_AndOldReadCannotClearNewResult()
    {
        var (first, oldProxy) = Gateway();
        var (second, newProxy) = Gateway();
        IOperatorGatewayClient current = first;
        using var owner = new GatewayHeartbeatDiagnostics(() => current);
        owner.Activate();
        oldProxy.Response = () => Task.FromResult<GatewayHeartbeatEvent?>(
            new GatewayHeartbeatEvent { Timestamp = 1, Status = "sent" });
        await owner.RefreshAsync();
        Assert.Equal(GatewayHeartbeatReadState.Recorded, owner.State);
        var oldResponse = new TaskCompletionSource<GatewayHeartbeatEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldProxy.Response = () => oldResponse.Task;
        var oldRead = owner.RefreshAsync();
        current = second;
        owner.Synchronize();
        Assert.Null(owner.Heartbeat);
        Assert.Equal(GatewayHeartbeatReadState.Ready, owner.State);
        newProxy.Response = () => Task.FromResult<GatewayHeartbeatEvent?>(
            new GatewayHeartbeatEvent { Timestamp = 2, Status = "skipped" });
        await owner.RefreshAsync();
        oldResponse.SetResult(new GatewayHeartbeatEvent { Timestamp = 1, Status = "sent" });
        await oldRead;
        Assert.Equal(GatewayHeartbeatReadState.Recorded, owner.State);
        Assert.Equal(2, owner.Heartbeat!.Timestamp);
        Assert.Equal("skipped", owner.Heartbeat.Status);
    }
}
