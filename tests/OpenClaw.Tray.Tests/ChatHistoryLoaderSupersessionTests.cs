using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Actual loader end-to-end proof via ChatHistoryLoader.TestReconstructionBarrier.
/// Repairs from review: a rig object with LIVE counters (asserted 0 after supersession), a unique
/// sentinel history entry (asserted absent after supersession, present on the normal control), and a
/// DEDICATED caller thread (not a reused pool thread) that blocks while the worker runs, so the
/// off-caller-thread claim is robust. Barriers are bounded and released/joined in finally; no sleeps.
/// Native WinUI execution remains unverified.
/// </summary>
public class ChatHistoryLoaderSupersessionTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);
    private const string Sentinel = "SENTINEL-UNDER-TEST-9f3c-4d1e";
    private const string SessionId = "sess-1";

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Bridge = new CompletedHistoryBridge();
            var dir = Directory.CreateTempSubdirectory("oc-loader-rig-").FullName;
            Metadata = new ChatMetadataStore(Path.Combine(dir, "tool-meta.json"));
            Persistence = new ChatStatePersistence(Path.Combine(dir, "last-state.json"));
            State = new ChatConversationState(ConnectionStatus.Connected, lastChatState: null, seedModels: null);
            Loader = new ChatHistoryLoader(
                Bridge, State, Metadata, Persistence, new ChatTelemetryTracker(),
                retryScheduler: (_, _, _) => { Interlocked.Increment(ref Retries); return Task.CompletedTask; },
                failureReservedForTesting: () => Interlocked.Increment(ref Failures));
        }

        public CompletedHistoryBridge Bridge { get; }
        public ChatMetadataStore Metadata { get; }
        public ChatStatePersistence Persistence { get; }
        public ChatConversationState State { get; }
        public ChatHistoryLoader Loader { get; }
        // Live fields (not returned by value) so the closures above and the assertions see the same storage.
        public int Retries;
        public int Failures;

        public void Dispose()
        {
            Loader.Dispose();
            Bridge.Dispose();
        }
    }

    private sealed class CompletedHistoryBridge : IChatGatewayBridge
    {
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
        // ALREADY-COMPLETED task carrying a unique sentinel entry.
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
            Task.FromResult(new ChatHistoryInfo
            {
                SessionId = SessionId,
                SessionKey = "main",
                Messages = [new ChatMessageInfo { SessionKey = "main", Role = "user", Text = Sentinel, OpenClawSeq = 1 }],
            });

        // Synthetic bounded page the actual loader now consumes (one exhausted page with the sentinel).
        public Task<GatewayChatHistoryPage> RequestChatHistoryPageAsync(
            string? sessionKey,
            ChatHistoryPageOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayChatHistoryPage(
                SessionKey: sessionKey ?? "main",
                SessionId: SessionId,
                Messages: [new ChatMessageInfo { SessionKey = "main", Role = "user", Text = Sentinel, OpenClawSeq = 1 }],
                HasMore: false,
                NextOffset: null,
                ResponseOffset: options.Offset,
                Total: 1));
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

    private static bool SnapshotHasSentinel(ChatConversationState state, ChatProjectionContext context) =>
        state.Snapshot(context).Timelines.TryGetValue("main", out var timeline)
        && timeline.Entries.Any(entry => entry.Text.Contains(Sentinel, StringComparison.Ordinal));

    [Fact]
    public async Task SupersededGeneration_DuringHeldReconstruction_DoesNotCommitPublishOrRetry()
    {
        using var rig = new Rig();
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        rig.State.Load([new SessionInfo { Key = "main", IsMain = true }], context);

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var superseded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;
        rig.Loader.Completed += (_, _) => Interlocked.Increment(ref published);

        var workerThread = 0;
        rig.Loader.TestReconstructionBarrier = async () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Bounded);
        };

        // Dedicated caller thread (not a reused pool thread) stands in for the UI caller.
        var callerThreadId = 0;
        Task? load = null;
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            load = rig.Loader.LoadAsync("main", force: true, authoritative: true);
            entered.Task.Wait(Bounded);                 // block the dedicated caller while the worker runs
            rig.State.ResetThread("main", context);     // advance/reset generation via production method
            superseded.TrySetResult(true);
        }) { IsBackground = true, Name = "loader-caller" };

        try
        {
            caller.Start();
            await entered.Task.WaitAsync(Bounded);
            await superseded.Task.WaitAsync(Bounded);
        }
        finally
        {
            release.TrySetResult(true);
            if (!caller.Join(Bounded))
                throw new TimeoutException("dedicated caller thread did not finish");
        }

        await load!.WaitAsync(Bounded);

        Assert.NotEqual(callerThreadId, workerThread);   // robust off-caller-thread proof
        Assert.Equal(0, Volatile.Read(ref published));   // no publication
        Assert.Equal(0, Volatile.Read(ref rig.Retries)); // no retry scheduled
        Assert.Equal(0, Volatile.Read(ref rig.Failures));
        Assert.False(SnapshotHasSentinel(rig.State, context)); // superseded sentinel never committed
    }

    [Fact]
    public async Task NormalCompletedSource_CommitsPublishesAndBindsSentinel()
    {
        using var rig = new Rig();
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        rig.State.Load([new SessionInfo { Key = "main", IsMain = true }], context);

        var published = 0;
        rig.Loader.Completed += (_, _) => Interlocked.Increment(ref published);

        await rig.Loader.LoadAsync("main", force: true, authoritative: true).WaitAsync(Bounded);

        Assert.Equal(1, Volatile.Read(ref published));
        Assert.Equal(0, Volatile.Read(ref rig.Retries));
        Assert.True(SnapshotHasSentinel(rig.State, context)); // committed with the expected content
    }
}
