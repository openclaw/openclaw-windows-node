using OpenClaw.Chat;
using OpenClaw.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Minimal <see cref="IChatDataProvider"/> fake for <see cref="ChatComposerSession"/>/
/// <see cref="ChatComposerFactory"/> tests that only need a valid provider reference,
/// not real chat behavior. All members are no-ops.
/// </summary>
internal sealed class FakeChatDataProviderForComposerTests : IChatDataProvider
{
    public string DisplayName => "fake";

#pragma warning disable CS0067 // Never raised: this fake only needs a valid provider reference.
    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067

    public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by session/factory tests.");

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
