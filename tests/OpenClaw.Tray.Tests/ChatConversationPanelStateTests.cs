using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationPanelStateTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long? Epoch = 12;
        public bool Connected = true;
        public string? SigningDeviceId = "operator-a";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_SessionMutationConnectionEpoch" => Epoch,
            "get_IsConnectedToGateway" => Connected,
            "get_AuthenticatedSigningDeviceId" => SigningDeviceId,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    private static SessionInfo Session(string key = "agent:writing:one", string? id = "session-one") =>
        new() { Key = key, SessionId = id, AgentId = "writing" };

    [Theory]
    [InlineData("epoch")]
    [InlineData("principal")]
    [InlineData("gateway")]
    [InlineData("session")]
    [InlineData("disconnected")]
    [InlineData("account")]
    public void CaptureRefusesUnreadyIdentity(string missing)
    {
        var (client, proxy) = Gateway();
        if (missing == "epoch") proxy.Epoch = null;
        if (missing == "principal") proxy.SigningDeviceId = null;
        if (missing == "disconnected") proxy.Connected = false;
        Assert.Null(ChatConversationContext.Capture(
            missing == "gateway" ? null : "gateway-one", client,
            missing == "session" ? null : Session(), missing == "account" ? null : "profile-a"));
    }

    [Fact]
    public void CaptureUsesOnlyExactSessionMetadataWithoutInventingLocationOrAgent()
    {
        var (client, _) = Gateway();
        var context = Assert.IsType<ChatConversationContext>(
            ChatConversationContext.Capture("gateway-one", client, new() { Key = "custom-key" }, "profile-a"));
        Assert.Equal("custom-key", context.Identity.SessionKey);
        Assert.Null(context.Identity.AgentId);
        Assert.Null(context.Identity.SessionId);
        Assert.Null(context.ExecNodeId);
        Assert.Null(context.WorktreeId);
        Assert.Same(client, context.Client);
        Assert.Equal(12, context.ConnectionEpoch);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("epoch")]
    [InlineData("principal")]
    [InlineData("principal-null")]
    [InlineData("session")]
    [InlineData("session-id")]
    [InlineData("agent")]
    [InlineData("environment")]
    [InlineData("worktree")]
    [InlineData("provider")]
    [InlineData("navigation")]
    [InlineData("panel")]
    public void SelectionOrAuthorityChangeRejectsDelayedCompletion(string change)
    {
        var (client, proxy) = Gateway();
        ChatConversationContext? current = ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a");
        using var panels = new ChatConversationPanelState(key =>
            current?.Identity.SessionKey == key ? current : null);
        panels.Bind("agent:writing:one");
        Assert.True(panels.Open("details", "tool-one", "run-one"));
        var operation = Assert.IsType<ChatConversationPanelOperation>(panels.BeginOperation("details", "run-one"));
        Assert.True(operation.IsCurrent);
        switch (change)
        {
            case "client": current = current! with { Client = Gateway().Client }; break;
            case "epoch": proxy.Epoch++; break;
            case "principal": proxy.SigningDeviceId = "operator-b"; break;
            case "principal-null": proxy.SigningDeviceId = null; break;
            case "session": panels.Bind("agent:writing:two"); break;
            case "session-id": current = current! with { Identity = current.Identity with { SessionId = "reset-id" } }; break;
            case "agent": current = current! with { Identity = current.Identity with { AgentId = "other" } }; break;
            case "environment": current = current! with { ExecNodeId = "node-two" }; break;
            case "worktree": current = current! with { WorktreeId = "worktree-two" }; break;
            case "provider": current = null; break;
            case "navigation": panels.Suspend(); break;
            case "panel": panels.Open("files"); break;
        }
        Assert.False(operation.IsCurrent);
        Assert.Null(panels.BeginOperation("details"));
    }

    [Fact]
    public void NavigationCancelsImmediatelyAndReturningRestoresOnlyScopedLayout()
    {
        var (client, _) = Gateway();
        using var panels = new ChatConversationPanelState(key =>
            ChatConversationContext.Capture("gateway-one", client, Session(key, key + "-id"), "profile-a"));
        panels.Bind("one");
        panels.Open("details");
        panels.Resize(512);
        var first = panels.BeginOperation("details")!;
        panels.Suspend();
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        panels.Resume();
        panels.Bind("two");
        Assert.Null(panels.Layout.SelectedPanel);
        panels.Open("files");
        panels.Bind("one");
        Assert.Equal("details", panels.Layout.SelectedPanel);
        Assert.Equal(512, panels.Layout.Width);
        Assert.False(first.IsCurrent);
        Assert.True(panels.BeginOperation("details")!.IsCurrent);
    }

    [Fact]
    public void OldSnapshotCannotReadmitWorkBetweenNavigationIntentAndRootSelection()
    {
        var (client, _) = Gateway();
        using var panels = new ChatConversationPanelState(key =>
            ChatConversationContext.Capture("gateway-one", client, Session(key, key), "profile-a"));
        panels.Bind("one");
        panels.Open("details");
        var operation = panels.BeginOperation("details")!;
        panels.ExpectSelection("two");
        panels.Bind("one");
        Assert.False(operation.IsCurrent);
        Assert.Null(panels.BeginOperation("details"));
        panels.Bind("two");
        Assert.Equal("two", panels.Context!.Identity.SessionKey);
        Assert.Null(panels.Layout.SelectedPanel);
        panels.Suspend();
        panels.Bind("two");
        Assert.False(panels.Open("files"));
        panels.Resume();
        Assert.True(panels.Open("files"));
    }

    [Fact]
    public void RootRejectingUnknownSelectionCanCommitItsActualConversation()
    {
        var (client, _) = Gateway();
        using var panels = new ChatConversationPanelState(key => key == "known"
            ? ChatConversationContext.Capture("gateway-one", client, Session(key, key), "profile-a") : null);
        panels.Bind("known");
        panels.Open("details");
        panels.ExpectSelection("unknown");
        panels.Bind("known");
        Assert.Null(panels.BeginOperation("details"));
        panels.CommitSelection("known");
        Assert.True(panels.BeginOperation("details")!.IsCurrent);
    }

    [Fact]
    public async Task BackgroundRefreshCannotRebindAnOlderSessionAfterCommittedNavigation()
    {
        var (client, _) = Gateway();
        using var resolving = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        using var panels = new ChatConversationPanelState(key =>
        {
            if (block && key == "one")
            {
                resolving.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return ChatConversationContext.Capture("gateway-one", client, Session(key, key), "profile-a");
        });
        panels.Bind("one");
        panels.Open("files");
        block = true;
        var refresh = Task.Run(panels.Refresh);
        Assert.True(resolving.Wait(TimeSpan.FromSeconds(10)));
        panels.ExpectSelection("two");
        panels.Bind("two");
        release.Set();
        await refresh;
        Assert.Equal("two", panels.Context!.Identity.SessionKey);
        Assert.True(panels.Open("files"));
        Assert.Equal("two", panels.BeginOperation("files")!.Context.Identity.SessionKey);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("principal")]
    [InlineData("reset")]
    [InlineData("known-session-id")]
    public void SameKeyCannotRestoreAnotherOwnerOrSessionLayout(string change)
    {
        var (client, proxy) = Gateway();
        var gateway = "gateway-one";
        var session = Session(id: change == "known-session-id" ? null : "session-one");
        using var panels = new ChatConversationPanelState(_ => ChatConversationContext.Capture(gateway, client, session, "profile-a"));
        panels.Bind(session.Key);
        panels.Open("files");
        panels.Resize(600);
        if (change == "gateway") gateway = "gateway-two";
        if (change == "principal") proxy.SigningDeviceId = "operator-b";
        if (change is "reset" or "known-session-id") session.SessionId = "new-session";
        panels.Bind(session.Key);
        Assert.Empty(panels.Layout.OpenPanels);
        Assert.Null(panels.Layout.SelectedPanel);
        Assert.Equal(440, panels.Layout.Width);
    }

    [Fact]
    public void ReconnectRetainsLayoutButNeverReusesOperation()
    {
        var (client, proxy) = Gateway();
        using var panels = new ChatConversationPanelState(_ => ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a"));
        panels.Bind("agent:writing:one");
        panels.Open("files");
        var operation = panels.BeginOperation("files")!;
        proxy.Connected = false;
        panels.Bind("agent:writing:one");
        Assert.Null(panels.Context);
        Assert.Equal("files", panels.Layout.SelectedPanel);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        proxy.Connected = true;
        proxy.Epoch++;
        panels.Bind("agent:writing:one");
        Assert.Equal("files", panels.Layout.SelectedPanel);
        Assert.False(operation.IsCurrent);
        Assert.Equal(13, panels.BeginOperation("files")!.Context.ConnectionEpoch);
    }

    [Fact]
    public void CloseAndDisposeCancelSelectedWorkButResizeDoesNot()
    {
        var (client, _) = Gateway();
        var panels = new ChatConversationPanelState(_ => ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a"));
        panels.Bind("agent:writing:one");
        panels.Open("files");
        var operation = panels.BeginOperation("files")!;
        panels.Resize(1);
        Assert.Equal(280, panels.Layout.Width);
        panels.Resize(5000);
        Assert.Equal(800, panels.Layout.Width);
        Assert.True(operation.IsCurrent);
        panels.Close("files");
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.Null(panels.Layout.SelectedPanel);
        panels.Open("files");
        var next = panels.BeginOperation("files")!;
        panels.Dispose();
        panels.Dispose();
        Assert.True(next.CancellationToken.IsCancellationRequested);
        Assert.False(next.IsCurrent);
        Assert.False(panels.Open("files"));
    }

    [Fact]
    public void SelectingDifferentContentInSamePanelRevokesPreviousRun()
    {
        var (client, _) = Gateway();
        using var panels = new ChatConversationPanelState(_ =>
            ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a"));
        panels.Bind("agent:writing:one");
        panels.Open("details", "tool-one", "run-one");
        var previous = panels.BeginOperation("details", "run-one")!;
        panels.Open("details", "tool-two", "run-two");
        Assert.True(previous.CancellationToken.IsCancellationRequested);
        Assert.False(previous.IsCurrent);
        Assert.Null(panels.BeginOperation("details", "run-one"));
        Assert.Equal("run-two", panels.BeginOperation("details")!.RunId);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void EscapeLeavesComposerAloneWhenNarrowPanelIsHidden(
        bool narrow, bool presented, bool withinPanel, bool expected) =>
        Assert.Equal(expected, ChatConversationPanelNavigation.ShouldCloseOnEscape(narrow, presented, withinPanel));

    [Fact]
    public void NewClientRestoresOnlySameIdentityLayoutWithoutRevivingOperations()
    {
        var layouts = new ChatConversationPanelLayouts();
        var (firstClient, _) = Gateway();
        var first = new ChatConversationPanelState(
            _ => ChatConversationContext.Capture("gateway-one", firstClient, Session(), "profile-a"), layouts);
        first.Bind("agent:writing:one");
        first.Open("files");
        first.Resize(560);
        var oldOperation = first.BeginOperation("files")!;
        first.Dispose();
        var (secondClient, _) = Gateway();
        using var second = new ChatConversationPanelState(
            _ => ChatConversationContext.Capture("gateway-one", secondClient, Session(), "profile-a"), layouts);
        second.Bind("agent:writing:one");
        Assert.Equal("files", second.Layout.SelectedPanel);
        Assert.Equal(560, second.Layout.Width);
        Assert.False(oldOperation.IsCurrent);
        Assert.Same(secondClient, second.BeginOperation("files")!.Context.Client);
        using var otherGateway = new ChatConversationPanelState(
            _ => ChatConversationContext.Capture("gateway-two", secondClient, Session(), "profile-a"), layouts);
        otherGateway.Bind("agent:writing:one");
        Assert.Null(otherGateway.Layout.SelectedPanel);
        layouts.Clear();
        using var reopenedWindow = new ChatConversationPanelState(
            _ => ChatConversationContext.Capture("gateway-one", secondClient, Session(), "profile-a"), layouts);
        reopenedWindow.Bind("agent:writing:one");
        Assert.Null(reopenedWindow.Layout.SelectedPanel);
    }

    [Fact]
    public async Task DelayedRootBindCannotOverwriteANewerAuthenticatedAccountFact()
    {
        var (client, _) = Gateway();
        var context = ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a")!;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockNext = 0;
        using var panels = new ChatConversationPanelState(_ =>
        {
            var captured = context;
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return captured;
        });
        panels.Bind("agent:writing:one");
        panels.Open("files");
        blockNext = 1;
        var delayedBind = Task.Run(() => panels.Bind("agent:writing:one"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        context = context with { Identity = context.Identity with { UserId = "profile-b" }, AccountRevision = 2 };
        panels.Refresh();
        release.Set();
        await delayedBind;
        Assert.Equal("profile-b", panels.Context!.Identity.UserId);
        Assert.Null(panels.Layout.SelectedPanel);
    }

    [Fact]
    public void StaleConsumerCannotReacquireAnotherDocumentsLeaseWithinTheSameRun()
    {
        var (client, _) = Gateway();
        using var panels = new ChatConversationPanelState(_ =>
            ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a"));
        panels.Bind("agent:writing:one");
        panels.Open("canvas", "document-a", "run-one");
        var first = panels.BeginOperation("canvas", "run-one", "document-a")!;
        panels.Open("canvas", "document-b", "run-one");
        Assert.False(first.IsCurrent);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Null(panels.BeginOperation("canvas", "run-one", "document-a"));
        var current = panels.BeginOperation("canvas", "run-one", "document-b")!;
        Assert.Equal("document-b", current.ContentId);
        Assert.True(current.IsCurrent);
    }

    [Theory]
    [InlineData("Loading", "details")]
    [InlineData("Loading", "files")]
    [InlineData("Loading", "canvas")]
    [InlineData("Profileless", "details")]
    [InlineData("Profileless", "files")]
    [InlineData("Profileless", "canvas")]
    [InlineData("Unavailable", "details")]
    [InlineData("Unavailable", "files")]
    [InlineData("Unavailable", "canvas")]
    [InlineData("Failed", "details")]
    [InlineData("Failed", "files")]
    [InlineData("Failed", "canvas")]
    [InlineData("RequiresReconnect", "details")]
    [InlineData("RequiresReconnect", "files")]
    [InlineData("RequiresReconnect", "canvas")]
    public void AllPanelSlotsRejectWorkWhileCanonicalAccountIsUnverified(string status, string panelId)
    {
        var (client, _) = Gateway();
        var context = ChatConversationContext.Capture("gateway-one", client, Session(), "profile-a")!;
        var account = ChatConversationAccountStatus.Known;
        using var panels = new ChatConversationPanelState(
            _ => account == ChatConversationAccountStatus.Known ? context : null,
            accountStatus: () => account);
        panels.Bind("agent:writing:one");
        Assert.True(panels.Open(panelId, "content-one", "run-one"));
        var operation = panels.BeginOperation(panelId, "run-one", "content-one")!;
        account = Enum.Parse<ChatConversationAccountStatus>(status);
        panels.Refresh();
        Assert.Null(panels.Context);
        Assert.Empty(panels.Layout.OpenPanels);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.False(panels.Open(panelId, "content-one", "run-one"));
        Assert.Null(panels.BeginOperation(panelId, "run-one", "content-one"));
    }
}
