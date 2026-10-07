using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
using OpenClaw.TestSupport;
using OpenClawTray.Services;
using OpenClawTray.Pages;
using OpenClaw.SetupEngine;
using OpenClaw.Connection;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Real-WinUI proof that <see cref="MountedReactorChat.Dispose"/> is first-wins and
/// idempotent: session/callback/host/target teardown happens exactly once even when
/// <c>Dispose()</c> is called repeatedly. Runs on the shared <see cref="UIThreadFixture"/>
/// because <see cref="ReactorHostControl"/>/<see cref="Border"/> require a live WinUI
/// dispatcher and <c>XamlRoot</c>.
/// </summary>
[Collection(UICollection.Name)]
public sealed class MountedReactorChatDisposalProofTests
{
    private readonly UIThreadFixture _ui;

    public MountedReactorChatDisposalProofTests(UIThreadFixture ui) => _ui = ui;

    private sealed class NoopChatDataProvider : IChatDataProvider
    {
        public string DisplayName => "noop";
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Dispose_RepeatedCalls_TearDownExactlyOnce()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var dispatcher = new WinUIDispatcher(_ui.Dispatcher);
            using var temp = new TempDirectory();
            using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
            var factory = new ChatComposerFactory(dispatcher, store);
            var provider = new NoopChatDataProvider();
            var hostActions = new ChatComposerHostActions(null, null, null, null, null);
            var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

            var target = new Border();
            _ui.Container.Children.Add(target);
            var host = new ReactorHostControl();
            host.Mount(_ => Empty());
            target.Child = host;

            var callbacks = new ReactorChatHostCallbacks
            {
                AttachFiles = _ => { },
            };
            var mounted = new MountedReactorChat(target, host, callbacks, session);

            // First call performs real teardown.
            mounted.Dispose();
            Assert.Null(target.Child);
            Assert.Null(callbacks.AttachFiles);

            // Second (and third) calls must be pure no-ops: no exception, no
            // observable change, and — critically — no second call into
            // ReactorHostControl.Dispose(), which is not itself guaranteed
            // idempotent by the Reactor library.
            var secondCallException = Record.Exception(mounted.Dispose);
            var thirdCallException = Record.Exception(mounted.Dispose);

            Assert.Null(secondCallException);
            Assert.Null(thirdCallException);
            Assert.Null(target.Child);
            Assert.Null(callbacks.AttachFiles);

