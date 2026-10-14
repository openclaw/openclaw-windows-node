using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Actual loader/state proof for the bounded initial page: ONE limit/maxBytes/offset request, the
/// accepted window is state-owned and generation-validated, a bounded incomplete tail never drops
/// previously seen older entries, and a reset during held reconstruction admits no stale window.
/// Native WinUI execution remains unverified.
/// </summary>
public class ChatHistoryWindowCommitTests
{
    // Adopted from Mini-Actual-Off-Lock-Lease-Counterexample (was FAILING at 98ced3f6): a held older
    // NETWORK request must stay bound to its ORIGINAL window lease even after a same-cursor refresh
    // commits a new window revision; the stale older page must be rejected and the cursor preserved.
    [Fact]
    public async Task Mini_OffLockMerge_HeldNetworkPageAfterSameCursorRefreshIsRejected()
    {
        using var rig = new Rig();
        var held = new TaskCompletionSource<GatewayChatHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = false;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg(refreshed ? "new tail" : "old tail", 9)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            entered.TrySetResult(true);
            return held.Task;
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);
        refreshed = true;
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        held.TrySetResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("stale older", 1)], HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        await older.WaitAsync(Bounded);
        Assert.DoesNotContain("stale older", Texts(rig.State, rig.Context));
        Assert.True(rig.State.GetHistoryWindow("main")!.Value.HasMore);
    }

    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    private sealed class Rig : IDisposable
    {
        public Rig(string key = "main")
        {
            Bridge = new ScriptedBridge();
            var dir = Directory.CreateTempSubdirectory("oc-window-rig-").FullName;
            Metadata = new ChatMetadataStore(Path.Combine(dir, "tool-meta.json"));
            Persistence = new ChatStatePersistence(Path.Combine(dir, "last-state.json"));
            State = new ChatConversationState(ConnectionStatus.Connected, lastChatState: null, seedModels: null);
            Loader = new ChatHistoryLoader(
                Bridge, State, Metadata, Persistence, new ChatTelemetryTracker(),
                retryScheduler: (_, _, _) => Task.CompletedTask);
            Context = new ChatProjectionContext(key, HasHandshakeSnapshot: true);
            State.Load([new SessionInfo { Key = key, IsMain = true }], Context);
        }

        public ScriptedBridge Bridge { get; }
        public ChatMetadataStore Metadata { get; }
        public ChatStatePersistence Persistence { get; }
        public ChatConversationState State { get; }
        public ChatHistoryLoader Loader { get; }
        public ChatProjectionContext Context { get; }

        public void Dispose()
        {
            Loader.Dispose();
            Bridge.Dispose();
        }
    }

    private sealed class ScriptedBridge : IChatGatewayBridge
    {
        public List<ChatHistoryPageOptions> PageRequests { get; } = new();
        public Func<ChatHistoryPageOptions, Task<GatewayChatHistoryPage>>? PageBehavior { get; set; }

        public bool IsConnected => true;
        public ConnectionStatus CurrentStatus => ConnectionStatus.Connected;
        public string? MainSessionKey => "main";
        public bool HasHandshakeSnapshot => true;
        public SessionInfo[] GetSessionList() => [new SessionInfo { Key = "main", IsMain = true }];
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }
        public void Dispose() { }
        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) => Task.CompletedTask;
        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null, string? idempotencyKey = null) => Task.FromResult(new ChatSendResult());
        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) => Task.FromResult(new CommandCatalog { IsSupported = false });
        public Task PatchSessionModelAsync(string sessionKey, string model) => Task.CompletedTask;
        public Task ClearSessionModelAsync(string sessionKey) => Task.CompletedTask;
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => Task.CompletedTask;
        public Task ClearSessionThinkingLevelAsync(string sessionKey) => Task.CompletedTask;

        // Legacy unbounded API stays separate; the loader must not use it for the bounded initial page.
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
            Task.FromException<ChatHistoryInfo>(new InvalidOperationException("legacy unbounded history must not be used for the bounded initial page"));

        public async Task<GatewayChatHistoryPage> RequestChatHistoryPageAsync(
            string? sessionKey,
            ChatHistoryPageOptions options,
            CancellationToken cancellationToken = default)
        {
            PageRequests.Add(options);
            if (PageBehavior is not null)
                return await PageBehavior(options).ConfigureAwait(false);
            return new GatewayChatHistoryPage(
                sessionKey ?? "main", "sess-1", Array.Empty<ChatMessageInfo>(),
                HasMore: false, NextOffset: null, ResponseOffset: options.Offset, Total: 0);
        }

        public Task SendChatAbortAsync(string runId, string? sessionKey = null) => Task.CompletedTask;
        public Task ResolveExecApprovalAsync(string approvalId, string decision) => Task.CompletedTask;
