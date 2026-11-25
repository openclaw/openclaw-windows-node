using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Fake <see cref="IChatComposerRuntimePort"/> that records every call so
/// characterization tests can assert exact delegation counts/arguments without a
/// real <see cref="IChatDataProvider"/> or gateway bridge. Every method is
/// independently gated by a completed-by-default <see cref="TaskCompletionSource{T}"/>
/// so tests can hold a call in flight (delayed send, in-flight attachment additions).
/// </summary>
internal sealed class FakeChatComposerRuntimePort : IChatComposerRuntimePort
{
    public bool SupportsNativeLifecycle { get; set; } = true;
    public bool SupportsSessionScopedModelCatalog { get; set; }
    public long ModelCatalogRevision { get; set; }
    public string? SessionId { get; set; }
    public List<string> RequestedSessionModelKeys { get; } = new();
    public Func<string, CancellationToken, Task<ModelsListInfo?>>? SessionModelsBehavior { get; set; }

    public int SendMessageCallCount { get; private set; }
    public (string ThreadId, string Message, IReadOnlyList<ChatAttachment> Attachments)? LastSendMessageCall { get; private set; }
    public CancellationToken? LastSendMessageToken { get; private set; }
    public TaskCompletionSource<bool> SendMessageGate { get; set; } = Completed(true);
    public ChatSendOutcome SendFailureOutcome { get; set; } = ChatSendOutcome.Indeterminate;
    public string? LastExpectedSessionId { get; private set; }

    public int EnqueueCompactCallCount { get; private set; }
    public string? LastCompactThreadId { get; private set; }
    public TaskCompletionSource<bool> EnqueueCompactGate { get; set; } = Completed(true);

    public int ExecuteLifecycleCallCount { get; private set; }
    public (string ThreadId, ChatLifecycleCommandKind Command)? LastLifecycleCall { get; private set; }
    public TaskCompletionSource<ChatLifecycleCommandResult> ExecuteLifecycleGate { get; set; } =
        CompletedResult(new ChatLifecycleCommandResult(ChatLifecycleCommandKind.New, Succeeded: true));

    public int StopCallCount { get; private set; }
    public string? LastStopThreadId { get; private set; }
    public CancellationToken? LastStopToken { get; private set; }
    public TaskCompletionSource StopGate { get; set; } = CompletedVoid();

    public int CancelQueuedCallCount { get; private set; }
    public (string ThreadId, string MessageId)? LastCancelQueuedCall { get; private set; }
    public CancellationToken? LastCancelQueuedToken { get; private set; }

    public int SetModelCallCount { get; private set; }
    public (string ThreadId, string Model)? LastSetModelCall { get; private set; }
    public List<string> SetModelCallOrder { get; } = new();
    public CancellationToken? LastSetModelToken { get; private set; }
    public TaskCompletionSource SetModelGate { get; set; } = CompletedVoid();

    public int ClearModelCallCount { get; private set; }
    public string? LastClearModelThreadId { get; private set; }
    public CancellationToken? LastClearModelToken { get; private set; }

    public int SetThinkingLevelCallCount { get; private set; }
    public (string ThreadId, string Level)? LastSetThinkingLevelCall { get; private set; }
    public CancellationToken? LastSetThinkingLevelToken { get; private set; }

    public int ClearThinkingLevelCallCount { get; private set; }
    public string? LastClearThinkingLevelThreadId { get; private set; }
    public int PatchDraftOptionsCallCount { get; private set; }
    public (string ThreadId, SessionPatch Patch)? LastDraftOptionsCall { get; private set; }
    public TaskCompletionSource PatchDraftOptionsGate { get; set; } = CompletedVoid();
    public int CreateDefaultDraftSessionCallCount { get; private set; }
    public TaskCompletionSource<SessionCreateResult> CreateDefaultDraftSessionGate { get; set; } =
        CompletedResult(new SessionCreateResult { Ok = true, Key = "agent:main:gateway-default" });
    public int CreateDraftSessionCallCount { get; private set; }
    public string? LastDraftSessionAgentId { get; private set; }
    public TaskCompletionSource<SessionCreateResult> CreateDraftSessionGate { get; set; } =
        CompletedResult(new SessionCreateResult { Ok = true, Key = "agent:main:gateway-default" });
    public List<(string ThreadId, string Message)> LocalErrors { get; } = new();

    public int EnsureCommandCatalogCallCount { get; private set; }
    public CancellationToken? LastEnsureCommandCatalogToken { get; private set; }
    public string? LastSendIdempotencyKey { get; private set; }