            _ui.Container.Children.Remove(target);
        });
    }

    [Fact]
    public async Task CreateComposerSession_PreservesSandboxAndSessionNavigationActions()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var dispatcher = new WinUIDispatcher(_ui.Dispatcher);
            using var temp = new TempDirectory();
            using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
            var sandboxNavigations = 0;
            var sessionNavigations = 0;
            string? selectedSession = null;
            using var session = ReactorChatHostExtensions.CreateComposerSession(
                new Border(),
                new ChatComposerFactory(dispatcher, store),
                new NoopChatDataProvider(),
                onVoiceRequest: null,
                onAttachClick: null,
                onSettingsClick: null,
                onSpeakerMuteChanged: null,
                initialMuted: false,
                onSandboxSettingsClick: () => sandboxNavigations++,
                onSessionNavigationStarting: () => sessionNavigations++,
                onSessionSelected: key => selectedSession = key);

            session.HostActions.SandboxSettingsNavigation!();
            Assert.Equal(1, sandboxNavigations);
            Assert.Equal(0, sessionNavigations);
            Assert.Null(selectedSession);

            session.HostActions.SessionNavigationStarting!();
            session.HostActions.SessionSelected!("agent:main:selected");
            Assert.Equal(1, sandboxNavigations);
            Assert.Equal(1, sessionNavigations);
            Assert.Equal("agent:main:selected", selectedSession);
        });
    }

    [Theory]
    [InlineData("window")]
    [InlineData("target")]
    [InlineData("provider")]
    public async Task FailedMount_DisposesSessionAndLeavesExistingContent(string invalidArgument)
    {
        await _ui.RunOnUIAsync(() =>
        {
            var dispatcher = new WinUIDispatcher(_ui.Dispatcher);
            using var temp = new TempDirectory();
            var settings = new SettingsManager(temp.Path) { SystemRunSandboxEnabled = true };
            using var store = new SettingsStore(settings, dispatcher);
            var provider = new NoopChatDataProvider();
            using var session = new ChatComposerFactory(dispatcher, store).Create(
                provider, new ChatComposerHostActions(null, null, null, null, null), false);
            var existingContent = new TextBlock { Text = "Keep existing content" };
            var target = new Border { Child = existingContent };
            var window = new Microsoft.UI.Xaml.Window();
            try
            {
                Assert.Throws<ArgumentNullException>(() => ReactorChatHostExtensions.MountReactorChat(
                    invalidArgument == "window" ? null! : window,
                    invalidArgument == "target" ? null! : target,
                    invalidArgument == "provider" ? null! : provider,
                    session));

                Assert.True(session.ViewModel.IsDisposed);
                Assert.True(session.Controller.IsDisposed);
                Assert.Same(existingContent, target.Child);
                var revision = session.ViewModel.RenderRevision;
                settings.SystemRunSandboxEnabled = false;
                settings.Save();
                Assert.Equal(revision, session.ViewModel.RenderRevision);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SetupVoiceWaitsForVisibleReadySurfaceInsteadOfHiddenRetainedHost()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var page = new ChatPage();
            _ui.Container.Children.Add(page);
            var target = Assert.IsType<Border>(page.FindName("ChatHost"));
            var capture = new TaskCompletionSource<string?>();
            CancellationToken captureToken = default;
            var dispatcher = new WinUIDispatcher(_ui.Dispatcher);
            using var temp = new TempDirectory();
            using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
            var session = new ChatComposerFactory(dispatcher, store).Create(
                new NoopChatDataProvider(),
                new(null, null, (ct, _) => { captureToken = ct; return capture.Task; }, null, null), false);
            var reactor = new ReactorHostControl();
            reactor.Mount(_ => Empty());
            target.Child = reactor;
            var recordings = 0;
            using var mounted = new MountedReactorChat(target, reactor,
                new() { TriggerVoiceRecording = () => recordings++ }, session);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ChatPage).GetField("_reactorHost", fields)!.SetValue(page, mounted);
            typeof(ChatPage).GetField("_pageActive", fields)!.SetValue(page, true);
            var binding = (SetupNativeChatBinding)typeof(ChatPage).GetField("_nativeSetupBinding", fields)!.GetValue(page)!;
            var presentation = (SetupNativeChatPresentation)typeof(ChatPage).GetField("_nativeSetupPresentation", fields)!.GetValue(page)!;
            var request = new SetupNativeNavigationRequest(new(
                new(SetupCompletionIntent.CustodianOnboarding, "test", "endpoint", "provider/model", "main", 1,
                    IdentityBinding: new string('A', 64), SessionKey: "agent:main:main"),
                new(SetupNativeDestination.Chat, "agent:main:main")));
            binding.Bind(request);
            presentation.Bind(request);
            target.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            page.TriggerAutoStartVoice();
            Assert.Equal(0, recordings);
            var consume = typeof(ChatPage).GetMethod("ConsumePendingVoice", fields)!;
            var client = DispatchProxy.Create<IOperatorGatewayClient, UnusedClient>();
            presentation.Evaluate(() => client, _ => true, _ =>
            {
                target.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                consume.Invoke(page, null);
                Assert.Equal(0, recordings);
                return true;
            }, () => Assert.Fail("No block expected"), () => Assert.Fail("No defer expected"));
            consume.Invoke(page, null);
            consume.Invoke(page, null);
            Assert.Equal(1, recordings);
            session.ViewModel.SetDraft("Keep draft when leaving chat");
            session.Controller.StartVoiceRecording();
            typeof(ChatPage).GetMethod("OnUnloaded", fields)!.Invoke(page,
                [page, new Microsoft.UI.Xaml.RoutedEventArgs()]);
            Assert.True(captureToken.IsCancellationRequested);
            Assert.False(session.Controller.IsDisposed);
            Assert.False(session.ViewModel.IsRecording);
            Assert.Equal("Keep draft when leaving chat", session.ViewModel.Draft);
            capture.SetResult("Discard late transcript");
            typeof(ChatPage).GetField("_reactorHost", fields)!.SetValue(page, null);
            _ui.Container.Children.Remove(page);
        });
    }

    public class UnusedClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException("Presentation must not call the client directly.");
    }
}
