using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceNavigationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("replacement")]
    public void RemovedSession_CannotReturnThroughHistory(string? replacement)
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "kept"));
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(new(WorkspacePageId.Notifications));
        history.GoBack();
        Assert.True(history.RemoveSession("removed", replacement));
        Assert.Equal(replacement, history.Current.SessionKey);
        Assert.Equal(replacement, history.ChatDestination.SessionKey);
        while (history.GoBack())
            Assert.NotEqual("removed", history.Current.SessionKey);
        while (history.GoForward())
            Assert.NotEqual("removed", history.Current.SessionKey);
    }

    [Fact]
    public void RemovingRememberedChat_PreservesNotificationsAndUnrelatedHistory()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "kept"));
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(history.RemoveSession("removed", null));
        Assert.Equal(WorkspacePageId.Notifications, history.Current.Page);
        Assert.Null(history.ChatDestination.SessionKey);
        Assert.True(history.GoBack());
        Assert.Equal("kept", history.Current.SessionKey);
    }

    [Fact]
    public void Workspace_OnlyHomeAndFooterNotificationsHaveRoutableDestinations()
    {
        Assert.Equal(new[] { WorkspacePageId.Home, WorkspacePageId.Notifications, WorkspacePageId.Settings },
            Enum.GetValues<WorkspacePageId>());
        Assert.Equal(new[] { "home", "notifications" }, WorkspaceNavigation.Routes.Keys);
        foreach (var (tag, page) in WorkspaceNavigation.Routes)
        {
            Assert.True(WorkspaceNavigation.TryResolveWorkspace($"workspace:{tag}", out var destination));
            Assert.Equal(page, destination.Page);
        }
    }

    public static IEnumerable<object[]> RemovedRoutes() =>
        new[] { "agents", "agent-detail", "writer-detail", "dashboards", "dashboard-detail", "canvas",
            "systems", "system-detail", "automations", "automation-detail", "plugins", "skills",
            "sessions", "usage", "activity", "tasks", "meetings", "apps", "portals", "more" }
        .Select(route => new object[] { route });

    [Theory]
    [MemberData(nameof(RemovedRoutes))]
    public void DeprecatedWorkspaceLinks_ReturnHomeAndCannotResurrectRemovedPages(string route)
    {
        Assert.True(WorkspaceNavigation.TryResolveWorkspace($"workspace:{route}", out var destination));
        Assert.Equal(WorkspacePageId.Home, destination.Page);
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Notifications));
        history.Navigate(destination);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Notifications, history.Current.Page);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Settings_ReturnsToExactPriorChat_AndForwardRestoresSection()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("permissions"));
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.ChatDestination);

        Assert.True(history.GoBack());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
        Assert.True(history.GoForward());
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("permissions"), history.Current);
        Assert.False(history.Navigate(WorkspaceDestination.Settings("permissions")));
    }

    [Fact]
    public void ChatSessionCompletingInSettings_DoesNotReplaceSettings()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.True(history.SelectChatSession("chat-b"));
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        Assert.Equal(new(WorkspacePageId.Home, "chat-b"), history.ChatDestination);
    }

    [Fact]
    public void RemoveSettingsEntries_PrunesOnlyMatchingSectionsFromBothStacks()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("sessions"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("channels"));
        history.GoBack();
        history.RemoveSettingsEntries(tag => tag is "sessions" or "channels");
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
    }

    [Fact]
    public void RemovedSession_IsPrunedAroundSettingsEntries()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.False(history.RemoveSession("removed", "kept"));
        Assert.Equal(new(WorkspacePageId.Home, "kept"), history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.NotEqual("removed", history.Current.SessionKey);
    }

    [Fact]
    public void PruneSettings_ReplacesPrunedCurrentWithFallbackWithoutAddingHistory()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("channels"));
        Assert.True(history.PruneSettings(tag => tag == "channels", WorkspaceDestination.Settings("connection")));
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        // The adjacent duplicate "connection" entry collapses so Back skips to chat.
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
    }

    [Fact]
    public void PruneSettings_WithCurrentFallback_KeepsCurrentAndCollapsesAdjacentDuplicates()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("settings"));
        history.Navigate(WorkspaceDestination.Settings("channels"));
        history.Navigate(WorkspaceDestination.Settings("settings"));
        Assert.False(history.PruneSettings(tag => tag == "channels", history.Current));
        Assert.Equal(WorkspaceDestination.Settings("settings"), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
    }

    [Fact]
    public void SettingsTags_KeepAboutAndAgentRoutesDistinct()
    {
        Assert.Equal("about", WorkspaceNavigation.NormalizeSettingsTag("about", "main"));
        Assert.Equal("about", WorkspaceNavigation.NormalizeSettingsTag("info", "main"));
        Assert.NotEqual(
            WorkspaceNavigation.NormalizeSettingsTag("settings", "main"),
            WorkspaceNavigation.NormalizeSettingsTag("about", "main"));
        Assert.NotEqual(
            WorkspaceNavigation.NormalizeSettingsTag("agent:alpha", "main"),
            WorkspaceNavigation.NormalizeSettingsTag("agent:beta", "main"));
    }

    [Fact]
    public void SettingsHistory_BackAndForwardWorkWithoutAnyGatewayState()
    {
        // The unconfigured path uses the same history: Settings opens, Back reaches chat, Forward returns.
        var history = new WorkspaceNavigationHistory();
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
    }

    [Fact]
    public void SettingsSameWindow_HasOneKeyboardOwnerOneHistoryAndSetupBypass()
    {
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        var viewXaml = File.ReadAllText(Source("Controls", "SettingsShellView.xaml"));
        Assert.DoesNotContain("OnRootPreviewKeyDown", view + viewXaml);
        Assert.DoesNotContain("GoBack", view);
        Assert.DoesNotContain("GoForward", view);
        Assert.DoesNotContain("BackStack", view);
        Assert.DoesNotContain("typeof(ChatPage)", view);
        Assert.DoesNotContain("new ChatPage", view);
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        var core = manager[manager.IndexOf("private void ShowHubCore", StringComparison.Ordinal)..];
        core = core[..core.IndexOf("private void ShowWorkspace", StringComparison.Ordinal)];
        var settingsRoute = core[core.IndexOf("}, tag =>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RequiresSetup", settingsRoute);
        Assert.Contains("settingsTag: tag", settingsRoute);

        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.DoesNotContain("ShowHub(\"command-center\")", workspace);
        Assert.Contains("_settings?.Deactivate()", workspace);
        Assert.Contains("settings.Dispose()", workspace);
    }

    [Fact]
    public void ColdNativeSetup_ChannelsAndSkills_AwaitSettingsRenderInsteadOfThrowing()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var wait = workspace[workspace.IndexOf("internal async Task WaitForNativeSetupAsync", StringComparison.Ordinal)..];
        wait = wait[..wait.IndexOf("request.RequireWorkspaceDestination(Destination);", StringComparison.Ordinal)];
        // The Settings view does not exist until Loaded on a closed window: wait for the render, never throw on null.
        Assert.DoesNotContain("_settings is null || !IsSettingsVisible", wait);
        Assert.Contains("_pendingSettingsRequest.WaitForRenderAsync(request, _settingsRendered.Task, ct)", wait);
        Assert.Contains("_settings.WaitForNativeSetupAsync(request, ct)", wait);
        Assert.Contains("_settingsRendered.TrySetResult()", workspace);
        Assert.Contains("_settingsRendered.TrySetCanceled()", workspace);
        // The request is never replayed through history: it is consumed once by the first render
        // and cleared when any non-Settings destination renders or the window closes.
        Assert.Contains("_pendingSettingsRequest.Consume()", workspace);
        Assert.Contains("_pendingSettingsRequest.Clear();\n        LeaveSettings();", workspace.Replace("\r\n", "\n"));
        Assert.Equal("channels", WorkspaceNavigation.NormalizeSettingsTag("channels", "main"));
        Assert.Equal("skills", WorkspaceNavigation.NormalizeSettingsTag("skills", "main"));
        Assert.Contains("NormalizeNativeSetupTag(request.PageTag)", workspace);
    }

    [Fact]
    public void PendingRequestSlot_SupersededRequestCancelsWaiterAndIsNeverConsumedLater()
    {
        var slot = new PendingRequestSlot<object>();
        var a = new object();
        var tokenA = slot.Set(a);
        Assert.Equal(tokenA, slot.TokenFor(a));
        // Leaving Settings before it loaded clears A: its waiter is cancelled promptly.
        slot.Clear();
        Assert.True(tokenA.IsCancellationRequested);
        Assert.Null(slot.TokenFor(a));
        // A later same-tag visit consumes nothing.
        Assert.Null(slot.Consume());
        // A newer request supersedes the older one.
        var b = new object();
        var c = new object();
        var tokenB = slot.Set(b);
        slot.Set(c);
        Assert.True(tokenB.IsCancellationRequested);
        Assert.Null(slot.TokenFor(b));
        Assert.NotNull(slot.TokenFor(c));
        // Consuming hands the request over once and leaves its waiter running for the render.
        var tokenC = slot.TokenFor(c)!.Value;
        Assert.Same(c, slot.Consume());
        Assert.False(tokenC.IsCancellationRequested);
        Assert.Null(slot.Consume());
    }

    [Fact]
    public async Task PendingRequestSlot_CancellationNeverResumesWaiterInsideClear()
    {
        var slot = new PendingRequestSlot<object>();
        var request = new object();
        slot.Set(request);
        var rendered = new TaskCompletionSource();
        var context = new QueueingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task wait;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            wait = slot.WaitForRenderAsync(request, rendered.Task, CancellationToken.None);
            // Clear runs in the middle of a render on the same context; the waiter must not resume
            // (and reenter the render) inside it.
            slot.Clear();
            Assert.False(wait.IsCompleted);
            Assert.Equal(0, context.Executed);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        context.RunAll();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("destination changed", ex.Message);
    }

    /// <summary>Models WorkspaceWindow's modal policy over the real DeferredNavigation: Navigate, prune and dialog stack.</summary>
    private sealed class ModalHost
    {
        public readonly DeferredNavigation Deferred = new();
        public readonly List<string> Renders = new();
        public readonly List<string> ModalRenders = new();
        public int Modals;
        public string Destination = "page";

        public void Navigate(string destination)
        {
            if (Deferred.ShouldDefer(destination != Destination) && Modals > 0)
            {
                Deferred.Defer(() => Navigate(destination));
                Deferred.TryArm();
                return;
            }
            if (destination != Destination) Destination = destination;
            Render();
        }

        public void Prune(string fallback)
        {
            Destination = fallback;
            if (Modals > 0 || Deferred.HasPending)
            {
                Deferred.DeferRender();
                if (Modals > 0) Deferred.TryArm();
            }
            else Render();
        }

        public void CloseDialog()
        {
            Modals--;
            if (!Deferred.Run(() => Modals > 0, Render)) Deferred.TryArm();
        }

        public void Render()
        {
            Renders.Add(Destination);
            if (Modals > 0) ModalRenders.Add(Destination);
        }
    }

    [Fact]
    public void DeferredNavigation_PrunedRenderRunsBeforeDeferredForwardAndOnlyOnce()
    {
        var host = new ModalHost { Modals = 1 };
        host.Navigate("forward");
        host.Prune("fallback");
        host.Navigate("latest");
        host.CloseDialog();
        Assert.Equal(new[] { "fallback", "latest" }, host.Renders);
        Assert.Empty(host.ModalRenders);
        Assert.False(host.Deferred.HasPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredNavigation_PruneThenSameFallbackNeverRendersUnderDialog(bool withEarlierIntent)
    {
        var host = new ModalHost { Modals = 1 };
        if (withEarlierIntent) host.Navigate("other");
        host.Prune("fallback");
        host.Navigate("fallback");
        Assert.Empty(host.Renders);
        host.CloseDialog();
        Assert.Equal(new[] { "fallback", "fallback" }, host.Renders);
        Assert.Empty(host.ModalRenders);
    }

    [Fact]
    public void DeferredNavigation_PruneWithoutIntentThenRepeatedFallbackWaitsForSecondDialog()
    {
        var host = new ModalHost { Modals = 1 };
        host.Prune("fallback");
        host.Navigate("fallback");
        // Dialog A closes while dialog B is already open: nothing may be consumed or rendered.
        host.Modals = 2;
        host.CloseDialog();
        Assert.Empty(host.Renders);
        Assert.True(host.Deferred.HasPending);
        host.CloseDialog();
        Assert.Empty(host.ModalRenders);
        Assert.NotEmpty(host.Renders);
        Assert.False(host.Deferred.HasPending);
    }

    [Fact]
    public void DeferredNavigation_PruneWithoutRepeatRendersOnceAfterLastDialog()
    {
        var host = new ModalHost { Modals = 1 };
        host.Prune("fallback");
        host.Modals = 2;
        host.CloseDialog();
        Assert.Empty(host.Renders);
        host.CloseDialog();
        Assert.Equal(new[] { "fallback" }, host.Renders);
        Assert.Empty(host.ModalRenders);
    }

    [Fact]
    public void DeferredNavigation_ClearDropsPendingWork()
    {
        var deferred = new DeferredNavigation();
        var ran = false;
        deferred.Defer(() => ran = true);
        deferred.DeferRender();
        deferred.Clear();
        Assert.True(deferred.Run(() => false, () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void WorkspaceWindow_ModalPolicyRetainsRenderAndRejectsNativeSetupUnderDialog()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("_deferred.ShouldDefer(destination != Destination) && DeferWhileModalOpen(", workspace);
        Assert.Contains("ArmDeferredDrain(next)", workspace);
        var admission = workspace[workspace.IndexOf("internal void NavigateNativeSetup", StringComparison.Ordinal)..
            workspace.IndexOf("private string NormalizeNativeSetupTag", StringComparison.Ordinal)];
        Assert.Contains("if (OpenModalDialog() is not null)", admission);
        Assert.DoesNotContain("IsSettingsVisible", admission);
        Assert.True(admission.IndexOf("_pendingSettingsRequest.Clear()", StringComparison.Ordinal) <
            admission.IndexOf("if (OpenModalDialog()", StringComparison.Ordinal));
        var modalAdmission = admission.IndexOf("if (OpenModalDialog()", StringComparison.Ordinal);
        Assert.True(modalAdmission < admission.IndexOf("if (request.WorkspaceDestination is null)", StringComparison.Ordinal));
        Assert.True(modalAdmission < admission.IndexOf("_pendingSettingsRequest.Set(request)", StringComparison.Ordinal));
        Assert.True(modalAdmission < admission.IndexOf("_chat.BindNativeSetupRequest(request)", StringComparison.Ordinal));
        Assert.True(modalAdmission < admission.IndexOf("_chat.Initialize(this)", StringComparison.Ordinal));
        Assert.Contains("if (destination != Destination) Navigate(destination);\n            return;",
            admission.Replace("\r\n", "\n", StringComparison.Ordinal));
        var defer = workspace[workspace.IndexOf("private bool DeferWhileModalOpen", StringComparison.Ordinal)..
            workspace.IndexOf("private void ArmDeferredDrain", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsSettingsVisible", defer);
    }

    [Fact]
    public void ChatCaptureProgress_UsesPerInvocationCallbacksAndKeepsLifetimeFence()
    {
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var capture = chat[chat.IndexOf("private async Task<string?> VoiceTranscribeAsync", StringComparison.Ordinal)..
            chat.IndexOf("private async Task ReadChatTextAloudAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("voiceService.TranscriptionReceived", capture);
        Assert.DoesNotContain("voiceService.AudioLevelChanged", capture);
        Assert.Contains("ListenOnceAsync(args, cancellationToken, OnTranscription, OnAudioLevel)", capture);
        Assert.Contains("!cancellationToken.IsCancellationRequested && _pageActive", capture);
        Assert.Contains("generation == _surfaceGeneration && ReferenceEquals(host, _reactorHost)", capture);
        Assert.Contains("preview.TryApply(IsCurrentCapture())", capture);
        var end = capture.IndexOf("preview.End()", StringComparison.Ordinal);
        var clear = capture.IndexOf("host?.SetVoiceTranscript(null)", StringComparison.Ordinal);
        Assert.True(end >= 0 && clear > end);
        var clearPredicate = capture[end..clear];
        Assert.Contains("_pageActive && generation == _surfaceGeneration && ReferenceEquals(host, _reactorHost)", clearPredicate);
        Assert.DoesNotContain("IsCurrentCapture()", clearPredicate);
    }

    [Fact]
    public void SettingsTransition_SuspendsCaptureBeforeHidingRetainedChat_AndSearchKeepsLocalAliases()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var render = workspace[workspace.IndexOf("private void RenderSettings()", StringComparison.Ordinal)..
            workspace.IndexOf("private void LeaveSettings()", StringComparison.Ordinal)];
        Assert.True(render.IndexOf("_chat.SuspendSurface()", StringComparison.Ordinal) >= 0);
        Assert.True(render.IndexOf("_chat.SuspendSurface()", StringComparison.Ordinal) <
            render.IndexOf("NavigationSection.Visibility = Visibility.Collapsed", StringComparison.Ordinal));
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var suspension = chat[chat.IndexOf("internal void SuspendSurface()", StringComparison.Ordinal)..
            chat.IndexOf("public void TriggerAutoStartVoice()", StringComparison.Ordinal)];
        Assert.Contains("_pendingVoice.Cancel()", suspension);
        Assert.Contains("_reactorHost?.SetHostVisible(false)", suspension);
        Assert.DoesNotContain("DisposeReactorHost();", suspension);
        Assert.Contains("_reactorHost?.SetHostVisible(true)", chat);
        Assert.Contains("if (preview.TryApply(IsCurrentCapture())) host?.SetVoiceTranscript(text)", chat);
        Assert.Contains("if (preview.TryApply(IsCurrentCapture())) host?.SetVoiceAudioLevel(level)", chat);
        Assert.Contains("preview.End();", chat);
        Assert.Contains("e.Key == global::Windows.System.VirtualKey.E ||", workspace);
        Assert.Contains("(IsSettingsVisible && e.Key == global::Windows.System.VirtualKey.F)", workspace);
    }

    [Fact]
    public async Task PendingRequestSlot_WaitRejectsRequestThatIsNotPendingAndHonorsCloseCancellation()
    {
        var slot = new PendingRequestSlot<object>();
        var stale = new object();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => slot.WaitForRenderAsync(stale, new TaskCompletionSource().Task, CancellationToken.None));

        var current = new object();
        slot.Set(current);
        using var close = new CancellationTokenSource();
        var wait = slot.WaitForRenderAsync(current, new TaskCompletionSource().Task, close.Token);
        close.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        // Close cancellation is not reported as a superseded destination.
        Assert.Same(current, slot.Current);

        var rendered = new TaskCompletionSource();
        var next = new object();
        slot.Set(next);
        var ok = slot.WaitForRenderAsync(next, rendered.Task, CancellationToken.None);
        Assert.Same(next, slot.Consume());
        rendered.SetResult();
        await ok;
    }

    private sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public int Executed { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.Count > 0)
                {
                    var (callback, state) = _queue.Dequeue();
                    Executed++;
                    callback(state);
                }
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    [Fact]
    public void ModalPolicy_DefersPrunesAndNativeSetupInsteadOfReplacingUnderDialogs()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("ArmDeferredDrain(modal)", workspace);
        Assert.Contains("_deferred.DeferRender()", workspace);
        Assert.Contains("if (Destination != destination)", workspace);
        Assert.Contains("popup.Child is ContentDialog", manager);
    }

    [Fact]
    public void LeavingSettings_StopsDebugPage_NeverCancelsDialogs_AndNullStatusRefreshes()
    {
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        var debug = File.ReadAllText(Source("Pages", "DebugPage.xaml.cs"));
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("(ContentFrame.Content as DebugPage)?.Deactivate()", view);
        Assert.Contains("internal void Deactivate()", debug);
        // Dialog policy: Settings never cancels a ContentDialog; the host defers navigation instead.
        Assert.DoesNotContain("dialog.Hide()", view);
        Assert.DoesNotContain("_foreignDialogs", view);
        Assert.Contains("DeferWhileModalOpen(NavigateBack)", workspace);
        Assert.Contains("DeferWhileModalOpen(NavigateForward)", workspace);
        Assert.Contains("DeferWhileModalOpen(() => Navigate(destination, preserveConversation))", workspace);
        Assert.DoesNotContain(".Hide()", workspace[workspace.IndexOf("private bool DeferWhileModalOpen", StringComparison.Ordinal)..][..900]);
        Assert.Contains("GatewayConnectionSnapshot? snapshot, ConnectionStatus status", view);
        Assert.DoesNotContain("if (snapshot is not null) _settings", workspace);
    }

    [Fact]
    public void CompanionCatalog_RemainsSeparateAndComplete()
    {
        foreach (var page in Enum.GetValues<CompanionPageId>())
            Assert.False(WorkspaceNavigation.TryResolveWorkspace(WorkspaceNavigation.CompanionTag(page), out _));
        Assert.Equal("cron", WorkspaceNavigation.CompanionTag(CompanionPageId.Cron));
        Assert.Equal("agent:custom", WorkspaceNavigation.CompanionTag(CompanionPageId.Agents, "custom"));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace("workspace:unknown", out _));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace("workspace:agents:unknown", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hub")]
    [InlineData("home")]
    [InlineData("workspace")]
    [InlineData("chat")]
    public void LandingAndChat_OpenWorkspace(string? route)
    {
        Assert.True(WorkspaceNavigation.TryResolveWorkspace(route, out var destination));
        Assert.Equal(WorkspacePageId.Home, destination.Page);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("connection")]
    [InlineData("usage")]
    [InlineData("channels")]
    [InlineData("about")]
    [InlineData("agent:main:workspace")]
    public void CompanionRoutes_DoNotReplaceWorkspace(string route)
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace(route, out _));
        Assert.Equal(new(WorkspacePageId.Notifications), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
    }

    [Fact]
    public void RepeatedDestinations_DoNotGrowBackStack()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.Navigate(new(WorkspacePageId.Home)));
        Assert.False(history.CanGoBack);
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(history.Navigate(new(WorkspacePageId.Notifications)));
        history.GoBack();
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void BackAndForward_PreserveIdentity_AndNewDestinationClearsForwardHistory()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.GoBack());
        Assert.False(history.GoForward());
        var first = new WorkspaceDestination(WorkspacePageId.Notifications);
        var second = new WorkspaceDestination(WorkspacePageId.Home);
        history.Navigate(first);
        history.Navigate(second);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.True(history.CanGoForward);
        Assert.False(history.Navigate(first));
        Assert.True(history.GoForward());
        Assert.Equal(second, history.Current);
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoBack);
        Assert.True(history.GoForward());
        Assert.Equal(first, history.Current);
        Assert.True(history.Navigate(new(WorkspacePageId.Home)));
        Assert.False(history.CanGoForward);
        Assert.False(history.GoForward());
    }

    [Fact]
    public void SessionHistory_RestoresExactKeysAndClearsForwardOnlyForNewDestinations()
    {
        var history = new WorkspaceNavigationHistory();
        var first = new WorkspaceDestination(WorkspacePageId.Home, "agent:main:first");
        var second = new WorkspaceDestination(WorkspacePageId.Home, "agent:research:second");
        Assert.True(history.Navigate(first));
        Assert.True(history.Navigate(second));
        Assert.False(history.Navigate(second));
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.False(history.Navigate(first));
        Assert.True(history.CanGoForward);
        Assert.True(history.GoForward());
        Assert.Equal(second, history.Current);
        Assert.True(history.Navigate(new(WorkspacePageId.Notifications)));
        Assert.Equal(second, history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.Equal(second, history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.Equal(first, history.ChatDestination);
        Assert.True(history.Navigate(new(WorkspacePageId.Home, "agent:main:third")));
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home), history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void SessionHistory_IsAppliedBeforeChatInitializationWithoutIntermediateHomeRoute()
    {
        var source = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("Navigate(new(WorkspacePageId.Home, sessionKey))", source);
        var render = source[source.IndexOf("private void RenderDestination()", StringComparison.Ordinal)..
            source.IndexOf("private void UpdateNavigationSelection()", StringComparison.Ordinal)];
        Assert.Contains("_chat.QueueSession(sessionKey)", render);
        Assert.True(render.IndexOf("_chat.QueueSession(sessionKey)", StringComparison.Ordinal) <
            render.IndexOf("_chat.Initialize(this)", StringComparison.Ordinal));
        Assert.DoesNotContain("_selectedSessionKey", source);
        Assert.Contains("destination = _navigation.ChatDestination;", source);
        Assert.Contains("preserveConversation: false", source);
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("_workspaceWindow.SelectSession(sessionKey);\n            else\n                _workspaceWindow.Navigate(destination);",
            manager.Replace("\r\n", "\n"));
    }

    [Fact]
    public void WorkspaceNavigation_UsesCompanionNativeControlAndExistingColourfulAssets()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = Assert.Single(document.Descendants(), element => element.Name.LocalName == "NavigationView");
        Assert.Equal("Left", (string?)navigation.Attribute("PaneDisplayMode"));
        Assert.Equal("False", (string?)navigation.Attribute("IsSettingsVisible"));
        var items = navigation.Descendants().Where(element => element.Name.LocalName == "NavigationViewItem" && element.Attribute("Tag") is not null).ToArray();
        Assert.Equal("home", (string?)Assert.Single(items).Attribute("Tag"));
        foreach (var item in items)
        {
            Assert.Contains(item.Descendants(), element => element.Name.LocalName == "ImageIcon");
            Assert.True(WorkspaceNavigation.Routes.ContainsKey((string)item.Attribute("Tag")!));
        }
        foreach (var image in navigation.Descendants().Where(element => element.Name.LocalName == "SvgImageSource"))
        {
            var asset = ((string)image.Attribute("UriSource")!)["ms-appx:///".Length..];
            Assert.True(File.Exists(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", asset)));
        }
        foreach (var (name, slot) in new[]
        {
            ("AssistantSelector", "NavigationView.PaneHeader"),
            ("SessionsHeader", "NavigationView.MenuItems"),
            ("OwnerButton", "NavigationView.PaneFooter"),
            ("NotificationsButton", "NavigationView.PaneFooter")
        })
        {
            var control = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Contains(control.Ancestors(), element => element.Name.LocalName == slot);
        }
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "PinnedList");
        Assert.Equal("False", (string?)navigation.Attribute("IsPaneToggleButtonVisible"));
        Assert.Contains("var open = !NavView.IsPaneOpen", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs")));
    }

    [Fact]
    public void HiddenPane_UsesZeroWidthAndReopenRowWithoutCoveringContent()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = document.Descendants().Single(element => element.Name.LocalName == "NavigationView");
        Assert.Equal("0", (string?)navigation.Attribute("CompactPaneLength"));
        var reopen = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ReopenPaneButton");
        Assert.Equal("Button", reopen.Name.LocalName);
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)reopen.Attribute("Style"));
        Assert.Equal("Collapsed", (string?)reopen.Attribute("Visibility"));
        var content = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ContentHost");
        var slot = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ReopenPaneSlot");
        Assert.Same(slot.Parent, content.Parent);
        Assert.Equal("56", (string?)slot.Attribute("Height"));
        var collapse = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CollapsePaneButton");
        Assert.Same(reopen.Parent, collapse.Parent);
        Assert.Same(reopen.Parent, navigation.Parent);
        Assert.Equal((string?)collapse.Attribute("Margin"), (string?)reopen.Attribute("Margin"));
        Assert.Equal("2", (string?)content.Attribute("Grid.Row"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "NavigationView.FooterMenuItems");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Equal("OnPaneClosed", (string?)navigation.Attribute("PaneClosed"));
        var duration = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "SplitViewPaneAnimationOpenDuration");
        Assert.Equal("00:00:00.16", duration.Value);
        Assert.DoesNotContain("DispatcherQueueTimer", code);
        Assert.Contains("NavView.IsPaneVisible = false", code);
        Assert.DoesNotContain("NavigationViewPaneDisplayMode.LeftMinimal", code);
        Assert.DoesNotContain("NavView.CompactPaneLength =", code);
        Assert.Contains("CollapsePaneButton : ReopenPaneButton).Focus(FocusState.Programmatic)", code);
        var toggle = code[code.IndexOf("private void OnTogglePane", StringComparison.Ordinal)..
            code.IndexOf("private void UpdatePanePresentation", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsPaneVisible = false", toggle);
        Assert.DoesNotContain("LeftMinimal", toggle);
    }

    [Fact]
    public void Sidebar_UsesNativePaneFillAndPreservesSessionSelection()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var background = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewDefaultPaneBackground");
        Assert.Equal("NavigationViewExpandedPaneBackground", (string?)background.Attribute("ResourceKey"));
        Assert.Equal("0", document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewBorderThickness").Value);
        var home = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "HomeItem");
        Assert.Equal("{StaticResource Chat_Icon}", (string?)Assert.Single(home.Descendants(),
            element => element.Name.LocalName == "ImageIcon").Attribute("Source"));
        Assert.DoesNotContain(home.Descendants(), element => element.Name.LocalName == "NavigationViewItem.Icon");
        Assert.Contains(home.Descendants(), element => (string?)element.Attribute(x + "Name") == "HomeLabel");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("session.Key == Destination.SessionKey", code);
        Assert.DoesNotContain("SelectsOnInvoked = false", code);
        Assert.DoesNotContain("OnNavigationInvoked", code);
    }

    [Fact]
    public void WorkspaceChrome_UsesNativeTitleBarAndTwoRowNavigationWithSubtleActions()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var title = document.Descendants().Single(element => element.Name.LocalName == "TitleBar");
        Assert.Equal("OpenClaw", (string?)title.Attribute("Title"));
        Assert.DoesNotContain(title.Descendants(), element => element.Name.LocalName == "Button");
        Assert.DoesNotContain(title.Descendants(), element => element.Name.LocalName == "TextBlock");
        foreach (var name in new[] { "BackButton", "ForwardButton" })
        {
            var button = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Contains(button.Ancestors(), element => (string?)element.Attribute(x + "Name") == "NavigationToolbar");
            Assert.Equal("Right", (string?)button.Parent!.Attribute("HorizontalAlignment"));
        }
        var toolbar = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NavigationToolbar");
        Assert.Contains(toolbar.Ancestors(), element => element.Name.LocalName == "NavigationView.PaneHeader");
        var toggle = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CollapsePaneButton");
        Assert.Equal("Left", (string?)toggle.Attribute("HorizontalAlignment"));
        Assert.Contains(toggle.Descendants(), element => (string?)element.Attribute("Glyph") == "\uE90C");
        var assistant = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AssistantSelector");
        Assert.Equal("ComboBox", assistant.Name.LocalName);
        Assert.Equal("Transparent", (string?)assistant.Attribute("Background"));
        Assert.Equal("0", (string?)assistant.Attribute("BorderThickness"));
        Assert.DoesNotContain(assistant.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(assistant.Descendants(), element => (string?)element.Attribute("ResourceKey") == "SubtleFillColorSecondaryBrush");
        Assert.Contains(assistant.Descendants(), element => (string?)element.Attribute("ResourceKey") == "SubtleFillColorTertiaryBrush");
        var headers = document.Descendants().Where(element => element.Name.LocalName == "NavigationViewItemHeader").ToArray();
        Assert.DoesNotContain(headers, element => (string?)element.Attribute(x + "Name") == "PagesHeader");
        var sessions = headers.Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Equal("home", (string?)sessions.ElementsBeforeSelf().Last().Attribute("Tag"));
        Assert.Equal("True", (string?)sessions.Attribute("IsEnabled"));
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "ContentPresenter"
            && (string?)element.Attribute("Content") == "{TemplateBinding Content}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "TextBlock"
            && (string?)element.Attribute("Style") == "{StaticResource NavigationViewItemHeaderTextStyle}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Target") == "HeaderContent.Visibility"
            && (string?)element.Attribute("Value") == "Collapsed");
        var newAgent = assistant.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NewAgentOption");
        Assert.Equal("ComboBoxItem", newAgent.Name.LocalName);
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "NewConversationButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("desiredItems.Add(NewAgentOption)", code);
        Assert.DoesNotContain("AssistantSelector.Items.Clear()", code);
        Assert.Contains("RestoreAssistantSelection();", code);
        Assert.Contains("ReferenceEquals(AssistantSelector.SelectedItem, NewAgentOption)", code);
        Assert.Contains("AsyncEventHandlerGuard.Run(NewAgentAsync", code);
        Assert.Contains("new AgentCreationDialog", code);
        foreach (var name in new[] { "NewSessionButton", "BackButton", "ForwardButton" })
        {
            var button = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)button.Attribute("Style"));
        }
    }

    [Fact]
    public void SidebarActionBackplates_AlignWithNativeNavigationInsetsAndCorners()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assistant = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AssistantSelector");
        Assert.Equal("4,8,4,8", (string?)assistant.Parent!.Attribute("Margin"));
        var footer = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ExpandedFooter");
        Assert.Equal("4,8,4,8", (string?)footer.Attribute("Margin"));
        var header = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Contains(header.Descendants(), element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewItemInnerHeaderMargin" && element.Value == "16,0,4,0");
        foreach (var name in new[] { "AssistantSelector", "NewSessionButton", "NotificationsButton" })
        {
            var control = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("{ThemeResource ControlCornerRadius}", (string?)control.Attribute("CornerRadius"));
            if (name != "AssistantSelector")
            {
                Assert.Equal("40", (string?)control.Attribute("Width"));
                Assert.Equal("{ThemeResource NavigationViewItemOnLeftMinHeight}", (string?)control.Attribute("Height"));
                Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)control.Attribute("Style"));
            }
        }
    }

    [Fact]
    public void WorkspaceContentSurface_IsOwnedByNativeNavigationTemplate()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(doc.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        var host = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ContentHost");
        var layout = host.Parent!;
        Assert.Equal("Grid", layout.Name.LocalName);
        Assert.Equal("NavigationView", layout.Parent!.Name.LocalName);
        foreach (var element in host.AncestorsAndSelf())
            Assert.Null(element.Attribute("Background"));
        Assert.Null(layout.Attribute("CornerRadius"));
        Assert.Null(layout.Attribute("Margin"));
        var titleBar = doc.Descendants().Single(element => element.Name.LocalName == "TitleBar");
        Assert.Null(titleBar.Attribute("Background"));
        Assert.DoesNotContain(doc.Descendants(), element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewContentBackground");

        Assert.False(File.Exists(Source("Pages", "WorkspaceContentPage.xaml")));
        Assert.False(File.Exists(Source("Pages", "WorkspaceContentPage.xaml.cs")));
        Assert.False(File.Exists(Source("Controls", "WorkspacePageRenderer.cs")));
    }

    [Fact]
    public void NativeFooter_NotificationsAreIndependentAndRightOfOwner()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var owner = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "OwnerButton");
        var bell = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Same(owner.Parent, bell.Parent);
        Assert.Equal("1", (string?)bell.Attribute("Grid.Column"));
        Assert.Null(bell.Attribute("Click"));
        var flyout = Assert.Single(bell.Descendants(), element => element.Name.LocalName == "Flyout");
        Assert.Equal("OnNotificationsOpening", (string?)flyout.Attribute("Opening"));
        Assert.Equal("OnNotificationsClosed", (string?)flyout.Attribute("Closed"));
        Assert.Single(doc.Descendants(), node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)owner.Attribute("Style"));
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)bell.Attribute("Style"));
        Assert.Single(owner.Descendants(), element => element.Name.LocalName == "PersonPicture");
        var statusDot = Assert.Single(owner.Descendants(), element => element.Name.LocalName == "FontIcon");
        Assert.Equal("OwnerStatusDot", (string?)statusDot.Attribute(x + "Name"));
        Assert.DoesNotContain(doc.Descendants(), element => (string?)element.Attribute(x + "Key") == "WorkspaceSubtleButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("OwnerButton.Flyout = menu", code);
        Assert.Contains("OpenCompanion(CompanionPageId.Usage)", code);
        Assert.Contains("OpenCompanion(CompanionPageId.Channels)", code);
        Assert.Contains("OpenCompanion(CompanionPageId.About)", code);
        Assert.Contains("Add(\"GetApps\", () => _ = OpenLinkAsync(\"https://docs.openclaw.ai/platforms\")", code);
        Assert.Contains("OpenLinkAsync(\"https://github.com/openclaw/openclaw-windows-node\")", code);
        Assert.Contains("help.Items.Add(github)", code);
        Assert.DoesNotContain("WebView", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml")));
    }

    [Fact]
    public void OwnerFooterStatusDot_PrecedesTextAndSharesLiveThemeResources()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dot = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "OwnerStatusDot");
        var detail = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "OwnerDetail");
        Assert.Same(dot.Parent, detail.Parent);
        Assert.Equal("4", (string?)dot.Parent!.Attribute("ColumnSpacing"));
        Assert.Equal("1", (string?)detail.Attribute("Grid.Column"));
        Assert.Equal("FontIcon", dot.Name.LocalName);
        Assert.Equal("Raw", (string?)dot.Attribute("AutomationProperties.AccessibilityView"));
        Assert.Equal("False", (string?)dot.Attribute("IsHitTestVisible"));
        Assert.Equal("{StaticResource ConnectionBadgeNeutral}", (string?)dot.Attribute("Style"));
        Assert.Equal("CharacterEllipsis", (string?)detail.Attribute("TextTrimming"));
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("OwnerStatusDot.Style = (Style)Root.Resources[$\"ConnectionBadge{accent}\"];", code);
        Assert.Contains("_connectionStatusIcon.Style = (Style)Root.Resources[$\"ConnectionBadge{accent}\"];", code);
        foreach (var accent in new[] { "Neutral", "Success", "Caution", "Critical" })
        {
            var style = doc.Descendants().Single(element => (string?)element.Attribute(x + "Key") == $"ConnectionBadge{accent}");
            Assert.Contains(style.Elements(), setter =>
                (string?)setter.Attribute("Property") == "Foreground" &&
                (string?)setter.Attribute("Value") == $"{{ThemeResource SystemFillColor{accent}Brush}}");
        }
    }

    [Fact]
    public void PaneToggle_TargetAndGlyphSizesMatchAcrossPaneStates()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        var expanded = doc.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "WorkspaceTogglePane");
        var compact = doc.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "WorkspaceReopenPane");
        foreach (var toggle in new[] { expanded, compact })
        {
            Assert.Equal("Button", toggle.Name.LocalName);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)toggle.Attribute("Style"));
            Assert.Equal("40", (string?)toggle.Attribute("Width"));
            Assert.Equal("40", (string?)toggle.Attribute("Height"));
            Assert.Equal("0", (string?)toggle.Attribute("Padding"));
            var icon = Assert.Single(toggle.Descendants(), element => element.Name.LocalName == "FontIcon");
            Assert.Equal("16", (string?)icon.Attribute("FontSize"));
        }
    }

    [Fact]
    public void WindowManager_OwnsOneWorkspaceWindowForChatAndSettings_AndResetsScopeOnClose()
    {
        var code = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("WorkspaceNavigation.Dispatch(navigateTo", code);
        Assert.Contains("if (_workspaceWindow is null || _workspaceWindow.IsClosed)", code);
        Assert.Contains("_callbacks.ApplyTheme(_workspaceWindow)", code);
        Assert.Contains("TryClose(\"Workspace window\"", code);
        // One HWND: Settings is a Workspace destination, never a second window.
        Assert.DoesNotMatch(@"(?<![A-Za-z])HubWindow(?![A-Za-z_])", code);
        Assert.DoesNotContain("_hubWindow", code);
        Assert.DoesNotContain("ShowCompanion", code);
        Assert.DoesNotContain("OnHubClosed", code);
        Assert.False(File.Exists(Source("Windows", "HubWindow.xaml")));
        Assert.False(File.Exists(Source("Windows", "HubWindow.xaml.cs")));
        Assert.Contains("settingsTag: tag", code);
        // The navigation scope is reset when the single Workspace window closes.
        var closed = code[code.IndexOf("private void OnWorkspaceClosed", StringComparison.Ordinal)..];
        Assert.Contains("_workspaceWindow = null;", closed);
        Assert.Contains("ResetNavigationScope();", closed[..closed.IndexOf("private void ResetNavigationScope", StringComparison.Ordinal)]);
        Assert.Contains("SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_workspaceWindow))", code);

        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.DoesNotContain("GatewayRegistry", workspace);
        Assert.DoesNotContain("new OpenClawGatewayClient", workspace);
        Assert.Contains("CurrentApp.PageActivator?.Reset();", workspace);
        Assert.Contains("settings.Dispose();", workspace);

        var viewMarkup = File.ReadAllText(Source("Controls", "SettingsShellView.xaml"));
        var chatLink = XDocument.Parse(viewMarkup).Descendants()
            .Single(element => (string?)element.Attribute("Tag") == "chat");
        Assert.Equal("False", (string?)chatLink.Attribute("SelectsOnInvoked"));
        Assert.Equal("SettingsNavChat", (string?)chatLink.Attribute("AutomationProperties.AutomationId"));
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        var invoked = view[view.IndexOf("private void NavView_ItemInvoked", StringComparison.Ordinal)..
            view.IndexOf("private void NavView_SelectionChanged", StringComparison.Ordinal)];
        Assert.Contains("NavigateTo(\"chat\")", invoked);
        Assert.Contains("DispatcherQueuePriority.Low", invoked);
        Assert.Contains("_active", invoked);
        Assert.DoesNotContain("NavigateInternal", invoked);
        Assert.DoesNotMatch(@"new\s+ChatPage\b|typeof\(ChatPage\)|\bMountReactorChat\b", view);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        Assert.Contains("Initialize(Window ownerWindow)", chat);
        Assert.Contains("ArgumentNullException.ThrowIfNull(ownerWindow);", chat);
        Assert.DoesNotContain("public void Initialize()", chat);
        Assert.Contains("GetWindowHandle(_ownerWindow)", chat);
        Assert.DoesNotContain("CurrentApp.ActiveHubWindow!.MountReactorChat", chat);
    }

    [Fact]
    public void WorkspaceRefocusAndPendingChat_PreserveUserIntent()
    {
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("preserveCurrent: navigateTo is null or \"hub\"", manager);
        Assert.Contains("if (!preserveCurrent)", manager);
        Assert.Contains("_workspaceWindow.Navigate(destination)", manager);
        Assert.Contains("_workspaceWindow.SelectSession(sessionKey)", manager);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("if (!_navigation.Navigate(destination)", workspace);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var legacy = chat[chat.IndexOf("private void ShowWebViewSurface", StringComparison.Ordinal)..];
        Assert.Contains("var pendingSessionKey = _pendingSessionKey ?? (App.Current as App)?.PendingChatSessionKey", legacy);
        Assert.DoesNotContain("_hub", chat);
        Assert.Contains("_pendingSessionKey = threadIdToMount", chat);
        Assert.Contains("_pendingVoice.Request(nativeSurface: !_webViewMode)", chat);
        Assert.Contains("_pendingVoice.Cancel()", chat);
        Assert.DoesNotContain("RetryTriggerVoice", chat);
    }

    [Fact]
    public void ComposerSelection_SynchronizesWorkspaceDestinationAndHistory()
    {
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var selected = chat[chat.IndexOf("internal void OnComposerSessionSelected", StringComparison.Ordinal)..
            chat.IndexOf("private void OnWorkspaceOpenConnection", StringComparison.Ordinal)];
        Assert.Contains("_mountedThreadId = sessionKey;", selected);
        Assert.Contains("SessionSelected?.Invoke(sessionKey, previousSessionKey)", selected);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("_chat.SessionSelected += OnChatSessionSelected;", workspace);
        Assert.Contains("_navigation.SelectChatSession(sessionKey, previousSessionKey)", workspace);
    }

    [Fact]
    public void NavigationInvalidatesDelayedComposerSelectionBeforeDeferredRendering()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var render = workspace[workspace.IndexOf("private void RenderDestination()", StringComparison.Ordinal)..
            workspace.IndexOf("private void QueueChatApply()", StringComparison.Ordinal)];
        Assert.True(render.IndexOf("_chat.InvalidatePendingSessionNavigation()", StringComparison.Ordinal) <
            render.IndexOf("_chat.QueueSession", StringComparison.Ordinal));
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var unload = chat[chat.IndexOf("private void OnUnloaded", StringComparison.Ordinal)..
            chat.IndexOf("internal void Initialize(Window ownerWindow)", StringComparison.Ordinal)];
        Assert.Contains("InvalidatePendingSessionNavigation();", unload);
        Assert.Contains("_reactorHost?.InvalidatePendingSessionNavigation()", chat);
    }

    [Fact]
    public void Projection_UsesRealIdentitiesAndNeverManufacturesAnAgent()
    {
        Assert.Empty(WorkspaceProjection.Agents(null, []));
        using var json = JsonDocument.Parse("""{"agents":[{"id":"custom","name":"Actual agent","workspace":"C:\\work"},null,{"id":9}]}""");
        var session = new SessionInfo { Key = "real-key", AgentId = "custom", DisplayName = "Actual conversation" };
        var agent = Assert.Single(WorkspaceProjection.Agents(json.RootElement, [session]));
        Assert.Equal("custom", agent.Id);
        Assert.Equal("Actual agent", agent.Name);
        Assert.Equal("real-key", agent.LatestSessionKey);
        var visible = Assert.Single(WorkspaceProjection.Sessions([session], "custom", new WorkspaceSessionOrder()));
        Assert.Equal("real-key", visible.Key);
        Assert.Empty(WorkspaceProjection.Sessions([session], "different", new WorkspaceSessionOrder()));
    }

    [Fact]
    public void Projection_UsesConfiguredIdentityAndResolvedAvatar_NotGatewayFilePaths()
    {
        using var json = JsonDocument.Parse("""
            {"defaultId":"main","agents":[
              {"id":"main","name":"Roster alias","identity":{"name":"Configured assistant","emoji":"C","avatar":"avatars/main.png","avatarUrl":"data:image/png;base64,AQID"}},
              {"id":"other","name":"Other assistant","identity":{"name":" ","avatar":"https://images.example/avatar.png"}},
              {"id":"plain","identity":42},
              {"id":" "}
            ]}
            """);
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Equal(3, agents.Count);
        Assert.Equal("Configured assistant", agents[0].Name);
        Assert.Equal("C", agents[0].Emoji);
        Assert.Equal("data:image/png;base64,AQID", agents[0].AvatarUrl);
        Assert.Equal("Other assistant", agents[1].Name);
        Assert.Equal("https://images.example/avatar.png", agents[1].AvatarUrl);
        Assert.Equal("plain", agents[2].Name);
        Assert.Null(agents[2].AvatarUrl);
        Assert.Equal("main", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, null));
        Assert.Equal("other", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "other"));
        Assert.Equal("main", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "removed"));
    }

    [Fact]
    public void Projection_RespectsExplicitAgentSelection_AndDoesNotGuessMain()
    {
        using var json = JsonDocument.Parse("""{"defaultId":"custom","selectionRequired":true,"agents":[{"id":"custom"},{"id":"other"}]}""");
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Null(WorkspaceProjection.SelectedAgentId(json.RootElement, agents, null));
        Assert.Equal("other", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "other"));
        using var legacy = JsonDocument.Parse("""{"agents":[{"id":"custom"}]}""");
        Assert.Equal("custom", WorkspaceProjection.SelectedAgentId(legacy.RootElement, WorkspaceProjection.Agents(legacy.RootElement, []), null));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "removed", true)]
    [InlineData(true, "main", false)]
    [InlineData(true, "other", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "removed", false)]
    public void SessionCreation_RequiresValidExplicitSelection(bool required, string? selectedId, bool expected)
    {
        using var json = JsonDocument.Parse(
            $$"""{"defaultId":"main","selectionRequired":{{required.ToString().ToLowerInvariant()}},"agents":[{"id":"main"},{"id":"other"}]}""");
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Equal(expected, WorkspaceProjection.RequiresAgentSelection(json.RootElement, agents, selectedId));
    }

    [Theory]
    [InlineData("""{"agents":[{"id":"main"}]}""", false)]
    [InlineData("""{"agents":[]}""", false)]
    [InlineData("""{"selectionRequired":true,"agents":[]}""", true)]
    [InlineData("null", false)]
    public void SessionCreation_PreservesLegacyPolicyAndHandlesEmptyRoster(string payload, bool expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, WorkspaceProjection.RequiresAgentSelection(
            json.RootElement, WorkspaceProjection.Agents(json.RootElement, []), null));
        Assert.False(WorkspaceProjection.RequiresAgentSelection(null, [], null));
    }

    [Fact]
    public void SessionCreation_GuardsMutationAndExplainsDisabledAction()
    {
        var source = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var handler = source[source.IndexOf("private async Task NewSessionAsync()", StringComparison.Ordinal)..
            source.IndexOf("internal void ShowError", StringComparison.Ordinal)];
        Assert.True(handler.IndexOf("WorkspaceProjection.RequiresAgentSelection", StringComparison.Ordinal) <
            handler.IndexOf("client.CreateSessionAsync", StringComparison.Ordinal));
        Assert.Contains("ShowError(Text(\"SelectAssistant\"))", handler);
        Assert.Contains("&& !selectionRequired", source);
        Assert.Contains("AutomationProperties.SetHelpText(NewSessionButton", source);
    }

    [Fact]
    public void OwnerStatusAndAgentBadge_UseNativeControlsAndLiveSources()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var accent in new[] { "Neutral", "Success", "Caution", "Critical" })
        {
            var style = document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == $"ConnectionBadge{accent}");
            Assert.Equal($"{{ThemeResource SystemFillColor{accent}Brush}}",
                (string?)Assert.Single(style.Elements()).Attribute("Value"));
        }
        var badge = XDocument.Load(Source("Controls", "AgentIdentityBadge.xaml"));
        Assert.Single(badge.Descendants(), element => element.Name.LocalName == "PersonPicture");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("ConnectionStatusPresenter.Pill(snapshot?.OverallState, status)", code);
        Assert.Contains("menu.Opening +=", code);
        Assert.Contains("_workspaceWindow?.UpdateConnectionStatus(snapshot, status)",
            File.ReadAllText(Source("Services", "WindowManager.cs")));
        var avatar = File.ReadAllText(Source("Controls", "AgentIdentityBadge.xaml.cs"));
        Assert.Contains("new MediaResolver(", avatar);
        Assert.Contains("Picture.Initials = agent.Emoji", avatar);
        Assert.Contains("cancellation.IsCancellationRequested", avatar);
        Assert.DoesNotContain("File.Read", avatar);
    }

    [Fact]
    public void Sidebar_ResolvesAgentFromSessionKeyWhenGatewayOmitsAgentId()
    {
        var sessions = WorkspaceProjection.Sessions([
            new SessionInfo { Key = "agent:main:main", IsMain = true },
            new SessionInfo { Key = "agent:research:main", IsMain = true },
            new SessionInfo { Key = "agent:main:subagent:worker", IsBackground = true }
        ], "main", new WorkspaceSessionOrder());
        var session = Assert.Single(sessions);
        Assert.Equal("agent:main:main", session.Key);
        Assert.Equal("main", session.AgentId);
    }

    [Fact]
    public void Sidebar_PrefersExplicitAgentMetadataOverSessionKey()
    {
        var sessions = new[] { new SessionInfo { Key = "agent:research:main", AgentId = "main" } };
        Assert.Equal("main", Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder())).AgentId);
        Assert.Empty(WorkspaceProjection.Sessions(sessions, "research", new WorkspaceSessionOrder()));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("completed")]
    public void Sidebar_RetainsConversationAfterRunCompletes_AndOnNextTurn(string completedStatus)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        foreach (var (status, working) in new[] { ("running", true), (completedStatus, false), ("running", true) })
        {
            var conversation = new SessionInfo
            {
                Key = "agent:main:new-conversation",
                DisplayName = "My conversation",
                Status = status,
                HasActiveRun = working,
                UpdatedAt = DateTime.UtcNow
            };
            var sessions = new[]
            {
                conversation,
                new SessionInfo { Key = "agent:main:cron:job", IsBackground = true, Status = completedStatus },
                new SessionInfo { Key = "agent:other:conversation", Status = completedStatus }
            };
            var visible = Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder()));
            Assert.Equal(conversation.Key, visible.Key);
            Assert.Equal("My conversation", visible.Title);
            Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
            Assert.Equal(status, conversation.Status);
        }
    }

    [Fact]
    public void Workspace_UsesSeparateAgentTemplatesAndSidebarSessionNavigation()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "AgentIdentityTemplate");
        Assert.Equal("DataTemplate", template.Name.LocalName);
        Assert.Equal("AgentIdentityBadge", Assert.Single(template.Elements()).Name.LocalName);
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("item.Content = agent", code);
        Assert.DoesNotContain("Content = new AgentIdentityBadge", code);
        Assert.DoesNotContain("ChatComposerSessionPicker", File.ReadAllText(Source("Chat", "ReactorChatComposer.cs")));
    }

    [Fact]
    public void Projection_DoesNotSelectBackgroundSessionsForAssistantChat()
    {
        using var json = JsonDocument.Parse("""{"agents":[{"id":"custom","name":"Actual agent"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "background", AgentId = "custom", IsBackground = true },
            new SessionInfo { Key = "visible", AgentId = "custom", DisplayName = "Conversation" }
        };
        var agent = Assert.Single(WorkspaceProjection.Agents(json.RootElement, sessions));
        Assert.Equal("visible", agent.LatestSessionKey);
        Assert.Equal("visible", Assert.Single(WorkspaceProjection.Sessions(sessions, "custom", new WorkspaceSessionOrder())).Key);
    }

    [Theory]
    [InlineData("agent:main:cron:job", null)]
    [InlineData("agent:main:subagent:worker", null)]
    [InlineData("agent:main:acp:worker", null)]
    [InlineData("agent:main:tui-one:heartbeat", null)]
    [InlineData("agent:main:hook:run", null)]
    [InlineData("agent:main:harness:run", null)]
    [InlineData("agent:main:dreaming-narrative-one", null)]
    [InlineData("agent:main:boot", null)]
    [InlineData("agent:main:opaque", "cron")]
    [InlineData("agent:main:opaque", "subagent")]
    [InlineData("agent:main:opaque", "system")]
    public void Projection_OmittedBackgroundFlagUsesCanonicalFallbackAndCannotBecomeLatest(string key, string? classification)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var conversation = new SessionInfo { Key = "agent:main:conversation", UpdatedAt = new DateTime(2026, 1, 1) };
        var background = new SessionInfo
        {
            Key = key, Classification = classification, UpdatedAt = conversation.UpdatedAt!.Value.AddHours(1)
        };
        Assert.Null(background.IsBackground);
        var sessions = new[] { conversation, background };
        Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder())).Key);
        Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Theory]
    [InlineData("agent:main:cron:job", null, false)]
    [InlineData("agent:main:opaque", "subagent", false)]
    [InlineData("agent:main:cron:job", "direct", null)]
    [InlineData("agent:main:main", null, null)]
    [InlineData("agent:main:dashboard:chat", null, null)]
    [InlineData("agent:main:voice:call", null, null)]
    [InlineData("agent:main:telegram:direct:peer", null, null)]
    public void Projection_PreservesForegroundMetadataAndExplicitFalseOverride(string key, string? classification, bool? background)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:older", UpdatedAt = new DateTime(2026, 1, 1) },
            new SessionInfo { Key = key, Classification = classification, IsBackground = background, UpdatedAt = new DateTime(2026, 1, 2) }
        };
        Assert.Equal(new[] { key, "agent:main:older" }, WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder()).Select(s => s.Key));
        Assert.Equal(key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Fact]
    public void Projection_PinnedSessionsSortFirstAndArchivedRowsHidden()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:unpinned-newer", UpdatedAt = new DateTime(2026, 3, 1) },
            new SessionInfo { Key = "agent:main:pinned-older", UpdatedAt = new DateTime(2026, 1, 1), Pinned = true, PinnedAt = 100 },
            new SessionInfo { Key = "agent:main:pinned-newer", UpdatedAt = new DateTime(2026, 2, 1), Pinned = true, PinnedAt = 200 },
            new SessionInfo { Key = "agent:main:unpinned-older", UpdatedAt = new DateTime(2026, 2, 15) },
            new SessionInfo { Key = "agent:main:archived", UpdatedAt = new DateTime(2026, 4, 1), Archived = true },
        };

        var visible = WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder());

        Assert.Equal(
            new[] { "agent:main:pinned-newer", "agent:main:pinned-older", "agent:main:unpinned-newer", "agent:main:unpinned-older" },
            visible.Select(s => s.Key));
        Assert.True(visible[0].IsPinned);
        Assert.False(visible[2].IsPinned);
        Assert.DoesNotContain(visible, s => s.IsArchived);
    }

    [Fact]
    public void Projection_ActivityDoesNotReorderSessions()
    {
        var order = new WorkspaceSessionOrder();
        var a = new SessionInfo { Key = "agent:main:a", CreatedAt = 100, UpdatedAt = new DateTime(2026, 1, 1) };
        var b = new SessionInfo { Key = "agent:main:b", CreatedAt = 200, UpdatedAt = new DateTime(2026, 1, 2) };

        Assert.Equal(
            new[] { "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([a, b], null, order).Select(s => s.Key));

        var bumped = a.Clone();
        bumped.UpdatedAt = new DateTime(2026, 3, 1);
        Assert.Equal(
            new[] { "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([b, bumped], null, order).Select(s => s.Key));

        var c = new SessionInfo { Key = "agent:main:c", CreatedAt = 300 };
        Assert.Equal(
            new[] { "agent:main:c", "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([bumped, b, c], null, order).Select(s => s.Key));
    }

    [Fact]
    public void Projection_SessionsWithoutCreatedAtFreezeFirstSeenOrder()
    {
        var order = new WorkspaceSessionOrder();
        var l1 = new SessionInfo { Key = "agent:main:l1", UpdatedAt = new DateTime(2026, 1, 1) };
        var l2 = new SessionInfo { Key = "agent:main:l2", UpdatedAt = new DateTime(2026, 1, 2) };

        Assert.Equal(
            new[] { "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([l1, l2], null, order).Select(s => s.Key));

        var bumped = l1.Clone();
        bumped.UpdatedAt = new DateTime(2026, 3, 1);
        Assert.Equal(
            new[] { "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([l2, bumped], null, order).Select(s => s.Key));

        var l3 = new SessionInfo { Key = "agent:main:l3", UpdatedAt = new DateTime(2026, 2, 1) };
        Assert.Equal(
            new[] { "agent:main:l3", "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([bumped, l2, l3], null, order).Select(s => s.Key));

        var n = new SessionInfo { Key = "agent:main:n", CreatedAt = 1 };
        Assert.Equal(
            new[] { "agent:main:n", "agent:main:l3", "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([bumped, l2, l3, n], null, order).Select(s => s.Key));
    }

    [Fact]
    public void Projection_LatestSessionKeyIgnoresPinnedOrderingAndArchivedRows()
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:pinned-but-old", UpdatedAt = new DateTime(2026, 1, 1), Pinned = true, PinnedAt = 900 },
            new SessionInfo { Key = "agent:main:latest", UpdatedAt = new DateTime(2026, 5, 1) },
            new SessionInfo { Key = "agent:main:archived-newest", UpdatedAt = new DateTime(2026, 6, 1), Archived = true },
        };

        Assert.Equal("agent:main:latest", Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Fact]
    public void Projection_ArchivedSessionsAreNotSidebarDestinations()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:archived", UpdatedAt = new DateTime(2026, 3, 1), Archived = true, Unread = true, Pinned = true },
        };

        Assert.Empty(WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder()));
    }

    [Fact]
    public void Projection_WorkingStateFollowsActiveRun()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:live", HasActiveRun = true, Status = "done", UpdatedAt = new DateTime(2026, 3, 1) },
            new SessionInfo { Key = "agent:main:legacy", Status = "running", UpdatedAt = new DateTime(2026, 2, 1) },
            new SessionInfo { Key = "agent:main:stale", HasActiveRun = false, Status = "running", UpdatedAt = new DateTime(2026, 1, 1) },
        };

        var rows = WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder()).ToDictionary(row => row.Key);

        Assert.True(rows["agent:main:live"].IsWorking);
        Assert.True(rows["agent:main:legacy"].IsWorking);
        Assert.False(rows["agent:main:stale"].IsWorking);

    }

    [Fact]
    public void ItemsSync_ArrangeReordersInPlaceAndPreservesIdentity()
    {
        object home = new(), header = new(), empty = new(), a = new(), b = new(), c = new(),
            archivedHeader = new(), newItem = new();
        var items = new List<object> { home, header, empty, a, b, c, archivedHeader };
        items.Remove(b);

        WorkspaceItemsSync.Arrange(items, 3, [c, newItem, a]);

        Assert.Equal(new[] { home, header, empty, c, newItem, a, archivedHeader }, items);
        Assert.Same(c, items[3]);
        Assert.Same(a, items[5]);
        Assert.Same(home, items[0]);
        Assert.Same(archivedHeader, items[6]);
    }

    private static string Source(string folder, string file) =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", folder, file);
}
