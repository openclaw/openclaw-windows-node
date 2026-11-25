// Mounted production root/client/bridge proof against an opt-in synthetic loopback task producer.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using static Microsoft.UI.Reactor.Factories;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    private static readonly TimeSpan TaskProofStepTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);

    [NativeKeyboardTheory]
    [InlineData(480, "local", false)]
    [InlineData(480, "local", true)]
    [InlineData(480, "dismiss", true)]
    [InlineData(1100, "local", false)]
    [InlineData(480, "session", false)]
    [InlineData(1100, "session", false)]
    [InlineData(480, "worktree", false)]
    [InlineData(1100, "worktree", false)]
    [InlineData(480, "cloud", false)]
    [InlineData(1100, "cloud", false)]
    [InlineData(480, "dismiss", false)]
    [InlineData(1100, "dismiss", false)]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_ProductionRootRetainsConversationAndDraft(int width, string mode, bool compact)
    {
        var captureMode = compact ? "compact-" + mode : mode;
        using var temp = new TempDirectory();
        var scenario = GatewayScenario.CreateTaskSuggestions();
        var tasks = scenario.TaskSuggestions!;
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionPublications = 0;
        client.SessionsUpdated += (_, sessions) =>
        {
            Interlocked.Increment(ref sessionPublications);
            if (sessions.Any(row => row.Key == GatewayScenario.MainSessionKey)) ready.TrySetResult();
        };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TaskProofStepTimeout);
        using var bridge = new GatewayClientChatBridge(client);
        await using var source = new OpenClawChatDataProvider(bridge, null,
            temp.Combine("tools.json"), temp.Combine("attachments.json"), temp.Combine("last-state.json"));
        await source.LoadHistoryAsync(GatewayScenario.MainSessionKey);
        try
        {
            await WithChatAsync(width, async (surface, _, session, _) =>
            {
                nint handle = 0;
                TextBox? editor = null;
                try
                {
                await ui.RunOnUIAsync(() =>
                {
                    var scale = surface.XamlRoot.RasterizationScale;
                    _captureWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(surface.Height * scale)));
                    handle = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow);
                });
                await TaskCardReadyAsync(surface);
                await NativeKeyboardProof.ActivateAsync(handle);
                await ui.RunOnUIAsync(() =>
                {
                    editor = FindControl<TextBox>(surface, "ChatComposerInput");
                    editor.Text = "Retain this exact unsent suggested-task draft.";
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => session.ViewModel.Draft == "Retain this exact unsent suggested-task draft.", "native draft accepted"));
                await TaskButtonAsync(surface, "Show instructions", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(TaskCard(surface)).Any(text => text.Text == FixtureTaskSuggestions.Prompt && text.Visibility == Visibility.Visible),
                    "literal instructions expanded"));
                await ui.RunOnUIAsync(() =>
                {
                    var prompt = Assert.Single(FindDescendants<TextBlock>(TaskCard(surface)), text => text.Text == FixtureTaskSuggestions.Prompt);
                    Assert.True(prompt.IsTextSelectionEnabled);
                    Assert.Contains("<instructions> and **literal text**", prompt.Text);
                });
                await TaskCaptureAsync(surface, handle, $"tasks-{width}-{captureMode}-instructions");
                await TaskButtonAsync(surface, "Copy instructions", handle, VirtualKey.Space);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<Button>(TaskCard(surface)).Any(button => Equals(button.Content, "Instructions copied")), "copy feedback"));
                await ui.RunOnUIAsync(async () => Assert.Equal(FixtureTaskSuggestions.Prompt, await Clipboard.GetContent().GetTextAsync()));
                await TaskButtonAsync(surface, "Hide instructions", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(TaskCard(surface)).Any(text => text.Text == FixtureTaskSuggestions.Prompt && text.Visibility == Visibility.Collapsed),
                    "instructions collapsed before the next action"));
                if (mode == "dismiss")
                {
                    await TaskButtonAsync(surface, "Dismiss task", handle, VirtualKey.Enter);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is null, "dismissed card removed by authoritative refresh"));
                    Assert.Equal(1, tasks.Dismissals);
                    Assert.Empty(tasks.Acceptances);
                }
                else
                {
                    var launch = "Start in a new session";
                    if (mode != "local")
                    {
                        await TaskButtonAsync(surface, "Launch options", handle, VirtualKey.Enter);
                        launch = mode == "session" ? "Start in this conversation" : mode == "worktree" ? "Start in new worktree" : "Start in cloud";
                        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                            FindDescendants<TextBox>(TaskCard(surface)).Any(input => AutomationProperties.GetName(input) == "Configured cloud profile ID" && input is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }),
                            "launch fields mounted"));
                        await ui.RunOnUIAsync(() =>
                        {
                            var card = TaskCard(surface);
                            var repository = Assert.Single(FindDescendants<TextBox>(card), input => AutomationProperties.GetName(input) == "Repository path on Gateway");
                            Assert.Equal(Visibility.Collapsed, repository.Visibility);
                            Assert.Equal("", repository.Text);
                            if (mode == "cloud")
                                Assert.Single(FindDescendants<TextBox>(card), input => AutomationProperties.GetName(input) == "Configured cloud profile ID").Text = "fixture-cloud";
                        });
                        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                            FindDescendants<Button>(TaskCard(surface)).Any(button => Equals(button.Content, launch) && button.IsEnabled), "selected launch enabled"));
                        await TaskCaptureAsync(surface, handle, $"tasks-{width}-{captureMode}-options");
                    }
                    var listsBefore = server.Requests.Count(request => request.Method == "sessions.list");
                    var publicationsBefore = Volatile.Read(ref sessionPublications);
                    await TaskButtonAsync(surface, launch, handle, VirtualKey.Enter);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        source.TaskSuggestions!.Snapshot().Cards.Any(card => card.StartedKey is not null), "matching launch acknowledgment"));
                    await server.WaitForCompletedRequestAsync("sessions.list", occurrence: listsBefore + 1).WaitAsync(TaskProofStepTimeout);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        Volatile.Read(ref sessionPublications) > publicationsBefore, "refreshed sessions published by the real client"));
                    var refreshed = await source.LoadAsync();
                    var acceptedKey = Assert.Single(source.TaskSuggestions!.Snapshot().Cards).StartedKey;
                    Assert.Contains(refreshed.Threads, thread => thread.Id == acceptedKey);
                    var request = Assert.Single(tasks.Acceptances);
                    Assert.Equal(FixtureTaskSuggestions.TaskId, request.GetProperty("taskId").GetString());
                    Assert.Equal(mode, request.GetProperty("mode").GetString());
                    Assert.False(request.TryGetProperty("cwd", out _));
                    Assert.Equal(mode == "cloud", request.TryGetProperty("cloudProfileId", out _));
                    if (mode == "cloud") Assert.Equal("fixture-cloud", request.GetProperty("cloudProfileId").GetString());
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        FindDescendants<TextBlock>(TaskCard(surface)).Any(text => text.Text == "Task started. Find it in Sessions."), "native start feedback"));
                }
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(editor, FindControl<TextBox>(surface, "ChatComposerInput"));
                    Assert.Equal("Retain this exact unsent suggested-task draft.", editor!.Text);
                    Assert.Equal(editor.Text, session.ViewModel.Draft);
                    Assert.Equal(GatewayScenario.MainSessionKey, session.ViewModel.Inputs!.CurrentThread.Id);
                });
                Assert.DoesNotContain(server.Requests, request => request.Method is "chat.send" or "sessions.create");
                await TaskCaptureAsync(surface, handle, $"tasks-{width}-{captureMode}-result", panelExpected: mode != "dismiss");
                }
                catch
                {
                    if (handle != 0 && Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } directory)
                    {
                        try { await SaveTaskWindowAsync(handle, directory, $"tasks-{width}-{captureMode}-failure"); }
                        catch (Exception capture) { Console.WriteLine("Failure capture unavailable: " + capture.GetType().Name); }
                    }
                    throw;
                }
            }, renderProvider: source, initialThreadId: GatewayScenario.MainSessionKey, interactiveKeyboard: true, compact: compact);
        }
        finally { await client.DisconnectAsync(); }
    }

    [NativeKeyboardTheory]
    [InlineData(480, "identity-unavailable")]
    [InlineData(1100, "identity-unavailable")]
    [InlineData(480, "worktree-default")]
    [InlineData(1100, "worktree-default")]
    [InlineData(480, "relative-path")]
    [InlineData(1100, "relative-path")]
    [InlineData(480, "dismiss-failed")]
    [InlineData(1100, "dismiss-failed")]
    [InlineData(480, "unrelated-refresh")]
    [InlineData(1100, "unrelated-refresh")]
    [InlineData(480, "admin-required")]
    [InlineData(1100, "admin-required")]
    [InlineData(480, "agent-null")]
    [InlineData(1100, "agent-null")]
    [InlineData(480, "disclosure")]
    [InlineData(1100, "disclosure")]
    [InlineData(480, "new-session-label")]
    [InlineData(1100, "new-session-label")]
    [InlineData(480, "hide-current")]
    [InlineData(1100, "hide-old")]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_BoundedCardContracts(int width, string contract)
    {
        var gateway = new TaskPanelGateway { AdministratorRequired = contract == "admin-required" };
        var custody = contract == "identity-unavailable" ? new ChatTaskSuggestionCustody() : null;
        custody?.SelectGateway("fixture-gateway");
        gateway.IdentityAvailable = contract != "identity-unavailable";
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: custody is null ? null : "fixture-gateway");
        owner.Start();
        if (contract == "relative-path")
        {
            gateway.RequireRepository = true;
            await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
            Assert.Null(Assert.Single(gateway.Accepts).Cwd);
            Assert.True(Assert.Single(owner.Snapshot().Cards).RepositoryRequired);
        }
        if (contract == "dismiss-failed") { gateway.FailDismiss = true; await owner.DismissAsync(FixtureTaskSuggestions.TaskId); }
        if (contract == "unrelated-refresh") { gateway.FailRead = true; await owner.RefreshAsync(); }
        if (contract.StartsWith("hide-", StringComparison.Ordinal))
        {
            await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree", Cwd: "/original"));
            await owner.RefreshAsync(); // Still pending can overtake a producer's original pre-claim await.
            Assert.Equal("/original", Assert.Single(owner.Snapshot().Cards).Attempt!.Cwd);
            if (contract == "hide-old") gateway.Connect(2);
        }
        Window? window = null;
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        Border? surface = null;
        nint handle = 0;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl { RequestedTheme = themeScope.ElementTheme };
                host.Mount(_ => Component<ReactorTaskSuggestionPanel, ReactorTaskSuggestionPanelProps>(
                    new(owner, contract == "unrelated-refresh" ? "unrelated-session" : GatewayScenario.MainSessionKey, contract == "agent-null" ? null : "main", width, 720)));
                surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Background=\"{ThemeResource NavigationViewContentBackground}\" />");
                surface.Width = width; surface.Height = 720; surface.Child = host;
                surface.HorizontalAlignment = HorizontalAlignment.Left; surface.VerticalAlignment = VerticalAlignment.Top;
                surface.RequestedTheme = themeScope.ElementTheme;
                window = new Window { Content = surface, SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop() };
                window.AppWindow.MoveAndResize(new(80, 80, width + 32, 800));
                window.Activate();
                handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            });
            await NativeKeyboardProof.ActivateAsync(handle);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => surface!.IsLoaded && surface.ActualWidth > 0, "panel host loaded"));
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                window!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(720 * scale)));
            });
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            if (contract == "unrelated-refresh")
            {
                await ui.RunOnUIAsync(() => Assert.Null(FindOptional<ScrollViewer>(surface!, "ChatTaskSuggestions")));
                return;
            }
            if (contract == "identity-unavailable")
            {
                await ui.RunOnUIAsync(() =>
                {
                    Assert.NotNull(FindOptional<ScrollViewer>(surface!, "ChatTaskSuggestions"));
                    Assert.Contains(FindDescendants<TextBlock>(surface!), text => text.Text == "Suggested tasks need a verified Gateway operator identity. Check your Gateway access, then retry.");
                    Assert.Null(FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId));
                });
                await TaskCaptureAsync(surface!, handle, $"contracts-{width}-{contract}");
                gateway.IdentityAvailable = true;
                await TaskButtonAsync(surface!, "Refresh tasks", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is not null, "identity retry restores task card"));
                return;
            }
            // Assert the observable card rather than waiting on a missing-control timeout.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<ScrollViewer>(surface!, "ChatTaskSuggestions") is not null ||
                contract == "agent-null", "task panel published"));
            await ui.RunOnUIAsync(() => Assert.NotNull(FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId)));
            if (contract == "worktree-default")
            {
                await TaskButtonAsync(surface!, "Launch options", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => Assert.DoesNotContain(FindDescendants<TextBox>(TaskCard(surface!)), input => input.Visibility == Visibility.Visible && AutomationProperties.GetName(input) == "Repository path on Gateway"));
                await TaskButtonAsync(surface!, "Start in new worktree", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => gateway.Accepts.Count == 1, "default worktree dispatch observed"));
                Assert.Null(Assert.Single(gateway.Accepts).Cwd);
                return;
            }
            if (contract == "relative-path")
            {
                await TaskButtonAsync(surface!, "Launch options", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => Assert.Single(FindDescendants<TextBox>(TaskCard(surface!)), input => AutomationProperties.GetName(input) == "Repository path on Gateway").Text = "relative-repository");
                await TaskButtonAsync(surface!, "Start in new worktree", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => Assert.Single(owner.Snapshot().Cards).RepositoryRequired, "local relative path refusal"));
            }
            if (contract is "relative-path" or "dismiss-failed")
            {
                var expected = contract == "relative-path"
                    ? "Enter an absolute repository path on the Gateway with a commit. The task has not started."
                    : "The task could not be dismissed. Try again.";
                await ui.RunOnUIAsync(() =>
                {
                    var guidance = Assert.Single(FindDescendants<TextBlock>(surface!), text => text.Text == expected);
                    guidance.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                });
                Assert.Equal(contract == "relative-path" ? 1 : 0, gateway.Accepts.Count);
                Assert.Null(Assert.Single(owner.Snapshot().Cards).Attempt);
                await TaskCaptureAsync(surface!, handle, $"contracts-{width}-{contract}");
                if (contract == "relative-path")
                {
                    await ui.RunOnUIAsync(() => Assert.Single(FindDescendants<TextBox>(TaskCard(surface!)), input => AutomationProperties.GetName(input) == "Repository path on Gateway").Text = "/valid-repository");
                    await TaskButtonAsync(surface!, "Start in new worktree", handle, VirtualKey.Enter);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => gateway.Accepts.Count == 2, "corrected absolute path dispatched"));
                    Assert.Equal("/valid-repository", gateway.Accepts.Last().Cwd);
                }
            }
            if (contract == "admin-required")
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Contains(FindDescendants<TextBlock>(surface!), text => text.Text == "Administrator access to the Gateway is required to start suggested tasks.");
                    Assert.False(Assert.Single(FindDescendants<Button>(surface!), button => Equals(button.Content, "Start in a new session")).IsEnabled);
                    Assert.True(Assert.Single(FindDescendants<Button>(surface!), button => Equals(button.Content, "Dismiss task")).IsEnabled);
                });
            if (contract == "new-session-label")
                await ui.RunOnUIAsync(() => Assert.Contains(FindDescendants<Button>(surface!), button => Equals(button.Content, "Start in a new session")));
            if (contract is "agent-null" or "admin-required" or "new-session-label")
                await TaskCaptureAsync(surface!, handle, $"contracts-{width}-{contract}");
            if (contract == "disclosure")
            {
                ToggleButton? disclosure = null;
                await ui.RunOnUIAsync(() =>
                {
                    disclosure = Assert.Single(FindDescendants<ToggleButton>(surface!), control =>
                        AutomationProperties.GetAutomationId(control) == "ChatTaskInstructions");
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(disclosure!);
                    Assert.NotNull(peer.GetPattern(PatternInterface.Toggle));
                    Assert.False(disclosure.IsChecked);
                    Assert.True(disclosure.Focus(FocusState.Keyboard));
                });
                await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Enter));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => disclosure!.IsChecked == true &&
                    new ToggleButtonAutomationPeer(disclosure).GetName() == "Hide instructions", "accessible expanded instruction toggle"));
                await TaskCaptureAsync(surface!, handle, $"contracts-{width}-{contract}");
            }
            if (contract.StartsWith("hide-", StringComparison.Ordinal))
            {
                await ui.RunOnUIAsync(() => Assert.Contains(FindDescendants<Button>(surface!), button => Equals(button.Content, "Hide locally")));
                var listsBefore = gateway.Lists;
                await TaskButtonAsync(surface!, "Hide locally", handle, VirtualKey.Space);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is null, "local hidden receipt removed"));
                Assert.Equal(listsBefore, gateway.Lists);
                Assert.Single(gateway.Accepts);
                Assert.Equal(0, gateway.Dismissals);
                await owner.RefreshAsync();
                Assert.Empty(owner.Snapshot().Cards); // Same pending ID must not recreate launch options.
                gateway.Rows = [gateway.Rows[0], gateway.Rows[0] with { Id = "unrelated-new-task" }];
                await owner.RefreshAsync();
                Assert.Equal("unrelated-new-task", Assert.Single(owner.Snapshot().Cards).Suggestion.Id);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_unrelated-new-task") is not null, "unrelated task stays visible"));
                Assert.Single(gateway.Accepts);
                await TaskCaptureAsync(surface!, handle, $"contracts-{width}-{contract}");
            }
        }
        catch
        {
            if (handle != 0 && Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } directory)
                try { await SaveTaskWindowAsync(handle, directory, $"contracts-{width}-{contract}-failure"); }
                catch (Exception capture) { Console.WriteLine("Failure capture unavailable: " + capture.GetType().Name); }
            throw;
        }
        finally
        {
            await ui.RunOnUIAsync(() => { host?.Dispose(); if (window is not null) { window.Content = null; window.Close(); } themeScope?.Dispose(); });
        }
    }

    private sealed class TaskPanelGateway : IChatTaskSuggestionGateway
    {
        public long? TaskConnectionEpoch { get; private set; } = 1;
        public bool TaskSuggestionsSupported => true;
        public bool AdministratorRequired;
        public bool TaskAcceptanceSupported => !AdministratorRequired;
        public bool TaskAdministratorRequired => AdministratorRequired;
        public bool TaskDismissalSupported => true;
        public event EventHandler? TaskSuggestionsInvalidated { add { } remove { } }
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public GatewayTaskSuggestion[] Rows = [new(FixtureTaskSuggestions.TaskId, FixtureTaskSuggestions.Title,
            FixtureTaskSuggestions.Prompt, "Synthetic task", FixtureTaskSuggestions.Cwd, GatewayScenario.MainSessionKey, "main", 17)];
        public List<GatewayTaskAcceptance> Accepts = [];
        public int Lists;
        public int Dismissals;
        public bool RequireRepository;
        public bool IdentityAvailable = true;
        public Task<string?> ReadTaskOperatorIdentityAsync(long epoch) => Task.FromResult(IdentityAvailable ? "verified-fixture-operator" : null);
        public bool FailRead;
        public bool FailDismiss;
        public Task<GatewayTaskSuggestionList> ListTaskSuggestionsAsync(long epoch)
        { Lists++; return FailRead ? Task.FromException<GatewayTaskSuggestionList>(new TimeoutException()) : Task.FromResult(new GatewayTaskSuggestionList(true, Rows)); }
        public Task<string> AcceptTaskSuggestionAsync(GatewayTaskAcceptance request, long epoch)
        {
            Accepts.Add(request);
            return RequireRepository && request.Mode == "worktree" && request.Cwd is null
                ? Task.FromException<string>(GatewayRequestErrors.Create("synthetic", "INVALID_REQUEST", GatewayRequestErrors.TaskWorktreeSourceRequired))
                : Task.FromException<string>(new TimeoutException());
        }
        public Task<bool> DismissTaskSuggestionAsync(string id, long epoch)
        { Dismissals++; return FailDismiss ? Task.FromException<bool>(new TimeoutException()) : Task.FromResult(false); }
        public Task RequestSessionsAsync() => Task.CompletedTask;
        public void Connect(long epoch) { TaskConnectionEpoch = epoch; StatusChanged?.Invoke(this, ConnectionStatus.Connected); }
    }

    private static Border TaskCard(DependencyObject surface) => FindControl<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId);

    private Task TaskCardReadyAsync(DependencyObject surface) => ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true, ActualWidth: > 0 }, "native suggested-task card"));

    private async Task TaskButtonAsync(Border surface, string label, nint handle, VirtualKey key)
    {
        ButtonBase? target = null;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            target = FindDescendants<ButtonBase>(label == "Refresh tasks" ? surface : TaskCard(surface)).SingleOrDefault(button =>
                (button is Button && Equals(button.Content, label)) || (button is ToggleButton && AutomationProperties.GetName(button) == label));
            return target is { IsLoaded: true, IsEnabled: true, IsTabStop: true, ActualWidth: > 0 }
                && target.Focus(FocusState.Keyboard);
        }, label + " ready and focusable"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(label, FrameworkElementAutomationPeer.CreatePeerForElement(target!).GetName());
            Assert.True(target!.IsTabStop);
            Assert.Equal(FocusState.Keyboard, target.FocusState);
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        });
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot), target), label + " focused"));
        await AssertTaskGeometryAsync(surface);
        await ui.RunOnUIAsync(() =>
        {
            var panel = Bounds(FindControl<ScrollViewer>(surface, "ChatTaskSuggestions"), surface);
            var bounds = Bounds(target!, surface);
            Assert.True(bounds.Top >= panel.Top - 1 && bounds.Bottom <= panel.Bottom + 1,
                $"Focused task action {label} must be fully visible. Action {bounds}; viewport {panel}.");
            var margin = target!.FocusVisualMargin;
            Assert.True(bounds.Top + margin.Top >= panel.Top - .5 && bounds.Bottom - margin.Bottom <= panel.Bottom + .5,
                $"Focused task action {label} must retain its complete native focus outline. Action {bounds}; focus margin {margin}; viewport {panel}.");
            foreach (var text in FindDescendants<TextBlock>(target!))
                Assert.False(text.IsTextTrimmed, "Task action text must not be clipped.");
        });
        if ((label.StartsWith("Start in ", StringComparison.Ordinal) || label == "Hide locally") &&
            Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } directory)
        {
            var identity = await ui.RunOnUIAsync(() => Task.FromResult((Width: surface.Width, Compact: surface.Height == 360)));
            await SaveTaskWindowAsync(handle, directory, $"tasks-{identity.Width}-{(identity.Compact ? "compact-" : "")}{label.Replace(' ', '-')}-focused");
        }
        await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, key));
    }

    private async Task AssertTaskGeometryAsync(Border surface)
    {
        string? previous = null;
        var stable = 0;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            var panel = FindControl<ScrollViewer>(surface, "ChatTaskSuggestions");
            var sample = string.Join(";", FindDescendants<Button>(panel).Select(button => Bounds(button, surface).ToString()));
            stable = sample == previous ? stable + 1 : 0;
            previous = sample;
            return stable >= 2;
        }, "task geometry stable"));
        await ui.RunOnUIAsync(() =>
        {
            var panel = FindControl<ScrollViewer>(surface, "ChatTaskSuggestions");
            Assert.InRange(panel.ActualHeight, 1, 320);
            foreach (var button in FindDescendants<Button>(panel).Where(button => button.Visibility == Visibility.Visible && button.ActualWidth > 0))
            {
                var bounds = Bounds(button, surface);
                Assert.InRange(bounds.Left, -1, surface.ActualWidth + 1);
                Assert.InRange(bounds.Right, -1, surface.ActualWidth + 1);
                Assert.True(button.ActualHeight >= 31, "Native action target must not collapse.");
            }
        });
    }

    private async Task TaskCaptureAsync(Border surface, nint handle, string name, bool panelExpected = true)
    {
        if (panelExpected)
            await AssertTaskGeometryAsync(surface);
        else
            await ui.RunOnUIAsync(() => Assert.Null(FindOptional<ScrollViewer>(surface, "ChatTaskSuggestions")));
        if (Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } directory)
            await SaveTaskWindowAsync(handle, directory, name);
    }

    private async Task SaveTaskWindowAsync(nint handle, string directory, string name)
    {
        await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
        Directory.CreateDirectory(directory);
        OwnedWindowCapture.SaveCurrentProcessWindow(handle, "suggested-tasks-fixture", directory, name + ".png");
    }
}
