using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ChatPlacementControllerTests
{
    private const string Key = "agent:main:exact:new-key";
    private static readonly TimeSpan DiscoveryTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_PLACEMENT_DISCOVERY_TIMEOUT_SECONDS"),
            out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);
    private static ChatComposerInputs Inputs(string key = Key) => new(
        "connected", false, new ChatThread { Id = key, Title = "New chat", AgentId = "main" },
        [], null, false, [], null, false)
    {
        ShowPlacementPickers = true,
        PlacementTargetIsEmpty = true,
        PlacementHistoryReady = true,
        PlacementTargetIsNew = true
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

    [Fact]
    public async Task SendAsync_SessionOptionsCannotChangeBeforeGatewayAcceptance()
    {
        using var context = new Context();
        var draft = ChatDraft.Create("main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("before-send");
        context.Vm.SetDraft("hello");
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Port.PatchDraftOptionsCallCount);
        Assert.Equal(1, context.Port.SendMessageCallCount);
        var submittedRevision = draft.Revision;

        context.Composer.SetModel("after-send-started");

        Assert.Equal(submittedRevision, draft.Revision);
        Assert.Equal("before-send", draft.Model);
        Assert.Equal(1, context.Port.PatchDraftOptionsCallCount);

        context.Port.SendMessageGate.SetResult(true);
        Assert.True(await sending);
        Assert.Equal(submittedRevision, draft.Revision);
    }

    [Fact]
    public async Task SuggestedSend_LocksSessionOptionsUntilGatewayAcceptance()
    {
        using var context = new Context();
        context.Session.ApplyInputs(Inputs(Key) with
        {
            PlacementTargetIsEmpty = false,
            PlacementTargetIsNew = false,
            ShowPlacementPickers = false
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var sending = context.Composer.SendSuggestionAsync(Key, "New chat", "Check system health");

        Assert.True(context.Composer.IsSendInProgress);
        Assert.Equal(1, context.Port.SendMessageCallCount);
        context.Composer.SetModel("changed-during-suggestion");
        Assert.Equal(0, context.Port.SetModelCallCount);

        context.Port.SendMessageGate.SetResult(true);
        Assert.True(await sending);
        Assert.False(context.Composer.IsSendInProgress);
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
        if (duringPreparation)
            Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        completion.SetResult();
        Assert.False(await (sending ?? context.Composer.SendAsync()));
        Assert.Equal("keep", context.Vm.Draft);
        Assert.Equal(ChatPlacementError.NoLongerEmpty, context.Placement.State.Error);
        Assert.False(context.Placement.RequiresPreparation);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.SendMessageCallCount);
        Assert.Equal(duringPreparation ? 1 : 0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task HistoryArrival_BlocksFurtherSendsUntilAdmittedPreparationDrains()
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        var preparing = context.Placement.PrepareAsync(Key, default);
        context.Session.ApplyInputs(Inputs() with { PlacementTargetIsEmpty = false, ShowPlacementPickers = false });
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        for (var i = 0; i < 2; i++)
        {
            Assert.False(await context.Composer.SendCoreAsync(Key, "Existing chat", "keep", []));
            Assert.True(context.Placement.RequiresPreparation);
            Assert.Equal(0, context.Port.SendMessageCallCount);
        }
        completion.SetResult();
        Assert.False(await preparing);
        Assert.Equal(ChatPlacementError.NoLongerEmpty, context.Placement.State.Error);
        Assert.True(await context.Composer.SendCoreAsync(Key, "Existing chat", "continue", []));
        Assert.Equal(1, context.Gateway.PrepareCalls);
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
        Assert.Equal(ChatPlacementError.PreparationFailed, context.Placement.State.Error);
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
                Assert.True(context.Composer.TrySelectSession("agent:main:other"));
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
                Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
                break;
        }
        completion.SetResult();
        Assert.False(await sending);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedPreparation_RestoresIntentAndWarningAfterNavigation(bool returnBeforeCompletion)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = async _ =>
        {
            await completion.Task;
            throw new ChatPlacementOutcomeUncertainException(
                new TimeoutException("The Gateway acknowledgment was lost."));
        };
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep this draft");
        var sending = context.Composer.SendAsync();
        context.Composer.BindSelectionHandoff(key => context.Session.ApplyInputs(Inputs(key)));
        Assert.True(context.Composer.TrySelectSession("agent:main:other"));
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        Assert.Null(context.Placement.State.Error);

        if (returnBeforeCompletion)
        {
            Assert.True(context.Composer.TrySelectSession(Key));
            Assert.True(context.Placement.State.Preparing);
            Assert.False(context.Placement.CanEdit);
            Assert.False(await context.Composer.SendCoreAsync(Key, "New chat", "another send", []));
            Assert.Equal(1, context.Gateway.PrepareCalls);
        }
        completion.SetResult();
        Assert.False(await sending);
        if (!returnBeforeCompletion)
            Assert.True(context.Composer.TrySelectSession(Key));

        Assert.False(context.Placement.State.Preparing);
        Assert.True(context.Placement.RequiresPreparation);
        Assert.Equal("node:windows", context.Placement.State.Selection?.Environment.Id);
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal("keep this draft", context.Vm.Draft);
        Assert.Equal(0, context.Port.SendMessageCallCount);

        // A generic interrupted preparation may have mutated the Gateway, so do not replay it.
        context.Gateway.Prepare = _ => Task.FromException(new InvalidOperationException("Placement already active"));
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task ConfirmedPreparation_CancellationDuringHistoryWaitDoesNotBecomeUncertain()
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Composer.BindSelectionHandoff(key => context.Session.ApplyInputs(
            Inputs(key) with
            {
                PlacementTargetIsNew = key == Key,
                PlacementTargetIsEmpty = key == Key,
                ShowPlacementPickers = key == Key,
                PlacementRequiresCreation = key == Key
            }));

        var preparing = context.Placement.PrepareAsync(Key, default);
        Assert.True(context.Composer.TrySelectSession("agent:main:other"));
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        Assert.True(context.Composer.TrySelectSession(Key));
        completion.SetResult();

        Assert.False(await preparing);
        Assert.False(context.Placement.RequiresPreparation);
        Assert.NotEqual(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.True(await context.Composer.SendCoreAsync(Key, "New chat", "continue", []));
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(1, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task CompletedLocation_RemainsSelectedWhenReturningWithoutRepeatedPreparation()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        Assert.True(await context.Placement.PrepareAsync(Key, default));
        context.Composer.BindSelectionHandoff(key => context.Session.ApplyInputs(Inputs(key)));
        Assert.True(context.Composer.TrySelectSession("agent:main:other"));
        Assert.Equal("default", context.Placement.State.Selection?.Workspace.Id);
        Assert.True(context.Composer.TrySelectSession(Key));
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        Assert.False(context.Placement.RequiresPreparation);
        context.Vm.SetDraft("next");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task DisconnectDuringPreparation_RetainsRequiredPreflightAfterOfflineCompletion()
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = async token =>
        {
            await completion.Task;
            if (token.IsCancellationRequested)
                throw new ChatPlacementNotMutatedException(new OperationCanceledException(token));
        };
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("send remotely");
        var sending = context.Composer.SendAsync();
        context.Gateway.ConnectionEpoch = null;
        context.Session.ApplyInputs(Inputs() with { ConnectionState = "disconnected" });
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        completion.SetResult();
        Assert.False(await sending);
        Assert.True(context.Placement.RequiresPreparation);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);

        context.Gateway.ConnectionEpoch = 8;
        context.Session.ApplyInputs(Inputs());
        context.Gateway.Prepare = _ => Task.CompletedTask;
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(2, context.Gateway.PrepareCalls);
        Assert.Equal("node:windows", context.Gateway.LastSelection?.Environment.Id);
        Assert.Equal(FakeGateway.Project, context.Gateway.LastSelection?.Workspace);
        Assert.Equal(8, context.Gateway.LastEpoch);
        Assert.Equal(1, context.Port.SendMessageCallCount);
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

    [Fact]
    public async Task LocalDraft_ShowsDefaultsAndCreatesOnlyBeforeFirstSend()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });

        Assert.True(context.Placement.State.SelectionIsKnown);
        Assert.Equal("local:gateway", context.Placement.State.Selection?.Environment.Id);
        Assert.Equal("/gateway/workspace", context.Placement.State.Selection?.Workspace.Path);
        Assert.Equal(0, context.Gateway.PrepareCalls);
        context.Gateway.Prepare = _ =>
        {
            Assert.Equal(0, context.Port.SendMessageCallCount);
            return Task.CompletedTask;
        };
        context.Vm.SetDraft("first");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(Key, context.Gateway.LastKey);
        Assert.Equal("/gateway/workspace", context.Gateway.LastSelection?.Workspace.Path);
        Assert.Equal(0, context.Port.CreateDraftSessionCallCount);
        context.Vm.SetDraft("follow up");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task LocalDraft_SelectionSurvivesNavigationAndReconnectWithoutChangingOtherSessions()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Session.ApplyInputs(Inputs("agent:main:other") with { PlacementTargetIsNew = false });
        Assert.False(context.Placement.State.SelectionIsKnown);
        Assert.False(context.Placement.RequiresPreparation);
        context.Gateway.ConnectionEpoch++;
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        Assert.True(context.Placement.State.SelectionIsKnown);
        Assert.Equal("node:windows", context.Placement.State.Selection?.Environment.Id);
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        context.Vm.SetDraft("use my choices");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal("node:windows", context.Gateway.LastSelection?.Environment.Id);
    }

    [Fact]
    public async Task LocalDraft_UsesGatewayDefaultWhilePlacementCatalogIsPending()
    {
        var catalog = new TaskCompletionSource<ChatPlacementCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new Context(
            targetIsNew: false,
            initialLoad: () => catalog.Task);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Placement.Changed += () =>
        {
            if (context.Port.CreateDraftSessionCallCount > 0 &&
                !context.Placement.State.Loading &&
                context.Placement.State.GatewayDefaultOnly)
            {
                settled.TrySetResult();
            }
        };
        var defaults = new ChatPlacementCatalog(
            7,
            "main",
            [
                new("local:gateway", "Gateway", true, ChatEnvironmentUnavailable.None),
                new("node:windows", "Windows", false, ChatEnvironmentUnavailable.None)
            ],
            new("default", "workspace", "/gateway/workspace"),
            [FakeGateway.Project]);
        var draft = ChatDraft.Create("main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Vm.SetDraft("keep this");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.CreateDraftSessionCallCount);
        Assert.Equal("main", context.Port.LastDraftSessionAgentId);
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal("agent:main:gateway-default", context.Port.LastSendMessageCall?.ThreadId);

        catalog.SetResult(defaults);
        await settled.Task.WaitAsync(DiscoveryTimeout);
        Assert.Null(context.Placement.State.Catalog);
        Assert.Equal(ChatPlacementError.GatewayDefaultOnly, context.Placement.State.Error);
    }

    [Fact]
    public async Task LocalDraft_DiscoveryFailureUsesGatewayDefaultOnFirstSend()
    {
        using var context = new Context(
            targetIsNew: false,
            initialLoad: () => Task.FromException<ChatPlacementCatalog>(
                new InvalidOperationException("catalog unavailable")));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Placement.Changed += () =>
        {
            if (context.Placement.State.GatewayDefaultOnly)
                ready.TrySetResult();
        };
        var draft = ChatDraft.Create("main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        await ready.Task.WaitAsync(DiscoveryTimeout);
        context.Vm.SetDraft("keep this");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.GatewayDefaultOnly, context.Placement.State.Error);
        Assert.Equal(1, context.Port.CreateDraftSessionCallCount);
        Assert.Equal("main", context.Port.LastDraftSessionAgentId);
        Assert.Equal("agent:main:gateway-default", context.Port.LastSendMessageCall?.ThreadId);
        Assert.Equal(0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task LocalDraft_DiscoveryFailureDoesNotReplaceAnExplicitNonDefaultLocation()
    {
        using var context = new Context();
        var selectionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Placement.Changed += () =>
        {
            if (context.Placement.State.SelectionIsKnown)
                selectionReady.TrySetResult();
        };
        var draft = ChatDraft.Create("main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        await selectionReady.Task.WaitAsync(DiscoveryTimeout);
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Load = () => Task.FromException<ChatPlacementCatalog>(
            new InvalidOperationException("catalog unavailable"));
        await context.Placement.RefreshAsync();

        context.Vm.SetDraft("keep the selected location");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(0, context.Port.CreateDraftSessionCallCount);
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal("node:windows", context.Gateway.LastSelection?.Environment.Id);
        Assert.Equal(draft.Key, context.Port.LastSendMessageCall?.ThreadId);
    }

    [Fact]
    public async Task LocalDraft_NonemptyHistoryDoesNotRetargetAnExistingSession()
    {
        using var context = new Context(targetIsNew: false);
        const string next = "agent:main:new";
        context.Session.ApplyInputs(Inputs(next) with
        {
            PlacementRequiresCreation = true, PlacementTargetIsNew = false,
            PlacementTargetIsEmpty = false, ShowPlacementPickers = false
        });
        context.Vm.SetDraft("continue existing work");
        Assert.False(context.Placement.RequiresPreparation);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task LocalDraft_OptionsStayLocalUntilCreationAndConfirmedOptionsPrecedeSend()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Composer.ClearThinkingLevel();
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SetModelCallCount);
        Assert.Equal(0, context.Port.ClearThinkingLevelCallCount);
        context.Gateway.Prepare = _ =>
        {
            Assert.Equal(0, context.Port.SetModelCallCount);
            return Task.CompletedTask;
        };
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("first");
        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal((Key, "chosen-model"), context.Port.LastSetModelCall);
        Assert.Equal(1, context.Port.PatchDraftOptionsCallCount);
        Assert.True(context.Port.LastDraftOptionsCall!.Value.Patch.Model.IsSpecified);
        Assert.Equal("chosen-model", context.Port.LastDraftOptionsCall.Value.Patch.Model.Value);
        Assert.True(context.Port.LastDraftOptionsCall.Value.Patch.ThinkingLevel.IsSpecified);
        Assert.Null(context.Port.LastDraftOptionsCall.Value.Patch.ThinkingLevel.Value);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        context.Port.PatchDraftOptionsGate.SetResult();
        Assert.True(await sending);
        Assert.Equal(1, context.Port.ClearThinkingLevelCallCount);
        Assert.Null(context.Composer.FindDraft(Key));
    }

    [Fact]
    public async Task LocalDraft_FailedOptionApplicationPreservesDraftAndAttachments()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.PatchDraftOptionsGate.SetException(new InvalidOperationException("patch rejected"));
        context.Vm.SetDraft("keep this");
        context.Vm.AddAttachments([new ChatAttachment { FileName = "note.txt", MimeType = "text/plain", Content = "eA==" }]);
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal("keep this", context.Vm.Draft);
        Assert.Single(context.Vm.PendingAttachments);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal(ChatPlacementError.DraftOptionsFailed, context.Placement.State.Error);
        Assert.NotNull(context.Composer.FindDraft(Key));
    }

    [Fact]
    public async Task LocalDraft_NavigationDuringOptionApplyFencesMessageSend()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Composer.ClearThinkingLevel();
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("keep this");
        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Port.SetModelCallCount);
        context.Session.ApplyInputs(Inputs("agent:main:other") with { PlacementTargetIsNew = false });
        context.Port.PatchDraftOptionsGate.SetResult();
        Assert.False(await sending);
        Assert.Equal(1, context.Port.PatchDraftOptionsCallCount);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("keep this", context.Vm.Draft);
        Assert.Null(context.Placement.State.Error);
    }

    [Fact]
    public async Task UnknownComposeTarget_DoesNotSendWithoutCurrentDraftOrGatewayReadiness()
    {
        using var context = new Context();
        context.Session.ApplyInputs(Inputs() with { PlacementHistoryReady = false });
        context.Vm.SetDraft("keep this");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        Assert.Equal("keep this", context.Vm.Draft);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task LocalDraft_OptionEditsDuringApplyAreIgnoredBeforeSend()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("first-model");
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("keep this");
        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Port.SetModelCallCount);
        context.Composer.SetModel("second-model");
        Assert.Equal("first-model", draft.Model);
        context.Port.PatchDraftOptionsGate.SetResult();
        Assert.True(await sending);
        Assert.Equal(1, context.Port.SendMessageCallCount);
        Assert.Equal("first-model", draft.Model);
    }

    [Fact]
    public async Task LocalDraft_LocationControlsStayLockedUntilConfirmedOptionsDispatch()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Placement.SelectEnvironment("node:windows");
        var selected = context.Placement.State.Selection;
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("send with the selected location");

        var sending = context.Composer.SendAsync();
        Assert.False(context.Placement.CanEdit);
        context.Placement.SelectWorkspace(FakeGateway.Project);
        Assert.Equal(selected, context.Placement.State.Selection);
        Assert.Equal(1, context.Gateway.PrepareCalls);

        context.Port.PatchDraftOptionsGate.SetResult();
        Assert.True(await sending);
        Assert.Equal(selected, context.Gateway.LastSelection);
    }

    [Fact]
    public async Task LocalDraft_NavigationAwayAndBackInvalidatesPendingSendLease()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Port.PatchDraftOptionsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("keep this");

        var sending = context.Composer.SendAsync();
        context.Session.ApplyInputs(Inputs("agent:main:other") with { PlacementTargetIsNew = false });
        context.Session.ApplyInputs(Inputs() with { PlacementRequiresCreation = true });
        context.Port.PatchDraftOptionsGate.SetResult();

        Assert.False(await sending);
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("keep this", context.Vm.Draft);
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/reset")]
    [InlineData("/compact")]
    public async Task LocalDraft_LifecycleCommandsExplainThatTheDraftMustBeCreatedFirst(string command)
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Vm.SetDraft(command);

        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(0, context.Port.ExecuteLifecycleCallCount);
        Assert.Equal(0, context.Port.EnqueueCompactCallCount);
        Assert.Equal(0, context.Port.CreateDefaultDraftSessionCallCount);
        Assert.Empty(context.Port.LocalErrors);
        Assert.False(string.IsNullOrWhiteSpace(context.Placement.State.DraftStatusMessage));
        Assert.Equal(command, context.Vm.Draft);
        Assert.NotNull(context.Composer.FindDraft(Key));
    }

    [Fact]
    public async Task GatewayDefaultDraft_CreationIsSingleFlightAcrossRemountedCallers()
    {
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        var creation = new TaskCompletionSource<SessionCreateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<SessionCreateResult> Create()
        {
            calls++;
            return creation.Task;
        }

        var first = draft.CreateGatewaySessionOnceAsync(Create);
        var remounted = draft.CreateGatewaySessionOnceAsync(Create);
        Assert.Same(first, remounted);
        Assert.Equal(1, calls);

        creation.SetResult(new SessionCreateResult { Ok = true, Key = "agent:main:created-once" });
        await Task.WhenAll(first, remounted);
        Assert.Equal("agent:main:created-once", draft.GatewaySessionKey);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GatewayDefaultDraft_UncertainCreationIsNeverRetriedBlindly()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Port.CreateDefaultDraftSessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.CreateDefaultDraftSessionGate.SetResult(new SessionCreateResult { OutcomeUncertain = true });
        context.Vm.SetDraft("preserve after uncertain creation");

        Assert.False(await context.Composer.SendAsync());
        Assert.True(draft.CreationOutcomeUncertain);
        Assert.Equal(1, context.Port.CreateDefaultDraftSessionCallCount);
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.CreateDefaultDraftSessionCallCount);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("preserve after uncertain creation", context.Vm.Draft);
        Assert.Equal(ChatPlacementError.DraftCreationUncertain, context.Placement.State.Error);
    }

    [Fact]
    public async Task LocalDraft_UncertainDefaultCreationStaysBlockedAfterCatalogRecovery()
    {
        using var context = new Context(
            targetIsNew: false,
            initialLoad: () => Task.FromException<ChatPlacementCatalog>(
                new InvalidOperationException("catalog unavailable")));
        var gatewayDefaultOnly = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Placement.Changed += () =>
        {
            if (context.Placement.State.GatewayDefaultOnly)
                gatewayDefaultOnly.TrySetResult();
            if (context.Placement.State.Catalog is not null)
                catalogReady.TrySetResult();
        };
        var draft = ChatDraft.Create("main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        await gatewayDefaultOnly.Task.WaitAsync(DiscoveryTimeout);
        context.Port.CreateDraftSessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.CreateDraftSessionGate.SetResult(new SessionCreateResult { OutcomeUncertain = true });
        context.Vm.SetDraft("preserve uncertain creation");

        Assert.False(await context.Composer.SendAsync());
        Assert.True(draft.CreationOutcomeUncertain);
        Assert.Equal(1, context.Port.CreateDraftSessionCallCount);

        context.Gateway.Load = null;
        await context.Placement.RefreshAsync();
        await catalogReady.Task.WaitAsync(DiscoveryTimeout);

        Assert.False(await context.Placement.PrepareAsync(draft.Key, default));
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.CreateDraftSessionCallCount);
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal(ChatPlacementError.DraftCreationUncertain, context.Placement.State.Error);
        Assert.Equal("preserve uncertain creation", context.Vm.Draft);
    }

    [Fact]
    public async Task GatewayDefaultDraft_ConfirmedCreationSurvivesNavigationBeforeSend()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.CreateDefaultDraftSessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("preserve after navigation");

        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Port.CreateDefaultDraftSessionCallCount);
        context.Session.ApplyInputs(Inputs("agent:main:other") with { PlacementTargetIsNew = false });

        context.Port.CreateDefaultDraftSessionGate.SetResult(new SessionCreateResult
        {
            Ok = true,
            Key = "agent:main:confirmed-default"
        });
        Assert.False(await sending);
        Assert.Equal("agent:main:confirmed-default", draft.GatewaySessionKey);
        Assert.Equal(0, context.Port.SendMessageCallCount);

        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.CreateDefaultDraftSessionCallCount);
        Assert.Equal("agent:main:confirmed-default", context.Port.LastSendMessageCall?.ThreadId);
    }

    [Fact]
    public async Task DraftSendFailureIsReportedOnVisibleDraft()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        var createdSessions = new List<string>();
        context.SessionCreated = createdSessions.Add;
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("preserve failed send");

        Assert.False(await context.Composer.SendAsync());

        Assert.False(string.IsNullOrWhiteSpace(context.Placement.State.DraftStatusMessage));
        Assert.Equal("agent:main:gateway-default", context.Port.LastSendMessageCall?.ThreadId);
        Assert.Equal("preserve failed send", context.Vm.Draft);
        Assert.Empty(context.Port.LocalErrors);
        Assert.Empty(context.PromotedSessions);
        Assert.Empty(createdSessions);
    }

    [Fact]
    public async Task DraftSendDeferredByActiveResponse_KeepsDraftAndDoesNotPromote()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendFailureOutcome = ChatSendOutcome.Deferred;
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("keep this until the response finishes");

        Assert.False(await context.Composer.SendAsync());

        Assert.Equal("keep this until the response finishes", context.Vm.Draft);
        Assert.NotNull(context.Composer.FindDraft(draft.Key));
        Assert.Empty(context.PromotedSessions);
        Assert.Empty(context.Port.LocalErrors);
        Assert.False(string.IsNullOrWhiteSpace(context.Placement.State.DraftStatusMessage));
        var deferredKey = Assert.IsType<string>(context.Port.LastSendIdempotencyKey);

        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(true);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(deferredKey, context.Port.LastSendIdempotencyKey);
        Assert.Null(context.Placement.State.DraftStatusMessage);
    }

    [Fact]
    public async Task GatewayDefaultDraft_RetryUsesTheSameMaterializedSession()
    {
        using var context = new Context();
        context.Port.SessionId = "session-1";
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Composer.SetModel("chosen-model");
        context.Composer.BindSelectionHandoff(_ => { });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("retry this message");

        Assert.False(await context.Composer.SendAsync());
        var idempotencyKey = Assert.IsType<string>(context.Port.LastSendIdempotencyKey);
        var materializedKey = Assert.IsType<string>(draft.GatewaySessionKey);
        Assert.Equal("agent:main:gateway-default", materializedKey);
        Assert.Equal(materializedKey, context.Port.LastSendMessageCall?.ThreadId);
        Assert.Equal("session-1", context.Port.LastExpectedSessionId);
        Assert.Equal(materializedKey, context.Port.LastDraftOptionsCall?.ThreadId);
        Assert.False(context.Placement.CanEdit);

        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementTargetIsEmpty = false,
            PlacementTargetIsNew = false,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(true);

        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Port.CreateDefaultDraftSessionCallCount);
        Assert.Equal(materializedKey, context.Port.LastSendMessageCall?.ThreadId);
        Assert.Equal(materializedKey, context.Port.LastDraftOptionsCall?.ThreadId);
        Assert.Equal(idempotencyKey, context.Port.LastSendIdempotencyKey);
    }

    [Fact]
    public async Task GatewayDefaultDraft_ChangedRetryPayloadGetsFreshIdempotencyKey()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("first retry payload");

        Assert.False(await context.Composer.SendAsync());
        var firstKey = Assert.IsType<string>(context.Port.LastSendIdempotencyKey);

        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("changed retry payload");

        Assert.False(await context.Composer.SendAsync());
        Assert.NotEqual(firstKey, context.Port.LastSendIdempotencyKey);
    }

    [Fact]
    public async Task GatewayDefaultDraft_DefinitiveRejectionUsesFreshIdempotencyKeyOnRetry()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendFailureOutcome = ChatSendOutcome.DefinitivelyRejected;
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("retry this rejected message");

        Assert.False(await context.Composer.SendAsync());
        var rejectedKey = Assert.IsType<string>(context.Port.LastSendIdempotencyKey);

        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementTargetIsEmpty = false,
            PlacementTargetIsNew = false,
            PlacementDraft = draft
        });
        context.Port.SendFailureOutcome = ChatSendOutcome.Indeterminate;
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);

        Assert.False(await context.Composer.SendAsync());
        Assert.NotEqual(rejectedKey, context.Port.LastSendIdempotencyKey);
        Assert.Equal("agent:main:gateway-default", context.Port.LastSendMessageCall?.ThreadId);
        var uncertainKey = Assert.IsType<string>(context.Port.LastSendIdempotencyKey);

        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(uncertainKey, context.Port.LastSendIdempotencyKey);
    }

    [Fact]
    public async Task GatewayDefaultDraft_MaterializationSignalsPromotionEvenWhenKeyIsUnchanged()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Port.CreateDefaultDraftSessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.CreateDefaultDraftSessionGate.SetResult(
            new SessionCreateResult { Ok = true, Key = draft.Key });
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Vm.SetDraft("first message");
        Assert.True(await context.Composer.SendAsync());
        Assert.Contains((draft.Key, draft.Key), context.PromotedSessions);
    }

    [Fact]
    public async Task GatewayDefaultDraft_RecordsMaterializationButPromotesOnlyAfterSendAcceptance()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        var events = new List<string>();
        context.SessionCreated = sessionKey => events.Add($"created:{sessionKey}");
        context.SessionPromoted = (_, sessionKey) =>
        {
            events.Add($"promoted:{sessionKey}");
            context.Placement.NavigateTo("agent:main:navigated-after-acceptance");
        };
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Vm.SetDraft("first message");

        var sending = context.Composer.SendAsync();

        Assert.DoesNotContain(events, value => value.StartsWith("created:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, value => value.StartsWith("promoted:", StringComparison.Ordinal));
        Assert.Equal(1, context.Port.SendMessageCallCount);
        Assert.Equal("agent:main:gateway-default", context.Port.LastSendMessageCall?.ThreadId);

        context.Port.SendMessageGate.SetResult(true);
        Assert.True(await sending);

        Assert.Equal(
            ["created:agent:main:gateway-default", "promoted:agent:main:gateway-default"],
            events);
    }

    [Fact]
    public async Task GatewayDefaultDraft_LateSendAcceptanceDoesNotOverrideNewerNavigation()
    {
        using var context = new Context();
        var draft = ChatDraft.Create(agentId: null, useGatewayDefaultAgent: true);
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs(draft.Key) with
        {
            PlacementRequiresCreation = false,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = draft.Key;
        context.Composer.BindSelectionHandoff(key => selected = key);
        context.Vm.SetDraft("sent from default draft");

        var sending = context.Composer.SendAsync();
        Assert.Equal(1, context.Port.SendMessageCallCount);
        Assert.True(context.Composer.TrySelectSession("agent:main:newer-selection"));
        context.Port.SendMessageGate.SetResult(true);

        Assert.True(await sending);
        Assert.Equal("agent:main:newer-selection", selected);
        Assert.Contains((draft.Key, "agent:main:gateway-default"), context.PromotedSessions);
    }

    [Fact]
    public async Task LocalDraft_SendRejectionRetriesSamePreparedIdentity()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(false);
        context.Vm.SetDraft("keep this");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal("keep this", context.Vm.Draft);
        Assert.NotNull(context.Composer.FindDraft(Key));
        context.Port.SendMessageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Port.SendMessageGate.SetResult(true);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(Key, context.Port.LastSendMessageCall?.ThreadId);
    }

    [Fact]
    public void ChatDraft_RestoringUncertainPayloadReusesItsOriginalIdempotencyKey()
    {
        var draft = new ChatDraft(Key, "main");
        var originalKey = draft.GetSendIdempotencyKey("original", []);
        draft.MarkSendOutcomeUncertain(originalKey);
        var editedKey = draft.GetSendIdempotencyKey("edited", []);

        Assert.NotEqual(originalKey, editedKey);
        Assert.Equal(originalKey, draft.GetSendIdempotencyKey("original", []));

        draft.ForgetSendIdempotencyKey(editedKey);
        Assert.Equal(originalKey, draft.GetSendIdempotencyKey("original", []));
    }

    [Fact]
    public void ChatDraft_UncertainPayloadGetsANewKeyForANewSessionIncarnation()
    {
        var draft = new ChatDraft(Key, "main");
        var originalKey = draft.GetSendIdempotencyKey("same payload", [], "session-a");
        draft.MarkSendOutcomeUncertain(originalKey);

        Assert.Equal(
            originalKey,
            draft.GetSendIdempotencyKey("same payload", [], "session-a"));
        Assert.NotEqual(
            originalKey,
            draft.GetSendIdempotencyKey("same payload", [], "session-b"));

        var unknownSessionDraft = new ChatDraft(Key, "main");
        var unknownSessionKey =
            unknownSessionDraft.GetSendIdempotencyKey("same payload", [], null);
        unknownSessionDraft.MarkSendOutcomeUncertain(unknownSessionKey);
        Assert.Equal(
            unknownSessionKey,
            unknownSessionDraft.GetSendIdempotencyKey("same payload", [], "session-a"));
    }

    [Fact]
    public async Task AcceptedDraft_IsReadyForFollowUpWhileSessionListLags()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Vm.SetDraft("first message");
        Assert.True(await context.Composer.SendAsync());
        Assert.True(context.Placement.IsKnownCreatedSession(Key));

        context.Session.ApplyInputs(Inputs(Key) with
        {
            PlacementRequiresCreation = false,
            PlacementTargetIsNew = true,
            PlacementTargetIsEmpty = false,
            PlacementHistoryReady = context.Placement.IsKnownCreatedSession(Key)
        });
        context.Vm.SetDraft("follow-up before the session list refreshes");

        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(2, context.Port.SendMessageCallCount);
        Assert.Equal(Key, context.Port.LastSendMessageCall?.ThreadId);
        Assert.Equal(1, context.Gateway.PrepareCalls);
    }

    [Fact]
    public void DraftPlacementChoice_IsRestoredWhenComposerRemounts()
    {
        using var context = new Context();
        var draft = new ChatDraft(Key, "main");
        context.Composer.RegisterDraft(draft);
        context.Session.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });
        context.Placement.SelectWorkspace(FakeGateway.Project);
        var savedSelection = Assert.IsType<ChatPlacementSelection>(draft.PlacementSelection);

        using var remountedPlacement = new ChatPlacementController(context.Gateway, new RecordingUiDispatcher());
        remountedPlacement.ApplyInputs(Inputs() with
        {
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        });

        Assert.Equal(savedSelection, remountedPlacement.State.Selection);
        Assert.True(remountedPlacement.State.SelectionIsKnown);
    }

    [Fact]
    public void DraftProjection_DoesNotInheritCachedSessionOptions()
    {
        var thread = new ChatThread
        {
            Id = Key,
            Title = "Cached session",
            AgentId = "main",
            Model = "stale-model",
            ModelProvider = "stale-provider",
            ThinkingLevel = "stale-level"
        };
        var draft = new ChatDraft(Key, "main");

        var untouched = draft.Project(thread);
        Assert.Null(untouched.Model);
        Assert.Null(untouched.ModelProvider);
        Assert.Null(untouched.ThinkingLevel);

        draft.SetModel("selected-model");
        draft.SetThinkingLevel("selected-level");
        var selected = draft.Project(thread);
        Assert.Equal("selected-model", selected.Model);
        Assert.Null(selected.ModelProvider);
        Assert.Equal("selected-level", selected.ThinkingLevel);
    }

    [Fact]
    public async Task ExistingSession_DefaultsRemainUnverifiedAndUntouchedSendDoesNotChangeLocation()
    {
        using var context = new Context(targetIsNew: false);
        Assert.NotNull(context.Placement.State.Selection);
        Assert.False(context.Placement.State.SelectionIsKnown);
        context.Vm.SetDraft("keep existing location");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.False(context.Placement.State.SelectionIsKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSession_ExplicitDefaultChoiceRequiresPreparation(bool environment)
    {
        using var context = new Context(targetIsNew: false);
        if (environment)
            context.Placement.SelectEnvironment("local:gateway");
        else
            context.Placement.SelectWorkspace(context.Placement.State.Catalog!.DefaultWorkspace);
        Assert.True(context.Placement.State.SelectionIsKnown);
        Assert.True(context.Placement.RequiresPreparation);
        context.Vm.SetDraft("use the chosen defaults");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal("local:gateway", context.Gateway.LastSelection?.Environment.Id);
        Assert.Equal("/gateway/workspace", context.Gateway.LastSelection?.Workspace.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewTargetMaterializing_DropsInferredDefaultsUntilExplicitChoice(bool environment)
    {
        using var context = new Context();
        Assert.True(context.Placement.State.SelectionIsKnown);
        context.Session.ApplyInputs(Inputs() with { PlacementTargetIsNew = false });
        Assert.False(context.Placement.State.SelectionIsKnown);
        Assert.False(context.Placement.RequiresPreparation);
        if (environment)
            context.Placement.SelectEnvironment("local:gateway");
        else
            context.Placement.SelectWorkspace(context.Placement.State.Catalog!.DefaultWorkspace);
        Assert.True(context.Placement.RequiresPreparation);
        context.Vm.SetDraft("use defaults explicitly");
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task UnknownHistory_BlocksPreparationWithoutDiscardingIntent()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Session.ApplyInputs(Inputs() with { PlacementHistoryReady = false, ShowPlacementPickers = false });
        context.Vm.SetDraft("keep pending location");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        context.Placement.ApplyInputs(Inputs() with { PlacementHistoryReady = false, ShowPlacementPickers = false });
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        Assert.True(context.Placement.RequiresPreparation);
        Assert.False(context.Placement.CanEdit);
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);

        context.Session.ApplyInputs(Inputs());
        Assert.Null(context.Placement.State.Error);
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(FakeGateway.Project, context.Gateway.LastSelection?.Workspace);
        Assert.Equal(1, context.Gateway.PrepareCalls);
    }

    [Fact]
    public async Task HistoryBecomingUnknown_AfterConfirmedPreparationDoesNotRetainUncertainty()
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep");
        var sending = context.Composer.SendAsync();
        context.Session.ApplyInputs(Inputs() with { PlacementHistoryReady = false, ShowPlacementPickers = false });
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        completion.SetResult();
        Assert.False(await sending);
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        Assert.False(context.Placement.RequiresPreparation);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaterializationDuringPreparation_WaitsForFreshHistoryBeforeSending(bool remainsEmpty)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectEnvironment("node:windows");
        context.Vm.SetDraft("keep until confirmed");
        var sending = context.Composer.SendAsync();
        try
        {
            context.Session.ApplyInputs(Inputs() with
            {
                PlacementTargetIsNew = false, PlacementHistoryReady = false, ShowPlacementPickers = false
            });
            Assert.False(context.Gateway.PrepareToken.IsCancellationRequested);
            completion.SetResult();
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, context.Port.SendMessageCallCount);
            Assert.Equal("keep until confirmed", context.Vm.Draft);

            context.Session.ApplyInputs(Inputs() with
            {
                PlacementTargetIsNew = false, PlacementTargetIsEmpty = remainsEmpty,
                ShowPlacementPickers = remainsEmpty
            });
            Assert.Equal(remainsEmpty, await sending);
            Assert.Equal(remainsEmpty ? 1 : 0, context.Port.SendMessageCallCount);
            Assert.Equal(1, context.Gateway.PrepareCalls);
            Assert.Equal(remainsEmpty ? "" : "keep until confirmed", context.Vm.Draft);
        }
        finally
        {
            context.Session.Dispose();
            completion.TrySetResult();
        }
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("reconnect")]
    [InlineData("dispose")]
    [InlineData("options")]
    public async Task MaterializationHistoryWait_CanStillBeCancelled(string change)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep");
        var sending = context.Composer.SendAsync();
        try
        {
            var pending = Inputs() with
            {
                PlacementTargetIsNew = false, PlacementHistoryReady = false, ShowPlacementPickers = false
            };
            context.Session.ApplyInputs(pending);
            Assert.False(context.Gateway.PrepareToken.IsCancellationRequested);
            completion.SetResult();
            Assert.False(sending.IsCompleted);
            switch (change)
            {
                case "navigation":
                    context.Composer.BindSelectionHandoff(_ => { });
                    Assert.True(context.Composer.TrySelectSession("agent:main:other"));
                    break;
                case "reconnect":
                    context.Gateway.ConnectionEpoch = null;
                    context.Session.ApplyInputs(pending with { ConnectionState = "disconnected" });
                    break;
                case "dispose":
                    context.Session.Dispose();
                    break;
                case "options":
                    context.Session.ApplyInputs(pending with { MessageOptionsDisabled = true });
                    break;
            }
            Assert.False(await sending);
            Assert.Equal(0, context.Port.SendMessageCallCount);
            Assert.Equal("keep", context.Vm.Draft);
        }
        finally
        {
            context.Session.Dispose();
            completion.TrySetResult();
        }
    }

    [Fact]
    public async Task MaterializationHistoryNeverArrives_ReleasesSendWithoutReplayingConfirmedPreparation()
    {
        var clock = new ManualTimeProvider();
        TimeSpan? scheduled = null;
        clock.TimerScheduled += delay => scheduled = delay;
        using var context = new Context(timeProvider: clock);
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Prepare = _ =>
        {
            context.Session.ApplyInputs(Inputs() with
            {
                PlacementTargetIsNew = false, PlacementHistoryReady = false, ShowPlacementPickers = false
            });
            return Task.CompletedTask;
        };
        context.Vm.SetDraft("keep after history failure");
        var sending = context.Composer.SendAsync();
        Assert.NotNull(scheduled);
        Assert.False(sending.IsCompleted);
        clock.Advance(scheduled.Value - TimeSpan.FromMilliseconds(1));
        Assert.False(sending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(await sending);
        Assert.False(context.Vm.IsSending);
        Assert.False(context.Placement.State.Preparing);
        Assert.False(context.Placement.RequiresPreparation);
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        Assert.Equal("keep after history failure", context.Vm.Draft);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal(1, context.Gateway.PrepareCalls);

        context.Vm.SetDraft("retry after history arrives");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);

        context.Session.ApplyInputs(Inputs() with
        {
            PlacementTargetIsNew = false,
            PlacementHistoryReady = true,
            ShowPlacementPickers = false
        });
        Assert.True(await context.Composer.SendAsync());
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal(1, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task FreshHistory_RetiresWaitNoticeAndLoadsTheReconnectedCatalog()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.ConnectionEpoch++;
        context.Session.ApplyInputs(Inputs() with { PlacementHistoryReady = false, ShowPlacementPickers = false });
        context.Vm.SetDraft("keep");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.HistoryPending, context.Placement.State.Error);
        Assert.Null(context.Placement.State.Catalog);

        context.Session.ApplyInputs(Inputs());

        Assert.Null(context.Placement.State.Error);
        Assert.NotNull(context.Placement.State.Catalog);
        Assert.Equal(0, context.Gateway.PrepareCalls);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal("keep", context.Vm.Draft);
    }

    [Fact]
    public async Task FailedSecondBrowse_CannotReuseThePreviousDirectory()
    {
        using var context = new Context();
        await context.Placement.BrowseAsync("/previous");
        Assert.Equal("/previous", context.Placement.State.Directory?.Path);
        var selection = context.Placement.State.Selection;
        var completion = new TaskCompletionSource<ChatWorkspaceDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Browse = _ => completion.Task;
        var browsing = context.Placement.BrowseAsync("/next");
        try
        {
            Assert.Null(context.Placement.State.Directory);
            completion.SetException(new InvalidOperationException("synthetic browse failure"));
            await browsing;
            Assert.Null(context.Placement.State.Directory);
            context.Placement.SelectWorkspace(new("folder:previous", "Previous", "/previous"));
            Assert.Equal(selection, context.Placement.State.Selection);
            Assert.Equal(0, context.Gateway.PrepareCalls);
        }
        finally
        {
            completion.TrySetException(new InvalidOperationException("fixture cleanup"));
        }
    }

    [Fact]
    public async Task UncertainMutation_RetainsWarningAcrossRetrySelectionAndNonemptyHistory()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Gateway.Prepare = _ => Task.FromException(
            new ChatPlacementOutcomeUncertainException(new TimeoutException("synthetic timeout")));
        context.Vm.SetDraft("keep");
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        context.Placement.SelectEnvironment("node:windows");
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        context.Gateway.Prepare = _ => Task.FromException(new InvalidOperationException("preflight rejected"));
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        context.Session.ApplyInputs(Inputs() with { PlacementTargetIsEmpty = false, ShowPlacementPickers = false });
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal(1, context.Gateway.PrepareCalls);
        Assert.Equal("keep", context.Vm.Draft);
    }

    [Fact]
    public async Task UncertainDraftPlacement_RemainsBlockedAfterControllerRemount()
    {
        var gateway = new FakeGateway
        {
            Prepare = _ => Task.FromException(
                new ChatPlacementOutcomeUncertainException(new TimeoutException("synthetic timeout")))
        };
        var dispatcher = new RecordingUiDispatcher();
        var selection = new ChatPlacementSelection(
            new("node:windows", "Windows", false, ChatEnvironmentUnavailable.None),
            FakeGateway.Project);
        var draft = ChatDraft.Create("main");
        draft.RememberPlacementIntent(new(
            selection,
            RequiresPreparation: true,
            OutcomeUncertain: false,
            Preparing: false,
            Error: null));
        var inputs = Inputs(draft.Key) with
        {
            ShowPlacementPickers = false,
            PlacementRequiresCreation = true,
            PlacementDraft = draft
        };

        using (var first = new ChatPlacementController(gateway, dispatcher))
        {
            first.ApplyInputs(inputs);
            Assert.False(await first.PrepareAsync(draft.Key, default));
        }

        Assert.True(draft.PlacementIntent?.OutcomeUncertain);
        using var remounted = new ChatPlacementController(gateway, dispatcher);
        remounted.ApplyInputs(inputs);
        Assert.False(await remounted.PrepareAsync(draft.Key, default));
        Assert.Equal(1, gateway.PrepareCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryFailure_DoesNotOutliveEmptyConversation(bool lateFailure)
    {
        using var context = new Context();
        var completion = new TaskCompletionSource<ChatPlacementCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Gateway.Load = () => completion.Task;
        var loading = context.Placement.RefreshAsync();
        if (!lateFailure)
        {
            completion.SetException(new TimeoutException("synthetic discovery timeout"));
            await loading;
            Assert.Equal(ChatPlacementError.DiscoveryFailed, context.Placement.State.Error);
        }
        context.Vm.SetDraft("normal send");
        Assert.True(await context.Composer.SendAsync());
        context.Session.ApplyInputs(Inputs() with { PlacementTargetIsEmpty = false, ShowPlacementPickers = false });
        if (lateFailure)
        {
            completion.SetException(new TimeoutException("synthetic discovery timeout"));
            await loading;
        }
        Assert.Null(context.Placement.State.Error);
        Assert.Equal(0, context.Gateway.PrepareCalls);
    }

    [Fact]
    public void PlacementRecoveryResources_CoverEveryTypedErrorInEveryLocale()
    {
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var path = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src",
                "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw");
            var values = System.Xml.Linq.XDocument.Load(path).Root!.Elements("data")
                .ToDictionary(element => (string)element.Attribute("name")!, element => (string)element.Element("value")!);
            foreach (var error in Enum.GetValues<ChatPlacementError>())
                Assert.False(string.IsNullOrWhiteSpace(values["Chat_Placement_Error_" + error]));
            Assert.False(string.IsNullOrWhiteSpace(values["Chat_Placement_Unknown"]));
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly TempDirectory _settingsDirectory = new();
        private readonly SettingsStore _settingsStore;
        internal FakeGateway Gateway { get; } = new();
        internal FakeChatComposerRuntimePort Port { get; } = new();
        internal ChatComposerViewModel Vm { get; }
        internal ChatPlacementController Placement { get; }
        internal ChatComposerController Composer { get; }
        internal ChatComposerSession Session { get; }
        internal List<(string DraftKey, string SessionKey)> PromotedSessions { get; } = [];
        internal Action<string>? SessionCreated { get; set; }
        internal Action<string, string>? SessionPromoted { get; set; }

        internal Context(
            bool targetIsNew = true,
            TimeProvider? timeProvider = null,
            Func<Task<ChatPlacementCatalog>>? initialLoad = null,
            bool showPlacementPickers = true)
        {
            Gateway.Load = initialLoad;
            var dispatcher = new RecordingUiDispatcher();
            _settingsStore = new(new SettingsManager(_settingsDirectory.Path), dispatcher);
            var actions = new ChatComposerHostActions(
                null, null, null, null, null,
                SessionCreated: sessionKey => SessionCreated?.Invoke(sessionKey),
                SessionPromoted: (draftKey, sessionKey) =>
                {
                    PromotedSessions.Add((draftKey, sessionKey));
                    SessionPromoted?.Invoke(draftKey, sessionKey);
                });
            Vm = new(dispatcher, false);
            Placement = new(Gateway, dispatcher, timeProvider);
            Composer = new(Vm, Port, actions, Placement);
            Session = new(Vm, Composer, actions, _settingsStore, new SandboxStatusStore(), Placement);
            Session.ApplyInputs(Inputs() with
            {
                PlacementTargetIsNew = targetIsNew,
                ShowPlacementPickers = showPlacementPickers
            });
        }

        public void Dispose()
        {
            Session.Dispose();
            _settingsStore.Dispose();
            _settingsDirectory.Dispose();
        }
    }

    private sealed class FakeGateway : IChatPlacementGateway
    {
        internal static readonly ChatWorkspace Project = new("project:one", "Repository", "/gateway/project", "one");
        public long? ConnectionEpoch { get; set; } = 7;
        internal Func<CancellationToken, Task> Prepare { get; set; } = _ => Task.CompletedTask;
        internal Func<Task<ChatPlacementCatalog>>? Load { get; set; }
        internal Func<string?, Task<ChatWorkspaceDirectory>> Browse { get; set; } =
            path => Task.FromResult(new ChatWorkspaceDirectory(path ?? "/", null, "/", []));
        internal int PrepareCalls { get; private set; }
        internal string? LastKey { get; private set; }
        internal string? LastBrowsePath { get; private set; }
        internal long LastEpoch { get; private set; }
        internal ChatPlacementSelection? LastSelection { get; private set; }
        internal CancellationToken PrepareToken { get; private set; }

        public Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken) =>
            Load?.Invoke() ?? Task.FromResult(new ChatPlacementCatalog(ConnectionEpoch!.Value, agentId,
                [new("local:gateway", "Gateway", true, ChatEnvironmentUnavailable.None),
                 new("node:windows", "Windows", false, ChatEnvironmentUnavailable.None)],
                new("default", "workspace", "/gateway/workspace"), [Project]));

        public Task<ChatWorkspaceDirectory> BrowseAsync(string? path, long connectionEpoch, CancellationToken cancellationToken)
        {
            LastBrowsePath = path;
            return Browse(path);
        }

        public async Task PrepareAsync(string sessionKey, string agentId, ChatPlacementSelection selection,
            long connectionEpoch, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeMutation = null)
        {
            if (beforeMutation is not null)
                await beforeMutation(cancellationToken);
            PrepareCalls++;
            LastKey = sessionKey;
            LastSelection = selection;
            LastEpoch = connectionEpoch;
            PrepareToken = cancellationToken;
            await Prepare(cancellationToken);
        }
    }
}
