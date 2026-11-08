using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspaceSessionOperationTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 1;
        public bool Connected = true;
        public int Calls;
        public Func<Task> Response = () => Task.CompletedTask;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_SessionMutationConnectionEpoch" => (long?)Epoch,
            "get_IsConnectedToGateway" => Connected,
            "PatchSessionConfirmedAsync" or "DeleteSessionConfirmedAsync" => Send(),
            _ => throw new NotSupportedException(targetMethod?.Name)
        };

        private Task Send()
        {
            Calls++;
            return Response();
        }
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    private static Task Mutate(WorkspaceSessionOperation operation, bool delete) =>
        delete ? operation.DeleteAsync("same-key") : operation.PatchAsync("same-key", new SessionPatch { Label = "renamed" });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedAcceptance_DoesNotCompleteBeforeResponse(bool delete)
    {
        var (client, proxy) = Gateway();
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => response.Task;
        var operation = new WorkspaceSessionOperation(client, () => client);
        var task = Mutate(operation, delete);
        Assert.False(task.IsCompleted);
        response.SetResult();
        await task;
        Assert.Equal(1, proxy.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RejectionOrTimeout_PropagatesWithoutAcceptance(bool delete, bool timeout)
    {
        var (client, proxy) = Gateway();
        Exception failure = timeout ? new TimeoutException("unknown outcome") : new InvalidOperationException("rejected");
        proxy.Response = () => Task.FromException(failure);
        var operation = new WorkspaceSessionOperation(client, () => client);
        var thrown = await Record.ExceptionAsync(() => Mutate(operation, delete));
        Assert.Same(failure, thrown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewaySwitchDuringDialog_DoesNotMutateEitherGateway(bool delete)
    {
        var (original, oldProxy) = Gateway();
        var (replacement, newProxy) = Gateway();
        IOperatorGatewayClient current = original;
        var operation = new WorkspaceSessionOperation(original, () => current);
        current = replacement;
        await Assert.ThrowsAsync<WorkspaceSessionConnectionChangedException>(() => Mutate(operation, delete));
        Assert.Equal(0, oldProxy.Calls);
        Assert.Equal(0, newProxy.Calls);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("disconnect")]
    [InlineData("close")]
    public async Task ChangedOwnerBeforeDispatch_DoesNotSend(string change)
    {
        var (client, proxy) = Gateway();
        IOperatorGatewayClient? current = client;
        var operation = new WorkspaceSessionOperation(client, () => current);
        if (change == "reconnect") proxy.Epoch++;
        if (change == "disconnect") proxy.Connected = false;
        if (change == "close") current = null;
        await Assert.ThrowsAsync<WorkspaceSessionConnectionChangedException>(() => Mutate(operation, false));
        Assert.Equal(0, proxy.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOwnerDuringResponse_DoesNotAcceptStaleCompletion(bool reconnect)
    {
        var (client, proxy) = Gateway();
        IOperatorGatewayClient? current = client;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => pending.Task;
        var operation = new WorkspaceSessionOperation(client, () => current);
        var task = operation.DeleteAsync("same-key");
        if (reconnect) proxy.Epoch++;
        else current = Gateway().Client;
        pending.SetResult();
        await Assert.ThrowsAsync<WorkspaceSessionConnectionChangedException>(() => task);
    }
}
