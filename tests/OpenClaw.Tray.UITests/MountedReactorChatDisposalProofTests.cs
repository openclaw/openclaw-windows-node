using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
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
            var factory = new ChatComposerFactory(dispatcher);
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
}
