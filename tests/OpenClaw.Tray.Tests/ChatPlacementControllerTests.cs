using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
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
        Assert.Equal(duringPreparation ? ChatPlacementError.Interrupted : ChatPlacementError.NoLongerEmpty,
            context.Placement.State.Error);
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
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
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
        context.Gateway.Prepare = _ => completion.Task;
        context.Placement.SelectEnvironment("node:windows");
        context.Placement.SelectWorkspace(FakeGateway.Project);
        context.Vm.SetDraft("keep this draft");
        var sending = context.Composer.SendAsync();
        context.Composer.BindSelectionHandoff(key => context.Session.ApplyInputs(Inputs(key)));
        context.Composer.SelectChannel("agent:main:other");
        Assert.True(context.Gateway.PrepareToken.IsCancellationRequested);
        Assert.Null(context.Placement.State.Error);

        if (returnBeforeCompletion)
        {
            context.Composer.SelectChannel(Key);
            Assert.True(context.Placement.State.Preparing);
            Assert.False(context.Placement.CanEdit);
            Assert.False(await context.Composer.SendCoreAsync(Key, "New chat", "another send", []));
            Assert.Equal(1, context.Gateway.PrepareCalls);
        }
        completion.SetResult();
        Assert.False(await sending);
        if (!returnBeforeCompletion)
            context.Composer.SelectChannel(Key);

        Assert.False(context.Placement.State.Preparing);
        Assert.True(context.Placement.RequiresPreparation);
        Assert.Equal("node:windows", context.Placement.State.Selection?.Environment.Id);
        Assert.Equal(FakeGateway.Project, context.Placement.State.Selection?.Workspace);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal("keep this draft", context.Vm.Draft);
        Assert.Equal(0, context.Port.SendMessageCallCount);

        // Retrying must reach the Gateway preflight, not silently send with the displayed defaults.
        context.Gateway.Prepare = _ => Task.FromException(new InvalidOperationException("Placement already active"));
        Assert.False(await context.Composer.SendAsync());
        Assert.Equal(2, context.Gateway.PrepareCalls);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal(0, context.Port.SendMessageCallCount);
    }

    [Fact]
    public async Task CompletedLocation_RemainsSelectedWhenReturningWithoutRepeatedPreparation()
    {
        using var context = new Context();
        context.Placement.SelectWorkspace(FakeGateway.Project);
        Assert.True(await context.Placement.PrepareAsync(Key, default));
        context.Composer.BindSelectionHandoff(key => context.Session.ApplyInputs(Inputs(key)));
        context.Composer.SelectChannel("agent:main:other");
        Assert.Equal("default", context.Placement.State.Selection?.Workspace.Id);
        context.Composer.SelectChannel(Key);
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
        context.Gateway.Prepare = _ => completion.Task;
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
    public async Task HistoryBecomingUnknown_CancelsPendingPreparationButRetainsUncertainty()
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
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.True(context.Placement.RequiresPreparation);
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
                    context.Composer.SelectChannel("agent:main:other");
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
    public async Task MaterializationHistoryNeverArrives_ReleasesSendWithUncertaintyAtDeadline()
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
        Assert.True(context.Placement.RequiresPreparation);
        Assert.Equal(ChatPlacementError.Interrupted, context.Placement.State.Error);
        Assert.Equal("keep after history failure", context.Vm.Draft);
        Assert.Equal(0, context.Port.SendMessageCallCount);
        Assert.Equal(1, context.Gateway.PrepareCalls);
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
        Assert.Equal("keep", context.Vm.Draft);
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
        internal FakeGateway Gateway { get; } = new();
        internal FakeChatComposerRuntimePort Port { get; } = new();
        internal ChatComposerViewModel Vm { get; }
        internal ChatPlacementController Placement { get; }
        internal ChatComposerController Composer { get; }
        internal ChatComposerSession Session { get; }

        internal Context(bool targetIsNew = true, TimeProvider? timeProvider = null)
        {
            var dispatcher = new RecordingUiDispatcher();
            var actions = new ChatComposerHostActions(null, null, null, null, null);
            Vm = new(dispatcher, false);
            Placement = new(Gateway, dispatcher, timeProvider);
            Composer = new(Vm, Port, actions, Placement);
            Session = new(Vm, Composer, actions, Placement);
            Session.ApplyInputs(Inputs() with { PlacementTargetIsNew = targetIsNew });
        }

        public void Dispose() => Session.Dispose();
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
