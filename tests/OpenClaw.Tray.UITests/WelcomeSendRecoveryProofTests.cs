using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation.Adapters;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WelcomeSendRecoveryProofTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task RejectedChips_KeepVisibleErrorAndAllowAnotherSend(int width)
    {
        const string error = "The selected model is unavailable. Select a supported model and try again.";
        var directory = Path.Combine(Path.GetTempPath(), $"welcome-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bridge = new RejectingBridge(error);
        var provider = new OpenClawChatDataProvider(
            bridge,
            action =>
            {
                if (!ui.Dispatcher.TryEnqueue(() => action()))
                    throw new InvalidOperationException("The test dispatcher rejected a chat update.");
            },
            Path.Combine(directory, "tools.json"),
            Path.Combine(directory, "attachments.json"),
            Path.Combine(directory, "last-chat.json"));
        ReactorHostControl? host = null;
        ChatComposerSession? session = null;
        ChatThemeProofScope? theme = null;
        Border? surface = null;
        ChatDataSnapshot? latest = null;
        provider.Changed += (_, args) => latest = args.Snapshot;

        try
        {
            await ui.ResetContainerAsync();
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope("Light");
                session = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)).Create(
                    provider, new(null, null, null, null, null), initialSpeakerMuted: false);
                host = new ReactorHostControl();
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, session, InitialThreadId: "main")));
                surface = new Border { Width = width, Height = 720, Child = host };
                ui.Container.Children.Add(surface);
            });

            foreach (var suggestion in new[] { "What can you do?", "Give me a quick tour of OpenClaw" })
            {
                await ui.RunOnUIAsync(async () =>
                {
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => FindSuggestion(surface!, suggestion) is { IsLoaded: true, IsEnabled: true },
                        "welcome suggestion ready");
                    Invoke(FindSuggestion(surface!, suggestion)!);
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => FindError(surface!) is { IsOpen: true, ActualHeight: > 0 }
                            && FindSuggestion(surface!, suggestion) is { IsLoaded: true, IsEnabled: true },
                        "failed welcome send recovered with visible error and enabled choices");

                    var banner = FindError(surface!);
                    Assert.Equal(InfoBarSeverity.Error, banner.Severity);
                    Assert.Equal(Visibility.Visible, banner.Visibility);
                    Assert.Contains(error, banner.Message);
                    Assert.InRange(banner.ActualWidth, 1, width);
                    Assert.Empty(latest!.Timelines["main"].Entries);
                    Assert.Contains(error, latest.Timelines["main"].WelcomeSendError);
                });
            }

            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal(2, bridge.SendCount);
                bridge.Reject = false;
                Invoke(FindSuggestion(surface!, "What can you do?")!);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => bridge.SendCount == 3 && !FindError(surface!).IsOpen
                        && latest!.Timelines["main"].Entries.Any(entry => entry.Kind == ChatTimelineItemKind.User),
                    "accepted welcome send clears recovered error");
                Assert.Null(latest!.Timelines["main"].WelcomeSendError);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                session?.Dispose();
                if (surface is not null)
                {
                    surface.Child = null;
                    ui.Container.Children.Remove(surface);
                }
                host?.Dispose();
                theme?.Dispose();
            });
            await provider.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Button? FindSuggestion(DependencyObject root, string suggestion) =>
        TestSupport.FindDescendants<Button>(root).FirstOrDefault(button =>
            AutomationProperties.GetName(button) == suggestion);

    private static InfoBar FindError(DependencyObject root) =>
        TestSupport.FindDescendants<InfoBar>(root).Single(bar =>
            AutomationProperties.GetAutomationId(bar) == "ChatWelcomeSendError");

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private sealed class RejectingBridge(string error) : IChatGatewayBridge
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);
        public bool Reject { get; set; } = true;
        public bool IsConnected => true;
        public ConnectionStatus CurrentStatus => ConnectionStatus.Connected;
        public string MainSessionKey => "main";
        public bool HasHandshakeSnapshot => true;
        public SessionInfo[] GetSessionList() => [new() { Key = "main", IsMain = true, DisplayName = "Main" }];
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }
        public Task<ChatWelcomeCatalog?> GetWelcomeCatalogAsync(string locale) =>
            Task.FromResult<ChatWelcomeCatalog?>(new(locale, "Welcome", "Recent chats",
            [
                new("capabilities", "What can you do?", " \r\nWhat can you do?\r\n "),
                new("tour", "Give me a quick tour of OpenClaw", "Give me a quick tour of OpenClaw"),
            ]));
        public Task<ChatSendResult> SendChatMessageForRunAsync(
            string message, string? sessionKey, string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null, string? idempotencyKey = null)
        {
            Interlocked.Increment(ref _sendCount);
            return Reject
                ? Task.FromException<ChatSendResult>(new InvalidOperationException(error))
                : Task.FromResult(new ChatSendResult { Status = "started", RunId = idempotencyKey });
        }
        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null) =>
            SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments);
        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) =>
            Task.FromResult(new CommandCatalog { IsSupported = false });
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
            Task.FromResult(new ChatHistoryInfo { SessionKey = sessionKey ?? "main" });
        public Task PatchSessionModelAsync(string sessionKey, string model) => Task.CompletedTask;
        public Task ClearSessionModelAsync(string sessionKey) => Task.CompletedTask;
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => Task.CompletedTask;
        public Task ClearSessionThinkingLevelAsync(string sessionKey) => Task.CompletedTask;
        public Task SendChatAbortAsync(string runId, string? sessionKey = null) => Task.CompletedTask;
        public Task ResolveExecApprovalAsync(string approvalId, string decision) => Task.CompletedTask;
        public void Dispose() { }
#pragma warning disable CS0067
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionUsageSnapshot>? SessionUsageSnapshotUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
#pragma warning restore CS0067
    }
}
