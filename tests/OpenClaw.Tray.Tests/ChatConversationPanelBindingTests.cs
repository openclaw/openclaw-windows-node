using System.Reflection;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationPanelBindingTests
{
    public class ManagerProxy : DispatchProxy
    {
        public IOperatorGatewayClient? Client;
        public GatewayConnectionSnapshot Snapshot = new()
        {
            GatewayId = "gateway-one", OperatorState = RoleConnectionState.Connected
        };
        public EventHandler<GatewayConnectionSnapshot>? StateChanged;
        public EventHandler<OperatorClientChangedEventArgs>? ClientChanged;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_CurrentSnapshot": return Snapshot;
                case "get_OperatorClient": return Client;
                case "add_StateChanged": StateChanged += (EventHandler<GatewayConnectionSnapshot>)args![0]!; return null;
                case "remove_StateChanged": StateChanged -= (EventHandler<GatewayConnectionSnapshot>)args![0]!; return null;
                case "add_OperatorClientChanged": ClientChanged += (EventHandler<OperatorClientChangedEventArgs>)args![0]!; return null;
                case "remove_OperatorClientChanged": ClientChanged -= (EventHandler<OperatorClientChangedEventArgs>)args![0]!; return null;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }

    public class ClientProxy : ChatConversationPanelStateTests.GatewayProxy
    {
        public readonly Dictionary<string, Delegate?> Events = [];
        public string UserId = "profile-a";
        public Func<Task<JsonElement>>? ProfileResponse;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_GrantedOperatorScopes") return new[] { "operator.read" };
            if (method?.Name == "SendWizardRequestAsync")
                return ProfileResponse?.Invoke() ??
                    Task.FromResult(JsonSerializer.SerializeToElement(new { profile = new { id = UserId } }));
            if (method?.Name is { } name && (name.StartsWith("add_") || name.StartsWith("remove_")))
            {
                var key = name[4..];
                if (name.StartsWith("remove_")) key = name[7..];
                Events[key] = name.StartsWith("add_")
                    ? Delegate.Combine(Events.GetValueOrDefault(key), (Delegate?)args![0])
                    : Delegate.Remove(Events.GetValueOrDefault(key), (Delegate?)args![0]);
                return null;
            }
            return base.Invoke(method, args);
        }
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("principal")]
    [InlineData("client")]
    [InlineData("gateway")]
    [InlineData("handshake")]
    public void ConnectionEventsRebindAndCancelWithoutProviderSnapshot(string change)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var clientProxy = (ClientProxy)client;
        var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        var managerProxy = (ManagerProxy)manager;
        managerProxy.Client = client;
        var session = new SessionInfo { Key = "one", SessionId = "id-one", AgentId = "writing" };
        using var state = ChatConversationPanelBinding.Create(manager, client, () => [session], identityAdvertised: () => true);
        state.Bind("one");
        state.Open("files");
        var operation = state.BeginOperation("files")!;
        var changed = 0;
        state.Changed += () => changed++;
        if (change == "epoch") clientProxy.Epoch++;
        if (change == "principal") clientProxy.SigningDeviceId = "operator-b";
        if (change == "client") managerProxy.Client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        if (change == "gateway") managerProxy.Snapshot = managerProxy.Snapshot with { GatewayId = "gateway-two" };
        if (change == "handshake") clientProxy.Epoch = null;
        managerProxy.StateChanged!(manager, managerProxy.Snapshot);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.True(changed > 0);
        if (change is "client" or "gateway" or "handshake") Assert.Null(state.Context);
    }

    [Fact]
    public void SnapshotChangingDuringCaptureCannotMixGatewayAndClient()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        var proxy = (ManagerProxy)manager;
        proxy.Client = client;
        using var state = ChatConversationPanelBinding.Create(manager, client, () =>
        {
            proxy.Snapshot = proxy.Snapshot with { GatewayId = "gateway-two" };
            return [new() { Key = "one", SessionId = "one", AgentId = "writing" }];
        }, identityAdvertised: () => true);
        state.Bind("one");
        Assert.Null(state.Context);
        Assert.False(state.Open("files"));
    }

    [Fact]
    public void DisposeRemovesEveryObservation()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        var proxy = (ManagerProxy)manager;
        proxy.Client = client;
        var state = ChatConversationPanelBinding.Create(manager, client, () => [], identityAdvertised: () => true);
        Assert.NotNull(proxy.StateChanged);
        state.Dispose();
        Assert.Null(proxy.StateChanged);
        Assert.Null(proxy.ClientChanged);
        Assert.All(((ClientProxy)client).Events.Values, handler => Assert.Null(handler));
    }

    [Fact]
    public void ProfileChangeOnSameTransportRevokesOldContextAndSeparatesRetainedLayout()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var account = (ClientProxy)client;
        var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        ((ManagerProxy)manager).Client = client;
        using var state = ChatConversationPanelBinding.Create(manager, client,
            () => [new() { Key = "one", SessionId = "one", AgentId = "writing" }], identityAdvertised: () => true);
        state.Bind("one");
        Assert.Equal("profile-a", state.Context!.Identity.UserId);
        state.Open("files");
        var operation = state.BeginOperation("files")!;
        account.UserId = "profile-b";
        ((EventHandler)account.Events["SelfProfileChanged"]!)(client, EventArgs.Empty);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.Null(state.Context);
        Assert.Equal(ChatConversationAccountStatus.RequiresReconnect, state.AccountStatus);
        Assert.Null(state.Layout.SelectedPanel);
        account.UserId = "profile-a";
        ((EventHandler)account.Events["SelfProfileChanged"]!)(client, EventArgs.Empty);
        Assert.Null(state.Context);
        account.Epoch++;
        ((ManagerProxy)manager).StateChanged!(manager, ((ManagerProxy)manager).Snapshot);
        Assert.Equal("files", state.Layout.SelectedPanel);
        Assert.Equal("profile-a", state.Context!.Identity.UserId);
        Assert.False(operation.IsCurrent);
        Assert.True(state.Context.AccountRevision > operation.Context.AccountRevision);
    }

    [Fact]
    public async Task PendingCanonicalAccountNeverPublishesThePreviousAccountsLayout()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var account = (ClientProxy)client;
        var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        ((ManagerProxy)manager).Client = client;
        using var state = ChatConversationPanelBinding.Create(manager, client,
            () => [new() { Key = "one", SessionId = "one", AgentId = "writing" }], identityAdvertised: () => true);
        state.Bind("one");
        state.Open("files");
        var oldOperation = state.BeginOperation("files")!;
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        account.ProfileResponse = () => response.Task;
        ((EventHandler)account.Events["SelfProfileChanged"]!)(client, EventArgs.Empty);
        Assert.Null(state.Context);
        Assert.Equal(ChatConversationAccountStatus.Loading, state.AccountStatus);
        Assert.Empty(state.Layout.OpenPanels);
        Assert.True(oldOperation.CancellationToken.IsCancellationRequested);
        var known = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () =>
        {
            if (state.AccountStatus == ChatConversationAccountStatus.RequiresReconnect) known.TrySetResult();
        };
        response.SetResult(JsonSerializer.SerializeToElement(new { profile = new { id = "profile-b" } }));
        await known.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(state.Layout.OpenPanels);
        Assert.Null(state.Context);
        Assert.False(oldOperation.IsCurrent);
    }
}