    public string? GetSessionId(string threadId) => SessionId;

    public async Task<ChatSendOutcome> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? expectedSessionId = null)
    {
        SendMessageCallCount++;
        LastSendMessageCall = (threadId, message, attachments);
        LastSendIdempotencyKey = idempotencyKey;
        LastSendMessageToken = cancellationToken;
        LastExpectedSessionId = expectedSessionId;
        var accepted = await SendMessageGate.Task.ConfigureAwait(false);
        return accepted ? ChatSendOutcome.Accepted : SendFailureOutcome;
    }

    public Task<bool> EnqueueCompactCommandAsync(string threadId)
    {
        EnqueueCompactCallCount++;
        LastCompactThreadId = threadId;
        return EnqueueCompactGate.Task;
    }

    public Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(string threadId, ChatLifecycleCommandKind command)
    {
        ExecuteLifecycleCallCount++;
        LastLifecycleCall = (threadId, command);
        return ExecuteLifecycleGate.Task;
    }

    public void ReportLocalError(string threadId, string message) =>
        LocalErrors.Add((threadId, message));

    public Task StopResponseAsync(string threadId, CancellationToken cancellationToken)
    {
        StopCallCount++;
        LastStopThreadId = threadId;
        LastStopToken = cancellationToken;
        return StopGate.Task;
    }

    public Task CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken)
    {
        CancelQueuedCallCount++;
        LastCancelQueuedCall = (threadId, queuedMessageId);
        LastCancelQueuedToken = cancellationToken;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken)
    {
        SetModelCallCount++;
        LastSetModelCall = (threadId, model);
        SetModelCallOrder.Add(model);
        LastSetModelToken = cancellationToken;
        return SetModelGate.Task;
    }

    public Task ClearModelAsync(string threadId, CancellationToken cancellationToken)
    {
        ClearModelCallCount++;
        LastClearModelThreadId = threadId;
        LastClearModelToken = cancellationToken;
        return Task.CompletedTask;
    }

    public Task<ModelsListInfo?> RequestSessionModelsListAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        RequestedSessionModelKeys.Add(threadId);
        return SessionModelsBehavior?.Invoke(threadId, cancellationToken)
            ?? Task.FromResult<ModelsListInfo?>(null);
    }

    public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken)
    {
        SetThinkingLevelCallCount++;
        LastSetThinkingLevelCall = (threadId, thinkingLevel);
        LastSetThinkingLevelToken = cancellationToken;
        return Task.CompletedTask;
    }

    public Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken)
    {
        ClearThinkingLevelCallCount++;
        LastClearThinkingLevelThreadId = threadId;
        return Task.CompletedTask;
    }

    public Task PatchDraftOptionsConfirmedAsync(
        string threadId, SessionPatch patch, CancellationToken cancellationToken)
    {
        PatchDraftOptionsCallCount++;
        LastDraftOptionsCall = (threadId, patch);
        if (patch.Model.IsSpecified)
        {
            SetModelCallCount++;
            LastSetModelCall = (threadId, patch.Model.Value ?? "");
            SetModelCallOrder.Add(LastSetModelCall.Value.Model);
        }
        if (patch.ThinkingLevel.IsSpecified)
        {
            if (patch.ThinkingLevel.Value is null)
                ClearThinkingLevelCallCount++;
            else
            {
                SetThinkingLevelCallCount++;
                LastSetThinkingLevelCall = (threadId, patch.ThinkingLevel.Value);
            }
        }
        return PatchDraftOptionsGate.Task;
    }

    public Task<SessionCreateResult> CreateDefaultDraftSessionAsync(CancellationToken cancellationToken)
    {
        CreateDefaultDraftSessionCallCount++;
        return CreateDefaultDraftSessionGate.Task;
    }

    public Task<SessionCreateResult> CreateDraftSessionAsync(
        string agentId, CancellationToken cancellationToken)
    {
        CreateDraftSessionCallCount++;
        LastDraftSessionAgentId = agentId;
        return CreateDraftSessionGate.Task;
    }

    public Task EnsureCommandCatalogAsync(CancellationToken cancellationToken)
    {
        EnsureCommandCatalogCallCount++;
        LastEnsureCommandCatalogToken = cancellationToken;
        return Task.CompletedTask;
    }

    private static TaskCompletionSource<bool> Completed(bool result)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(result);
        return tcs;
    }

    private static TaskCompletionSource<T> CompletedResult<T>(T result)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(result);
        return tcs;
    }

    private static TaskCompletionSource CompletedVoid()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