#pragma warning disable CS0067
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
#pragma warning restore CS0067
    }

    private static ChatMessageInfo Msg(string text, int seq, string? id = null, long ts = 0) =>
        new() { SessionKey = "main", Role = "user", Text = text, OpenClawSeq = seq, OpenClawId = id, Ts = ts };

    private static ChatMessageInfo AssistantMsg(string text, int seq) =>
        new() { SessionKey = "main", Role = "assistant", Text = text, OpenClawSeq = seq };

    private static string[] Texts(ChatConversationState state, ChatProjectionContext context) =>
        Texts(state, context, "main");

    private static string[] Texts(ChatConversationState state, ChatProjectionContext context, string key) =>
        state.Snapshot(context).Timelines[key].Entries.Select(e => e.Text).ToArray();

    [Fact]
    public async Task InitialLoad_RequestsOneBoundedTailPageAndRetainsAcceptedWindow()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("tail", 5)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        // Exactly ONE page request with the explicit bounded tail bounds (no eager older-page loop).
        var options = Assert.Single(rig.Bridge.PageRequests);
        Assert.Equal(ChatHistoryLoader.InitialHistoryPageLimit, options.Limit);
        Assert.Equal(ChatHistoryLoader.InitialHistoryPageMaxBytes, options.MaxBytes);
        Assert.Equal(0, options.Offset);

        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.True(window!.Value.HasMore);
        Assert.Equal(200, window.Value.NextOffset);
        Assert.Equal("sess-1", window.Value.SessionId);
        Assert.Equal(400, window.Value.Total);
    }

    [Fact]
    public async Task BoundedIncompleteTail_PreservesPreviouslySeenOlderEntries()
    {
        using var rig = new Rig();
        // A complete (exhausted) older page becomes the prior history.
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("older", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Contains("older", Texts(rig.State, rig.Context));

        // A bounded INCOMPLETE tail arrives with authoritative:true; it must NOT drop the older entry.
        // Valid strict-carrier shape: nextOffset (200) must be below totalMessages (400).
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("tail", 9)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var texts = Texts(rig.State, rig.Context);
        Assert.Contains("tail", texts);
        Assert.Contains("older", texts);
        Assert.True(rig.State.GetHistoryWindow("main")!.Value.HasMore);
    }

    [Fact]
    public async Task ResetDuringHeldReconstruction_AdmitsNoStaleWindow()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("tail", 5)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };

        var load = rig.Loader.LoadAsync("main", force: true, authoritative: true);
        await entered.Task.WaitAsync(Bounded);
        rig.State.ResetThread("main", rig.Context);   // supersede the in-flight generation
        release.TrySetResult(true);
        await load.WaitAsync(Bounded);

        // The stale page's window must never be admitted; it is stored only with an accepted commit.
        Assert.Null(rig.State.GetHistoryWindow("main"));
    }

    [Fact]
    public async Task CancellationDuringHeldReconstruction_LeavesHeldTimelineAndWindowUnchanged()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("first", 1)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var entriesBefore = Texts(rig.State, rig.Context);
        var windowBefore = rig.State.GetHistoryWindow("main");
        Assert.NotNull(windowBefore);

        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("second", 2)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };

        using var cts = new CancellationTokenSource();
        var load = rig.Loader.LoadAsync(
            "main", force: true, cancellationToken: cts.Token, authoritative: true);
        await entered.Task.WaitAsync(Bounded);
        cts.Cancel();
        release.TrySetResult(true);
        await load.WaitAsync(Bounded);

        // Cancellation leaves the held timeline and the accepted window unchanged.
        Assert.Equal(entriesBefore, Texts(rig.State, rig.Context));
        Assert.Equal(windowBefore, rig.State.GetHistoryWindow("main"));
    }

    [Fact]
    public async Task OlderPage_PrependsDedupsPreservesNewerEntriesAndExhausts()
    {
        using var rig = new Rig();
        // Server tail offsets: offset 0 is the newest page; 200 is the older page (chronological rows).
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400)
            : new GatewayChatHistoryPage("main", "sess-1",
                [Msg("older", 1), Msg("tail", 9)],   // "tail" replays the boundary row
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 400));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["tail"], Texts(rig.State, rig.Context));

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        var texts = Texts(rig.State, rig.Context);
        Assert.Contains("older", texts);
        Assert.Contains("tail", texts);
        Assert.Equal(1, texts.Count(t => t == "tail"));            // boundary replay deduplicated
        Assert.True(Array.IndexOf(texts, "older") < Array.IndexOf(texts, "tail")); // older prepended

        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.False(window!.Value.HasMore);
        Assert.Null(window.Value.NextOffset);
        Assert.Equal(200, window.Value.ResponseOffset);

        // Exhausted window: a further LoadOlder is a no-op (still exactly two page requests).
        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);
        Assert.Equal(2, rig.Bridge.PageRequests.Count);
        Assert.Contains(200, rig.Bridge.PageRequests.Select(o => o.Offset));
    }

    [Fact]
    public async Task OlderPage_SessionMismatch_RejectsWithoutClearingHeldState()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400)
            : new GatewayChatHistoryPage("main", "sess-2", [Msg("older", 1)],   // wrong identity
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 400));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        var before = Texts(rig.State, rig.Context);
        var windowBefore = rig.State.GetHistoryWindow("main");

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        // Rejected: held timeline and accepted window are untouched (no mixed identities).
        Assert.Equal(before, Texts(rig.State, rig.Context));
        Assert.Equal(windowBefore, rig.State.GetHistoryWindow("main"));
        Assert.True(rig.State.GetHistoryWindow("main")!.Value.HasMore);
    }

    [Fact]
    public async Task InitialPage_IdentityChange_ExplicitlyReplacesPriorIdentity()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("a", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["a"], Texts(rig.State, rig.Context));

        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-2", [Msg("b", 2)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        // New identity: the previous identity's entries are dropped, not silently mixed.
        Assert.Equal(["b"], Texts(rig.State, rig.Context));
        Assert.Equal("sess-2", rig.State.GetHistoryWindow("main")!.Value.SessionId);
    }

    [Fact]
    public async Task InitialPage_CatalogSeedThenIncompleteNewUuid_DoesNotMixPriorIdentity()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-A", [Msg("held-A", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["held-A"], Texts(rig.State, rig.Context));
        rig.State.ApplySessions([new SessionInfo { Key = "main", IsMain = true, SessionId = "sess-B" }], rig.Context);
        Assert.Equal(["held-A"], Texts(rig.State, rig.Context));
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-B", [Msg("tail-B", 9)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["tail-B"], Texts(rig.State, rig.Context));
        Assert.Equal("sess-B", rig.State.GetHistoryWindow("main")!.Value.SessionId);
        Assert.True(rig.State.GetHistoryWindow("main")!.Value.HasMore);
    }

    // Adopted from Mini-Reconnect-UUID-Counterexample (was FAILING on base 6f).
    [Fact]
    public async Task InitialPage_ReconnectThenCatalogNewUuid_DoesNotMixRetainedIdentity()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-A", [Msg("held-A", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["held-A"], Texts(rig.State, rig.Context));
        rig.Loader.ApplyStatusAndAdvanceGeneration(ConnectionStatus.Disconnected, rig.Context);
        rig.Loader.ApplyStatusAndAdvanceGeneration(ConnectionStatus.Connected, rig.Context);
        Assert.Null(rig.State.GetHistoryWindow("main"));
        Assert.Equal(["held-A"], Texts(rig.State, rig.Context));
        rig.State.ApplySessions([new SessionInfo { Key = "main", IsMain = true, SessionId = "sess-B" }], rig.Context);
        Assert.Equal(["held-A"], Texts(rig.State, rig.Context));
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-B", [Msg("tail-B", 9)], HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Equal(["tail-B"], Texts(rig.State, rig.Context));
        Assert.Equal("sess-B", rig.State.GetHistoryWindow("main")!.Value.SessionId);
    }

    // Adopted semantics change with the bounded content-root REBASE: a single concurrent live change no
    // longer aborts the older page; the retry re-captures the newest immutable roots and merges the older
    // page ON TOP of the new live state, advancing the cursor exactly once.
    [Fact]
    public async Task OlderPage_ConcurrentLiveContentChange_RebasesAndPreservesBoth()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800)
            : new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };

        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);

        // Concurrent live content change while the older page is held: the exact retained-content version
        // must reject the stale merge (a history lease alone would have accepted it).
        rig.State.ApplyEvent("main", new ChatMessageEvent("live text"), null, rig.Context);

        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        var texts = Texts(rig.State, rig.Context);
        Assert.Contains("live text", texts);   // the concurrent live change is preserved
        Assert.Contains("older", texts);       // and the older page is admitted via the bounded rebase
        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.Equal(200, window!.Value.ResponseOffset);   // cursor advanced exactly once, to the older page
        Assert.False(window.Value.HasMore);
    }

    // Deterministic attempt/fetch counting via the production pre-commit seam: a live edit injected AFTER
    // the tentative reconstruction forces exactly ONE rebase. The seam is asserted to be called exactly
    // TWICE (attempt 1 rejected + attempt 2 committed), the older page is fetched exactly once, both the
    // live change and the older page survive, and the cursor advances exactly once.
    [Fact]
    public async Task OlderPage_LiveEditAtCommitSeam_RebasesOncePreservingBothAndFetchesOnce()
    {
        using var rig = new Rig();
        var olderFetches = 0;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            olderFetches++;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var seamCalls = 0;
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
        {
            seamCalls++;
            if (seamCalls == 1)
                rig.State.ApplyEvent("main", new ChatMessageEvent("live text"), null, rig.Context);
        };

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.Equal(2, seamCalls);      // EXACT attempt count: 1 rejected + 1 committed
        Assert.Equal(1, olderFetches);   // ONE bridge fetch for the older page across both attempts
        var texts = Texts(rig.State, rig.Context);
        Assert.Contains("live text", texts);   // live change preserved through the rebase
        Assert.Contains("older", texts);       // older page admitted on the second attempt
        Assert.Equal(200, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);   // cursor advanced once
    }

    // METADATA-ONLY proof: the mutation changes the immutable metadata map while the retained Entries
    // reference is IDENTICAL. If only the timeline reference were compared, the first commit would succeed
    // (seam call count would be 1); asserting 2 proves the metadata-reference compare is actually load-bearing.
    [Fact]
    public async Task OlderPage_MetadataOnlyConflictAtSeam_RebasesOnceWithIdenticalEntries()
    {
        using var rig = new Rig();
        var olderFetches = 0;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1",
                    [AssistantMsg("assistant", 1), Msg("tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            olderFetches++;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var metadataWritten = false;
        var entriesUnchangedByMetadataWrite = false;
        var seamCalls = 0;
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
        {
            seamCalls++;
            if (seamCalls == 1)
            {
                // Metadata-ONLY mutation: usage contribution. Prove it writes metadata WITHOUT replacing
                // the retained Entries reference (measured across the mutation itself, not across the
                // whole load, which legitimately merges the older page afterwards).
                var entriesBeforeWrite = rig.State.Snapshot(rig.Context).Timelines["main"].Entries;
                metadataWritten = rig.State.SnapshotAssistantUsageContribution(
                    "main",
                    new ChatEntryMetadata(null, null, InputTokens: 5, OutputTokens: 5, ContextTokens: 1000),
                    rig.Context) is not null;
                var entriesAfterWrite = rig.State.Snapshot(rig.Context).Timelines["main"].Entries;
                entriesUnchangedByMetadataWrite = ReferenceEquals(entriesBeforeWrite, entriesAfterWrite);
            }
        };

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.True(metadataWritten, "the metadata-only mutation must have been applied");
        Assert.True(entriesUnchangedByMetadataWrite,
            "the metadata-only mutation must not change the retained Entries reference");
        Assert.Equal(2, seamCalls);   // only the METADATA reference compare can force this rebase
        Assert.Equal(1, olderFetches);
        Assert.Contains("older", Texts(rig.State, rig.Context));
        Assert.Equal(200, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);
    }

    [Fact]
    public async Task OlderPage_SustainedChurnForThreeAttempts_SurfacesVisibleRetryWithoutAdvancing()
    {
        using var rig = new Rig();
        var olderFetches = 0;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            olderFetches++;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        ChatHistoryLoadResult? last = null;
        rig.Loader.Completed += (_, result) => last = result;

        var seamCalls = 0;
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
        {
            seamCalls++;
            rig.State.ApplyEvent("main", new ChatMessageEvent($"churn {seamCalls}"), null, rig.Context);
        };

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.Equal(3, seamCalls);      // bounded: exactly three attempts
        Assert.Equal(1, olderFetches);   // but only ONE bridge fetch (no refetch per attempt)
        Assert.DoesNotContain("older", Texts(rig.State, rig.Context));   // no cursor advance
        Assert.Equal(0, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);
        Assert.NotNull(last);
        Assert.False(last!.PublishSnapshot);       // visible retry state, not a silent drop
        Assert.NotNull(last.Notification);
        // Legitimate content-only churn does NOT change the window revision, so the full-lease delivery
        // fence must STILL accept and deliver the visible retry (the fix must not suppress it).
        Assert.NotNull(rig.State.SnapshotIfHistoryResultCurrent(last!, rig.Context));
    }

    // Adopted from Mini-Actual-Final-Rebase-Notification-Counterexample (1 executed / 1 FAILED at 51a49c27).
    // After two live content conflicts, attempt 3 performs a successful force/authoritative refresh on the
    // SAME session UUID and SAME cursor. No stale page commits, but the exhausted-rebase ERROR was still
    // deliverable through the token-only guard: the generation token was unchanged while the window REVISION
    // was replaced. The delivery fence must carry the FULL ORIGINAL window lease.
    [Fact]
    public async Task OlderPage_ThirdSeamSameCursorRefresh_CannotPublishStaleExhaustionError()
    {
        using var rig = new Rig();
        var refreshed = false;
        var olderFetches = 0;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1",
                    [Msg(refreshed ? "fresh tail" : "old tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            olderFetches++;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("stale older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var errors = new List<ChatHistoryLoadResult>();
        rig.Loader.Completed += (_, result) => { if (result.Notification is not null) errors.Add(result); };

        var attempts = 0;
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
        {
            attempts++;
            if (attempts < 3)
                rig.State.ApplyEvent("main", new ChatMessageEvent($"live {attempts}"), null, rig.Context);
            else
            {
                refreshed = true;
                rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded).GetAwaiter().GetResult();
            }
        };

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.Equal(3, attempts);
        Assert.Equal(1, olderFetches);
        Assert.DoesNotContain("stale older", Texts(rig.State, rig.Context));
        Assert.Contains("fresh tail", Texts(rig.State, rig.Context));
        Assert.Equal(0, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);
        Assert.NotEmpty(errors);   // the exhausted rebase still surfaces a candidate retry notification
        // The ACTUAL source-owned delivery fence (the one the WinUI provider consumer uses) rejects the
        // obsolete error: token unchanged, window revision replaced. A token-only guard would deliver it.
        var deliverable = errors
            .Where(r => rig.State.SnapshotIfHistoryResultCurrent(r, rig.Context) is not null)
            .ToList();
        Assert.Empty(deliverable);
    }

    [Fact]
    public async Task OlderPage_ExhaustionErrorFence_IsEvaluatedAtDeliveryTime()
    {
        // Emit the exhausted-rebase error, capture it, THEN perform a same-cursor refresh. The source-owned
        // fence must reject it only AFTER the refresh, proving it is a live DELIVERY-time check (a queued
        // delivery re-runs it), not a one-shot emit-time boolean.
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800)
            : new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        ChatHistoryLoadResult? error = null;
        rig.Loader.Completed += (_, result) => { if (result.Notification is not null) error = result; };
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
            rig.State.ApplyEvent("main", new ChatMessageEvent("churn"), null, rig.Context);

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);
        Assert.NotNull(error);
        Assert.NotNull(rig.State.SnapshotIfHistoryResultCurrent(error!, rig.Context));   // deliverable now

        // Same session UUID, SAME cursor, newer revision.
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        Assert.Null(rig.State.SnapshotIfHistoryResultCurrent(error!, rig.Context));      // fenced later
    }

    [Fact]
    public async Task CapturedOlderMergeRoots_RealMetadataRootUnchangedAfterUsageWrite()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1",
                [AssistantMsg("assistant", 1), Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800)
            : new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        // Capture the REAL immutable metadata root the off-lock merge would run against.
        Assert.True(rig.State.TryCaptureOlderMergeLease("main", out var lease, out _));
        var window = rig.State.GetHistoryWindow("main")!.Value;
        var offset = window.NextOffset!.Value;
        Assert.True(rig.State.TryPrepareOlderMergeRoots("main", offset, window, lease, out var roots));
        var capturedRoot = roots.Metadata;

        // REAL metadata-only write through the production path (usage contribution).
        Assert.NotNull(rig.State.SnapshotAssistantUsageContribution(
            "main",
            new ChatEntryMetadata(null, null, InputTokens: 5, OutputTokens: 5, ContextTokens: 1000),
            rig.Context));

        Assert.True(rig.State.TryPrepareOlderMergeRoots("main", offset, window, lease, out var laterRoots));
        // The CURRENT root is a NEW immutable snapshot; the CAPTURED root still never observes the write.
        Assert.False(ReferenceEquals(capturedRoot, laterRoots.Metadata));
        var assistantId = rig.State.Snapshot(rig.Context).Timelines["main"].Entries
            .Single(entry => entry.Kind == ChatTimelineItemKind.Assistant).Id;
        Assert.True(laterRoots.Metadata[assistantId].UsageContributionTokens is > 0);
        Assert.True(
            !capturedRoot.TryGetValue(assistantId, out var before) || before.UsageContributionTokens is null,
            "the captured metadata root must not observe the subsequent write");
    }

    [Fact]
    public async Task OlderPage_PreCancelledRequest_FetchesNothingAndCommitsNothing()
    {
        using var rig = new Rig();
        var olderFetches = 0;
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            olderFetches++;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        await rig.Loader.LoadOlderAsync("main", cancelled.Token).WaitAsync(Bounded);

        Assert.Equal(0, olderFetches);   // pre-cancelled: no reconstruction stage runs at all
        Assert.DoesNotContain("older", Texts(rig.State, rig.Context));
        Assert.Equal(0, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);
    }

    [Fact]
    public async Task OlderPage_ResetDuringHeldMerge_CommitsNothing()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options =>
        {
            if (options.Offset == 0)
            {
                return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                    HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 800));
            }
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var published = false;
        rig.Loader.Completed += (_, result) => { if (result.PublishSnapshot) published = true; };

        var seamCalls = 0;
        rig.Loader.BeforeOlderMergeCommitForTests = () =>
        {
            seamCalls++;
            if (seamCalls == 1)
                rig.State.ResetThread("main", rig.Context);
        };

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.DoesNotContain("older", Texts(rig.State, rig.Context));   // reset: nothing committed
        Assert.False(published);                                        // ... and nothing stale published
    }

    [Fact]
    public async Task OlderPage_IdentityFirstDedup_KeepsDistinctRowsAndDropsSameIdReplay()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("same", 1, "m1", 1000)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400)
            : new GatewayChatHistoryPage("main", "sess-1",
                [Msg("same", 2, "m2", 1000), Msg("same", 1, "m1", 1000)],   // m2 distinct; m1 replay
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 400));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        // Distinct source identity (m2/seq2) survives despite identical text+timestamp; only the
        // exact same-id/sequence replay (m1/seq1) is dropped.
        Assert.Equal(2, Texts(rig.State, rig.Context).Count(t => t == "same"));
    }

    [Fact]
    public async Task OlderPage_NullSessionId_RejectedWithoutClearingHeldState()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 0
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: 0, Total: 400)
            : new GatewayChatHistoryPage("main", null, [Msg("older", 1)],   // unknown identity
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 400));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        var before = Texts(rig.State, rig.Context);
        var windowBefore = rig.State.GetHistoryWindow("main");

        await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);

        Assert.Equal(before, Texts(rig.State, rig.Context));
        Assert.Equal(windowBefore, rig.State.GetHistoryWindow("main"));
    }

    [Fact]
    public async Task LateOlderResponse_CannotRegressNewerAcceptedCursor()
    {
        using var rig = new Rig();
        var tailNextOffset = 200;
        // Coherent strict shape: nextOffset (200/400) must be below totalMessages for HasMore pages.
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 200
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800)
            : new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: tailNextOffset, ResponseOffset: options.Offset, Total: 800));

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };

        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);

        // A competing refresh advances the accepted cursor while the older response is still held.
        rig.Loader.TestReconstructionBarrier = null;
        tailNextOffset = 400;
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.Equal(400, window!.Value.NextOffset);                 // not regressed to 200
        Assert.DoesNotContain("older", Texts(rig.State, rig.Context));
    }

    private static (TaskCompletionSource<bool> Entered, TaskCompletionSource<bool> Release) HoldOlder(
        Rig rig)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };
        return (entered, release);
    }

    private static void UseTwoPageBehavior(Rig rig)
    {
        rig.Bridge.PageBehavior = options => Task.FromResult(options.Offset == 200
            ? new GatewayChatHistoryPage("main", "sess-1", [Msg("older", 1)],
                HasMore: false, NextOffset: null, ResponseOffset: 200, Total: 800)
            : new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 9)],
                HasMore: true, NextOffset: 200, ResponseOffset: options.Offset, Total: 800));
    }

    [Fact]
    public async Task OlderCallerCancellation_DuringHeldReconstruction_PreservesHeldStateAndWindow()
    {
        using var rig = new Rig();
        UseTwoPageBehavior(rig);
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        var windowBefore = rig.State.GetHistoryWindow("main");

        var published = false;
        ChatHistoryLoadResult? emitted = null;
        rig.Loader.Completed += (_, result) => { emitted = result; if (result.PublishSnapshot) published = true; };

        var (entered, release) = HoldOlder(rig);
        using var cts = new CancellationTokenSource();
        var older = rig.Loader.LoadOlderAsync("main", cts.Token);
        await entered.Task.WaitAsync(Bounded);
        cts.Cancel();
        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        Assert.Equal(["tail"], Texts(rig.State, rig.Context));
        Assert.Equal(windowBefore, rig.State.GetHistoryWindow("main"));
        // Cancellation neither publishes a stale page nor emits an error, and never advances the cursor.
        Assert.False(published);
        Assert.Null(emitted);
        Assert.Equal(0, rig.State.GetHistoryWindow("main")!.Value.ResponseOffset);
    }

    [Fact]
    public async Task OlderReset_DuringHeldReconstruction_AdmitsNoStalePage()
    {
        using var rig = new Rig();
        UseTwoPageBehavior(rig);
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var (entered, release) = HoldOlder(rig);
        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);
        rig.State.ResetThread("main", rig.Context);   // reset invalidates the held history + window
        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        // Reset semantics: the held timeline is cleared and the window invalidated; the stale older
        // page is never admitted on top of the reset state.
        Assert.Empty(Texts(rig.State, rig.Context));
        Assert.Null(rig.State.GetHistoryWindow("main"));
    }

    [Fact]
    public async Task OlderReconnect_DuringHeldReconstruction_AdmitsNoStalePage()
    {
        using var rig = new Rig();
        UseTwoPageBehavior(rig);
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var (entered, release) = HoldOlder(rig);
        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);
        rig.Loader.ApplyStatusAndAdvanceGeneration(ConnectionStatus.Disconnected, rig.Context);
        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        Assert.Equal(["tail"], Texts(rig.State, rig.Context));
        Assert.Null(rig.State.GetHistoryWindow("main"));
    }

    [Fact]
    public async Task OlderDisposal_DuringHeldReconstruction_AdmitsNothing()
    {
        using var rig = new Rig();
        UseTwoPageBehavior(rig);
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var (entered, release) = HoldOlder(rig);
        var older = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);
        rig.Loader.Dispose();
        release.TrySetResult(true);
        await older.WaitAsync(Bounded);

        Assert.Equal(["tail"], Texts(rig.State, rig.Context));
    }

    [Fact]
    public async Task OlderSingleFlight_IssuesExactlyOneRequestPerThread()
    {
        using var rig = new Rig();
        UseTwoPageBehavior(rig);
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var (entered, release) = HoldOlder(rig);
        var first = rig.Loader.LoadOlderAsync("main");
        await entered.Task.WaitAsync(Bounded);
        var second = rig.Loader.LoadOlderAsync("main");   // single-flight: returns without requesting
        release.TrySetResult(true);
        await first.WaitAsync(Bounded);
        await second.WaitAsync(Bounded);

        Assert.Equal(1, rig.Bridge.PageRequests.Count(o => o.Offset == 200));
    }

    // REAL COMPOSITION: exact materialized wire fixture served by the real loopback gateway server; the REAL
    // OpenClawGatewayClient.RequestChatHistoryPageAsync parses it; Rig.Bridge.PageBehavior DELEGATES to that real
    // client result; the REAL ChatHistoryLoader walks offset 0 then older until the real window HasMore is false.
    [Fact]
    public async Task Mini_RealSocketClientToLoader_All4200ExactContentNoLoss()
    {
        var key = GatewayScenario.LongSessionKey;
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, (OpenClaw.Shared.IOpenClawLogger?)null,
            identityPath: Path.Combine(Directory.CreateTempSubdirectory("oc-comp-").FullName, "device.json"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            var byOffset = LoadWireFixtureByOffset();
            var requested = new List<int>();
            server.HistoryResponseOverride = p =>
            {
                var off = p.TryGetProperty("offset", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt32() : 0;
                requested.Add(off);
                var obj = (System.Text.Json.Nodes.JsonObject)byOffset[off].DeepClone();
                obj["sessionKey"] = key;   // identity only: canonical key the scenario registers and the client requests
                return obj;
            };
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            await client.ConnectAsync();
            await handshake.Task.WaitAsync(Bounded);

            // Preflight (separate accounting): must SUCCEED, then discard its request accounting.
            var preflight = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(200, 512 * 1024, 0));
            Assert.Equal(200, preflight.Messages.Count);
            Assert.True(preflight.HasMore);
            requested.Clear();

            using var rig = new Rig(key);
            var notes = new List<string>();
            rig.Loader.Completed += (_, r) => { if (!r.PublishSnapshot && r.Notification is not null) notes.Add("note=" + r.Notification.Kind); };
            rig.Bridge.PageBehavior = options => client.RequestChatHistoryPageAsync(key, options);
            await rig.Loader.LoadAsync(key, force: true, authoritative: true).WaitAsync(Bounded);
            for (var i = 0; i < 64; i++)
            {
                var w = rig.State.GetHistoryWindow(key);
                if (w is null || !w.Value.HasMore) break;
                await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded);
            }
            Assert.Empty(notes);                                  // no error notifications surfaced
            var window = rig.State.GetHistoryWindow(key);
            Assert.NotNull(window);
            Assert.False(window!.Value.HasMore);                  // terminal window
            var entries = rig.State.Snapshot(rig.Context).Timelines[key].Entries;
            var texts = entries.Select(e => e.Text).ToArray();
            var expected = Enumerable.Range(0, 4200).Select(i => "row" + i + "-" + new string('y', 2000)).ToArray();
            Assert.Equal(4200, texts.Length);
            Assert.Equal(expected.OrderBy(x => x), texts.OrderBy(x => x));   // full expected set, exactly one per ordinal
            // Per-row ORDINAL check (not a global multiset): the merged timeline must be ordinal-ordered with the
            // fixture's alternating roles (even -> User, odd -> Assistant) and exact per-row text.
            Assert.Equal(ChatTimelineItemKind.User, entries[0].Kind);
            Assert.Equal(ChatTimelineItemKind.Assistant, entries[1].Kind);
            for (var i = 0; i < entries.Count; i++)
            {
                Assert.Equal("row" + i + "-" + new string('y', 2000), entries[i].Text);
                Assert.Equal(i % 2 == 0 ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant, entries[i].Kind);
            }
            // TERMINAL NO-OP (qualified): after exhaustion a re-request issues no further page. This proves the
            // terminal no-op only - NOT duplicate/replayed-page admission or cursor immutability in general.
            var covered = requested.Count;
            var windowBefore = rig.State.GetHistoryWindow(key);
            var snapshotBefore = rig.State.Snapshot(rig.Context).Timelines[key].Entries.Count;
            await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded);
            Assert.Equal(covered, requested.Count);
            Assert.Equal(windowBefore, rig.State.GetHistoryWindow(key));
            Assert.Equal(snapshotBefore, rig.State.Snapshot(rig.Context).Timelines[key].Entries.Count);
            var offs = requested.OrderBy(x => x).ToArray();
            Assert.Equal(21, offs.Length);
            Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 200).ToArray(), offs);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    private static Dictionary<int, System.Text.Json.Nodes.JsonObject> LoadWireFixtureByOffset() =>
        LoadWireFixtureByOffset("wire-pages-4200.jsonl.gz");

    private static Dictionary<int, System.Text.Json.Nodes.JsonObject> LoadWireFixtureByOffset(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var c = Path.Combine(dir, "fixtures", fileName);
            if (File.Exists(c))
            {
                using var file = File.OpenRead(c);
                using var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gz);
                var map = new Dictionary<int, System.Text.Json.Nodes.JsonObject>();
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0) continue;
                    var payload = System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject()["payload"]!.AsObject();
                    map[payload["offset"]!.GetValue<int>()] = payload;
                }
                return map;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("wire fixture");
    }

    // EXPANDED PROJECTED SIBLINGS: actual source-produced bounded wire (3 raw seq records -> 5 projected IDs a..e)
    // through the real client parser -> real ChatHistoryLoader/Merge. Every returned ID once; offsets 0,2,4; terminal.
    [Fact]
    public async Task Mini_RealClientToLoader_ExpandedProjectedSiblingsAllFiveOnce()
    {
        var key = GatewayScenario.LongSessionKey;
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, (OpenClaw.Shared.IOpenClawLogger?)null,
            identityPath: Path.Combine(Directory.CreateTempSubdirectory("oc-sib-").FullName, "device.json"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            var byOffset = LoadWireFixtureByOffset("projected-siblings-3raw-5.jsonl.gz");
            var requested = new List<int>();
            server.HistoryResponseOverride = p =>
            {
                var off = p.TryGetProperty("offset", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt32() : 0;
                requested.Add(off);
                var obj = (System.Text.Json.Nodes.JsonObject)byOffset[off].DeepClone();
                obj["sessionKey"] = key;
                return obj;
            };
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            await client.ConnectAsync();
            await handshake.Task.WaitAsync(Bounded);
            using var rig = new Rig(key);
            var notes = new List<string>();
            rig.Loader.Completed += (_, r) => { if (!r.PublishSnapshot && r.Notification is not null) notes.Add("note=" + r.Notification.Kind); };
            rig.Bridge.PageBehavior = options => client.RequestChatHistoryPageAsync(key, options);
            await rig.Loader.LoadAsync(key, force: true, authoritative: true).WaitAsync(Bounded);
            for (var i = 0; i < 8; i++)
            {
                var w = rig.State.GetHistoryWindow(key);
                if (w is null || !w.Value.HasMore) break;
                await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded);
            }
            var entries = rig.State.Snapshot(rig.Context).Timelines[key].Entries;
            Assert.Empty(notes);
            Assert.Equal(5, entries.Count);
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, entries.Select(e => e.Text).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { 0, 2, 4 }, requested.OrderBy(x => x).ToArray());
            Assert.False(rig.State.GetHistoryWindow(key)!.Value.HasMore);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // PAIRED-FIXTURE ERROR REGRESSION: real client -> actual ChatHistoryLoader. Initial load succeeds (200 tail rows);
    // a LoadOlder at offset 200 forwards the ACTUAL source error object via HistoryErrorOverride. Loader.LoadOlderAsync
    // catches ONLY OperationCanceledException, so the real gateway error PROPAGATES. State must be held unchanged; a
    // retry re-requests offset 200 and throws again.
    [Fact]
    public async Task Mini_RealClientToLoader_PairedOversizeOlderErrorHoldsState()
    {
        var key = GatewayScenario.LongSessionKey;
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, (OpenClaw.Shared.IOpenClawLogger?)null,
            identityPath: Path.Combine(Directory.CreateTempSubdirectory("oc-pair-").FullName, "device.json"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            var successFrame = LoadJsonFixture("success-tail-frame.json");
            var errorFrame = LoadJsonFixture("oversize-older-error-frame.json");
            var requested = new List<int>();
            var wire = new List<(int Offset, int? Limit, int? MaxBytes)>();
            server.HistoryResponseOverride = p =>
            {
                // NOTE: both overrides run per chat.history request; count requests ONLY in HistoryErrorOverride.
                var payload = (System.Text.Json.Nodes.JsonObject)successFrame["payload"]!.DeepClone();
                payload["sessionKey"] = key;   // canonical fixture key adapter (identity only)
                return payload;
            };
            server.HistoryErrorOverride = p =>
            {
                var off = p.TryGetProperty("offset", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt32() : 0;
                var lim = p.TryGetProperty("limit", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number ? l.GetInt32() : (int?)null;
                var mb = p.TryGetProperty("maxBytes", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Number ? m.GetInt32() : (int?)null;
                requested.Add(off); wire.Add((off, lim, mb));
                return off == 200 ? errorFrame["error"]!.DeepClone() : null;
            };
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            await client.ConnectAsync();
            await handshake.Task.WaitAsync(Bounded);
            using var rig = new Rig(key);
            var publishes = 0;
            rig.Loader.Completed += (_, r) => { if (r.PublishSnapshot) publishes++; };
            rig.Bridge.PageBehavior = options => client.RequestChatHistoryPageAsync(key, options);
            await rig.Loader.LoadAsync(key, force: true, authoritative: true).WaitAsync(Bounded);
            var windowBefore = rig.State.GetHistoryWindow(key);
            var beforeEntries = rig.State.Snapshot(rig.Context).Timelines[key].Entries.ToArray();
            var expectedTexts = Enumerable.Range(0, 200).Select(i => "tail" + i).ToArray();
            Assert.Equal(expectedTexts, beforeEntries.Select(e => e.Text).ToArray());
            for (var i = 0; i < beforeEntries.Length; i++)
                Assert.Equal(i % 2 == 0 ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant, beforeEntries[i].Kind);
            Assert.True(windowBefore!.Value.HasMore);
            Assert.Equal(200, windowBefore.Value.NextOffset);
            var identityBefore = windowBefore.Value.SessionId;
            var publishesBefore = publishes;
            Exception? first = null;
            try { await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded); } catch (Exception ex) { first = ex; }
            var firstError = Assert.IsType<InvalidOperationException>(first);   // typed: a TimeoutException cannot pass
            Assert.Contains("oversized item cannot fit", firstError.Message);
            Assert.Equal(beforeEntries, rig.State.Snapshot(rig.Context).Timelines[key].Entries.ToArray());
            Assert.Equal(windowBefore, rig.State.GetHistoryWindow(key));
            Assert.Equal(identityBefore, rig.State.GetHistoryWindow(key)!.Value.SessionId);
            Assert.Equal(publishesBefore, publishes);
            Exception? second = null;
            try { await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded); } catch (Exception ex) { second = ex; }
            var secondError = Assert.IsType<InvalidOperationException>(second);
            Assert.Contains("oversized item cannot fit", secondError.Message);
            Assert.Equal(beforeEntries, rig.State.Snapshot(rig.Context).Timelines[key].Entries.ToArray());
            Assert.Equal(windowBefore, rig.State.GetHistoryWindow(key));
            Assert.Equal(publishesBefore, publishes);
            Assert.Equal(new[] { 0, 200, 200 }, requested.ToArray());
            Assert.Contains(wire, w => w.Offset == 200 && w.Limit == 200 && w.MaxBytes == 512 * 1024);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // COMMENTARY PROJECTOR FALLBACK WIRE -> REAL CLIENT -> ACTUAL ChatHistoryLoader/Merge. Input is ONLY the actual
    // projector fallbacks (alpha/itemA, beta/itemB; shared raw1/seq1). Qualified fixture page-policy adapter (client
    // limit200 vs generator max1; returned count1 within client budget) + canonical-key serve adapter.
    [Fact]
    public async Task Mini_RealClientToLoader_CommentaryFallbackWireAlphaBetaOnce()
    {
        var key = GatewayScenario.LongSessionKey;
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, (OpenClaw.Shared.IOpenClawLogger?)null,
            identityPath: Path.Combine(Directory.CreateTempSubdirectory("oc-cw-").FullName, "device.json"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            var byOffset = LoadCommentaryWireByOffset();
            var requested = new List<int>();
            server.HistoryResponseOverride = p =>
            {
                var off = p.TryGetProperty("offset", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt32() : 0;
                requested.Add(off);
                var payload = (System.Text.Json.Nodes.JsonObject)byOffset[off].DeepClone();
                payload["sessionKey"] = key;
                return payload;
            };
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            await client.ConnectAsync();
            await handshake.Task.WaitAsync(Bounded);
            using var rig = new Rig(key);
            rig.Bridge.PageBehavior = options => client.RequestChatHistoryPageAsync(key, options);
            await rig.Loader.LoadAsync(key, force: true, authoritative: true).WaitAsync(Bounded);
            var afterInitial = rig.State.Snapshot(rig.Context).Timelines[key].Entries.ToArray();
            Assert.Equal(new[] { "beta" }, afterInitial.Select(e => e.Text).ToArray());
            Assert.Equal(ChatTimelineItemKind.Assistant, afterInitial[0].Kind);
            for (var i = 0; i < 8; i++)
            {
                var w = rig.State.GetHistoryWindow(key);
                if (w is null || !w.Value.HasMore) break;
                await rig.Loader.LoadOlderAsync(key).WaitAsync(Bounded);
            }
            var entries = rig.State.Snapshot(rig.Context).Timelines[key].Entries.ToArray();
            Assert.Equal(new[] { "alpha", "beta" }, entries.Select(e => e.Text).ToArray());
            Assert.All(entries, e => Assert.Equal(ChatTimelineItemKind.Assistant, e.Kind));
            Assert.False(rig.State.GetHistoryWindow(key)!.Value.HasMore);
            Assert.Equal(new[] { 0, 1 }, requested.OrderBy(x => x).ToArray());
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    private static Dictionary<int, System.Text.Json.Nodes.JsonObject> LoadCommentaryWireByOffset()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var c = Path.Combine(dir, "fixtures", "commentary-wire.jsonl");
            if (File.Exists(c))
            {
                var map = new Dictionary<int, System.Text.Json.Nodes.JsonObject>();
                foreach (var line in File.ReadAllLines(c))
                {
                    if (line.Length == 0) continue;
                    var payload = System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject()["payload"]!.AsObject();
                    map[payload["offset"]!.GetValue<int>()] = payload;
                }
                return map;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("commentary-wire.jsonl");
    }

    private static System.Text.Json.Nodes.JsonObject LoadJsonFixture(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var c = Path.Combine(dir, "fixtures", fileName);
            if (File.Exists(c)) return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(c))!.AsObject();
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(fileName);
    }

    // REAL INTEGRATION: bounded pages through the ACTUAL socket parser + ChatHistoryLoader.LoadAsync/LoadOlderAsync
    // + real history merge, until exhaustion. Coverage is measured from the ACTUAL MERGED OUTPUT (every id exactly
    // once), never from the input dataset.
    [Fact]
    public async Task All4200_RealLoaderMergeDeliversEveryIdExactlyOnce()
    {
        using var rig = new Rig();
        const int total = 4200, page = 200;
        rig.Bridge.PageBehavior = options =>
        {
            var fromTail = options.Offset ?? 0;                 // 0 = newest page (tail-relative)
            var start = total - fromTail - page;
            if (start < 0) start = 0;
            var count = Math.Min(page, total - fromTail - start);
            if (count < 0) count = 0;
            var rows = Enumerable.Range(start, count).Select(i => Msg("id" + i, i + 1, "id" + i)).ToArray();
            var hasMore = (total - fromTail - page) > 0;
            return Task.FromResult(new GatewayChatHistoryPage("main", "sess-1", rows,
                HasMore: hasMore, NextOffset: hasMore ? fromTail + page : (int?)null,
                ResponseOffset: fromTail, Total: total));
        };
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        for (var i = 0; i < 64; i++)
        {
            var w = rig.State.GetHistoryWindow("main");
            if (w is null || !w.Value.HasMore) break;
            await rig.Loader.LoadOlderAsync("main").WaitAsync(Bounded);
        }
        var texts = Texts(rig.State, rig.Context);
        Assert.Equal(total, texts.Length);              // exact content from the REAL merged output
        Assert.Equal(total, texts.Distinct().Count());  // no duplicates
        Assert.Contains("id0", texts);
        Assert.Contains("id" + (total - 1), texts);
        Assert.Equal(1, rig.Bridge.PageRequests.Count(o => o.Offset == 0));   // tail fetched exactly once (no re-fetch)
    }

    // ACTUAL CALLER (real ChatHistoryLoader over the page path), not an unused model property.
    // An ORDINARY terminal page (hasMore=false, totalMessages present, completeSnapshot OMITTED by design) must
    // NOT read as unknown/partial/loss: the handler pagination markers prove a known ordinary page.
    [Fact]
    public async Task Caller_OrdinaryTerminalPageIsCommittedNotUnknownNotLoss()
    {
        using var rig = new Rig();
        var notifications = new List<ChatProviderNotification>();
        rig.Loader.Completed += (_, result) =>
        {
            if (result.Notification is not null)
                lock (notifications) notifications.Add(result.Notification);
        };
        rig.Bridge.PageBehavior = _ => Task.FromResult(
            new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.Equal(ChatHistoryPageCompleteness.OrdinaryTerminalPage, window!.Value.Completeness);
        Assert.False(window.Value.HasMore);
        Assert.Empty(notifications);                 // ordinary page is neither loss nor error
        Assert.Equal(["tail"], Texts(rig.State, rig.Context));
    }

    // ACTUAL source-REPORTED omission: the received content stays committed (valid rows preserved) and a VISIBLE
    // partial state is surfaced; the reported loss is never turned into a false complete or invented rows.
    [Fact]
    public async Task Caller_ReportedOmissionKeepsReceivedContentAndSurfacesPartial()
    {
        using var rig = new Rig();
        var notifications = new List<ChatProviderNotification>();
        rig.Loader.Completed += (_, result) =>
        {
            if (result.Notification is not null)
                lock (notifications) notifications.Add(result.Notification);
        };
        rig.Bridge.PageBehavior = _ => Task.FromResult(new GatewayChatHistoryPage(
            "main", "sess-1", [Msg("kept", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1,
            CompleteSnapshot: null, OmittedCount: 3, NormalizedBytes: 4096));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        var window = rig.State.GetHistoryWindow("main");
        Assert.NotNull(window);
        Assert.Equal(ChatHistoryPageCompleteness.ReportedLoss, window!.Value.Completeness);
        Assert.Equal(3, window.Value.OmittedCount);
        Assert.Equal(["kept"], Texts(rig.State, rig.Context));   // received content preserved incrementally
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);
    }

    // MALFORMED/UNRECOVERABLE page: existing valid history + accepted window are PRESERVED (no false complete),
    // and a visible error/retry state is surfaced.
    [Fact]
    public async Task Caller_MalformedPagePreservesExistingHistory()
    {
        using var rig = new Rig();
        rig.Bridge.PageBehavior = _ => Task.FromResult(
            new GatewayChatHistoryPage("main", "sess-1", [Msg("tail", 1)], HasMore: false, NextOffset: null, ResponseOffset: 0, Total: 1));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);
        var windowBefore = rig.State.GetHistoryWindow("main");

        var notifications = new List<ChatProviderNotification>();
        rig.Loader.Completed += (_, result) =>
        {
            if (result.Notification is not null)
                lock (notifications) notifications.Add(result.Notification);
        };
        rig.Bridge.PageBehavior = _ => Task.FromException<GatewayChatHistoryPage>(
            new ChatHistoryPageException("chat.history page omission.omittedCount is not a non-negative integer."));
        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        Assert.Equal(windowBefore, rig.State.GetHistoryWindow("main"));   // window untouched
        Assert.Equal(["tail"], Texts(rig.State, rig.Context));            // existing history preserved
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);
    }
}
