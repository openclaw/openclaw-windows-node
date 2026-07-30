using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class NodeCapabilityDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_UsesFirstRegisteredCapabilityAndPreservesEventSender()
    {
        var owner = new object();
        var dispatcher = new NodeCapabilityDispatcher(owner, () => "node-1", new TestLogger());
        dispatcher.RegisterCapability(new StubCapability("first", "example.echo"));
        dispatcher.RegisterCapability(new StubCapability("second", "EXAMPLE.ECHO"));
        var responseTcs = NewCompletion<NodeInvokeResponse>();
        object? invokeSender = null;
        object? completedSender = null;
        var completedTcs = NewCompletion<NodeInvokeCompletedEventArgs>();
        dispatcher.InvokeReceived += (sender, _) => invokeSender = sender;
        dispatcher.InvokeCompleted += (sender, args) =>
        {
            completedSender = sender;
            completedTcs.TrySetResult(args);
        };

        await dispatcher.DispatchAsync(
            Request("invoke-1", "example.echo"),
            response =>
            {
                responseTcs.TrySetResult(response);
                return Task.CompletedTask;
            },
            error => Task.FromException(new Xunit.Sdk.XunitException(error)),
            CancellationToken.None);

        var response = await responseTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Ok);
        Assert.Equal("first", Assert.IsType<string>(response.Payload));
        Assert.Same(owner, invokeSender);
        Assert.Same(owner, completedSender);
        Assert.Equal("node-1", completed.NodeId);
        Assert.True(completed.Ok);
    }

    [Fact]
    public async Task DispatchAsync_RejectsUnsupportedCommandWithStructuredCompletion()
    {
        var dispatcher = new NodeCapabilityDispatcher(new object(), () => "node-1", new TestLogger());
        var errorTcs = NewCompletion<string>();
        var completedTcs = NewCompletion<NodeInvokeCompletedEventArgs>();
        dispatcher.InvokeCompleted += (_, args) => completedTcs.TrySetResult(args);

        await dispatcher.DispatchAsync(
            Request("invoke-2", "example.missing"),
            _ => Task.FromException(new Xunit.Sdk.XunitException("unexpected response")),
            error =>
            {
                errorTcs.TrySetResult(error);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            "Command not supported: example.missing",
            await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var completed = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completed.Ok);
        Assert.Equal("invoke-2", completed.RequestId);
        Assert.Equal("node-1", completed.NodeId);
    }

    [Fact]
    public async Task TryCancel_PropagatesToWindowsCapabilityAndReturnsCancelledResult()
    {
        var capability = new CancellableCapability();
        var dispatcher = new NodeCapabilityDispatcher(new object(), () => "node-1", new TestLogger());
        dispatcher.RegisterCapability(capability);
        var errorTcs = NewCompletion<string>();

        await dispatcher.DispatchAsync(
            Request("invoke-3", "example.wait"),
            _ => Task.FromException(new Xunit.Sdk.XunitException("unexpected response")),
            error =>
            {
                errorTcs.TrySetResult(error);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await capability.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispatcher.TryCancel("invoke-3"));
        Assert.Equal("cancelled", await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(dispatcher.TryCancel("invoke-3"));
    }

    private static NodeInvokeRequest Request(string id, string command)
    {
        using var document = JsonDocument.Parse("{}");
        return new NodeInvokeRequest
        {
            Id = id,
            Command = command,
            Args = document.RootElement.Clone()
        };
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StubCapability(string value, string command) : INodeCapability
    {
        public string Category => "example";
        public IReadOnlyList<string> Commands { get; } = [command];
        public bool CanHandle(string candidate) =>
            string.Equals(candidate, command, StringComparison.OrdinalIgnoreCase);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = value });
    }

    private sealed class CancellableCapability : INodeCapability
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Category => "example";
        public IReadOnlyList<string> Commands { get; } = ["example.wait"];
        public bool CanHandle(string command) => command == "example.wait";
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            throw new NotSupportedException();

        public async Task<NodeInvokeResponse> ExecuteAsync(
            NodeInvokeRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new NodeInvokeResponse { Ok = true };
        }
    }
}
