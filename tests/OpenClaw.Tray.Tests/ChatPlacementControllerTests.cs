using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatPlacementControllerTests
{
    private const string Key = "agent:main:exact:new-key";
    private static ChatComposerInputs Inputs(string key = Key) => new(
        "connected", false, new ChatThread { Id = key, Title = "New chat", AgentId = "main" },
        [], [], null, false, [], null, false)
    {
        ShowPlacementPickers = true,
        PlacementTargetIsEmpty = true
    };

    [Fact]
    public async Task ChoosingAndBrowsing_AreReadOnlyUntilSend()
    {
        using var context = new Context();
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        await context.Placement.BrowseAsync("/gateway");
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("/gateway", context.Gateway.LastBrowsePath);
        Assert.Equal("node:windows", context.Placement.State.Selection?.Environment.Id);
    }

    [Fact]
    public void WorkspaceActivation_RebindsCatalogIdentityAndRejectsStaleFolderRows()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project with { Path = "/another-agent/project" });
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        context.Placement.SelectWorkspace(new("folder:stale", "Old folder", "/another-agent/private"));
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        context.Placement.SelectWorkspace(new("default", "Old agent", "/another-agent/default"));
        Assert.Equal("/gateway/workspace", context.Placement.State.Selection?.Workspace.Path);
        Assert.Equal(0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task UntouchedDefaults_PreserveExistingSendBehavior()
    {
        using var context = new Context();
        context.Vm.SetDraft("hello");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(1, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task UntouchedDefaults_DoNotGateTransportErrorsOnPlacementEpoch()
    {
        using var context = new Context();
        context.Gateway.ConnectionEpoch++;
        context.Vm.SetDraft("hello");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(1, context.Port.SendMessageCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryArrival_CancelsPendingIntentWithNoticeAndAllowsExplicitRetry(bool duringPreparation)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep");
        var sending = duringPreparation ? context.Composer.SendAsync() : null;
        context.Session.ApplyInputs(Inputs() with { PlacementTargetIsEmpty = false, ShowPlacementPickers = false });
        completion.SetResult();
        Assert.False(await (sending ?? context.Composer.SendAsync()));
        Assert.Equal("keep", context.Vm.Draft);
        Assert.Contains("send again", context.Placement.State.Error);
        Assert.False(context.Placement.RequiresPreparation);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.SendMessageCallCount);
        Assert.Equal(duringPreparation ? 1 : 0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task FailedPreparation_PreservesDraftAndAttachmentsAndNeverSends()
    {
        using var context = new Context();
        context.Vm.SetDraft("  keep this draft  ");
        var attachment = new ChatAttachment { FileName = "keep.txt" };
        context.Vm.AddAttachments([attachment]);
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Prepare = _ => Task.FromException(new InvalidOperationException("project unavailable"));

        Assert.False(await context.Composer.SendAsync());
        Assert.Equal("  keep this draft  ", context.Vm.Draft);
        Assert.Same(attachment, Assert.Single(context.Vm.PendingAttachments));
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("project unavailable", context.Placement.State.Error);
        Assert.False(context.Vm.IsSending);
    }

    [Fact]
    public async Task SuccessfulPreparation_PrecedesSendAndIsNotRepeatedForFollowUps()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Prepare = _ =>
        {
            Assert.Equal(0, context.Port.SendMessageCallCount);
            return Task.CompletedTask;
        };
        context.Vm.SetDraft("first");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(Key, context.Gateway.LastKey);
        Assert.Equal(FakeGateway.Project, context.Gateway.LastSelection?.Workspace);
        Assert.Equal(Key, context.Port.LastSendMessageCall?.ThreadId);
        context.Vm.SetDraft("second");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(2, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task WelcomeSuggestion_UsesTheSamePreparationGate()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Prepare = _ => Task.FromException(new InvalidOperationException("no permission"));
        Assert.False(await context.Composer.SendCoreAsync(Key, "New chat", "Say hi", []));
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("reconnect")]
    [InlineData("dispose")]
    [InlineData("history")]
    public async Task PendingPreparation_IsFencedBeforeSend(string change)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep");
        var sending = context.Composer.SendAsync();
        Assert.True(context.Placement.State.Preparing);
        switch (change)
        {
            case "navigation":
                context.Composer.BindSelectionHandoff(_ => { });
                context.Composer.SelectChannel("agent:main:other");
                Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
                break;
            case "reconnect":
                context.Gateway.ConnectionEpoch++;
                break;
            case "dispose":
                context.Session.Dispose();
                Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
                break;
            case "history":
                context.Session.ApplyInputs(Inputs() with { PlacementTargetIsEmpty = false, ShowPlacementPickers = false });
                break;
        }
        completion.SetResult();
        Assert.False(await sending);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task Reconnect_PreservesExplicitIntentRatherThanFallingBackToGateway()
    {
        using var context = new Context();
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.ConnectionEpoch = null;
        context.Session.ApplyInputs(Inputs() with { ConnectionState = "disconnected" });
        context.Gateway.ConnectionEpoch = 8;
        context.Session.ApplyInputs(Inputs());
        Assert.Equal("node:windows", context.Placement.State.Selection?.Environment.Id);
        context.Vm.SetDraft("on Windows");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal("node:windows", context.Gateway.LastSelection?.Environment.Id);
        Assert.Equal(8, context.Gateway.LastEpoch);
    }

    [Fact]
    public async Task StaleDirectoryResponse_DoesNotReplaceNewerNavigation()
    {
        using var context = new Context();
        var first = new TaskCompletionSource<ChatWorkspaceDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Browse = path => path == "/first"
            ? first.Task : Task.FromResult(new ChatWorkspaceDirectory("/second", "/", "/", []));
        var browsing = context.Placement.BrowseAsync("/first");
        await context.Placement.BrowseAsync("/second");
        first.SetResult(new("/first", "/", "/", []));
        await browsing;
        Assert.Equal("/second", context.Placement.State.Directory?.Path);
    }

    [Theory]
    [InlineData("https://github.com/openclaw/openclaw.git", true)]
    [InlineData("ssh://git@github.com/openclaw/openclaw.git", true)]
    [InlineData("git@github.com:openclaw/openclaw.git", true)]
    [InlineData("file:///C:/private", false)]
    [InlineData("https://user:password@github.com/repo.git", false)]
    [InlineData("--upload-pack=command", false)]
    [InlineData("https://example.com/with a space", false)]
    public void RepositoryInput_IsOnlyAnExplicitSupportedUrl(string input, bool expected) =>
        Assert.Equal(expected, ChatPlacementController.IsGitUrl(input));

    private sealed class Context : IDisposable
    {
        internal FakeGateway Gateway { get; } = new();
        internal FakeChatComposerRuntimePort Port { get; } = new();
        internal ChatComposerViewModel Vm { get; }
        internal ChatPlacementController Placement { get; }
        internal ChatComposerController Composer { get; }
        internal ChatComposerSession Session { get; }

        internal Context()
        {
            var dispatcher = new RecordingUiDispatcher();
            var actions = new ChatComposerHostActions(null, null, null, null, null);
            Vm = new(dispatcher, false);
            Placement = new(Gateway, dispatcher);
            Composer = new(Vm, Port, actions, Placement);
            Session = new(Vm, Composer, actions, Placement);
            Session.ApplyInputs(Inputs());
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeGateway : IChatPlacementGateway
    {
        internal static readonly ChatWorkspace Project = new("project:one", "Repository", "/gateway/project", "one");
        public long? ConnectionEpoch { get; set; } = 7;
        internal Func<CancellationToken, Task> Prepare { get; set; } = _ => Task.CompletedTask;
        internal Func<string?, Task<ChatWorkspaceDirectory>> Browse { get; set; } =
            path => Task.FromResult(new ChatWorkspaceDirectory(path ?? "/", null, "/", []));
        internal int PrepareCalls { get; private set; }
        internal string? LastKey { get; private set; }
        internal string? LastBrowsePath { get; private set; }
        internal long LastEpoch { get; private set; }
        internal ChatPlacementSelection? LastSelection { get; private set; }
        internal CancellationToken PrepareToken { get; private set; }

        public Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatPlacementCatalog(ConnectionEpoch!.Value, agentId,
                [new("local:gateway", "Gateway", true, ChatEnvironmentUnavailable.None),
                 new("node:windows", "Windows", false, ChatEnvironmentUnavailable.None)],
                new("default", "workspace", "/gateway/workspace"), [Project]));

        public Task<ChatWorkspaceDirectory> BrowseAsync(string? path, long connectionEpoch, CancellationToken cancellationToken)
        {
            LastBrowsePath = path;
            return Browse(path);
        }

        public Task PrepareAsync(string sessionKey, string agentId, ChatPlacementSelection selection,
            long connectionEpoch, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            LastKey = sessionKey;
            LastSelection = selection;
            LastEpoch = connectionEpoch;
            PrepareToken = cancellationToken;
            return Prepare(cancellationToken);
        }
    }
}
