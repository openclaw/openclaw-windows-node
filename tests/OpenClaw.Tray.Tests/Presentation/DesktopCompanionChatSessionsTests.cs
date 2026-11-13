// Verifies dedicated lobster session identity and in-flight creation ownership.
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class DesktopCompanionChatSessionsTests
{
    [Fact]
    public async Task AlreadyCanceledOpenDoesNotCreateAConversation()
    {
        var sessions = new DesktopCompanionChatSessions();
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.GetOrCreateAsync(
            "gateway", () => { calls++; return Task.FromResult("unused"); }, closed.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ClosingAndReopeningDuringCreationDoesNotCreateAnotherConversation()
    {
        var sessions = new DesktopCompanionChatSessions();
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string> Create() { calls++; return created.Task; }
        using var closing = new CancellationTokenSource();
        var first = sessions.GetOrCreateAsync("gateway-a", Create, closing.Token);
        closing.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var reopened = sessions.GetOrCreateAsync("gateway-a", Create, default);
        Assert.False(reopened.IsCompleted);
        created.SetResult("agent:main:lobster-a");

        Assert.Equal("agent:main:lobster-a", await reopened);
        Assert.Equal("agent:main:lobster-a", await sessions.GetOrCreateAsync("gateway-a", Create, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GatewaysRetainIndependentConversationsAndExplicitNewSelections()
    {
        var sessions = new DesktopCompanionChatSessions();
        Assert.Equal("chat-a", await sessions.GetOrCreateAsync("a", () => Task.FromResult("chat-a"), default));
        Assert.Equal("chat-b", await sessions.GetOrCreateAsync("b", () => Task.FromResult("chat-b"), default));
        sessions.Select("a", "new-chat-a");
        Assert.Equal("new-chat-a", await sessions.GetOrCreateAsync("a", () => throw new Exception("must not create"), default));
        Assert.Equal("chat-b", await sessions.GetOrCreateAsync("b", () => throw new Exception("must not create"), default));
    }

    [Fact]
    public async Task FailedCreationSurfacesAndAnExplicitRetryCanCreate()
    {
        var sessions = new DesktopCompanionChatSessions();
        await Assert.ThrowsAsync<IOException>(() => sessions.GetOrCreateAsync(
            "gateway", () => Task.FromException<string>(new IOException("unavailable")), default));
        Assert.Equal("chat", await sessions.GetOrCreateAsync("gateway", () => Task.FromResult("chat"), default));
    }
}
