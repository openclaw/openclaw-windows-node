using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class PendingRequestRegistryTests
{
    [Fact]
    public void DiagnosticObserver_ReportsAcceptedRegistrationAndResponseClassification()
    {
        using var registry = CreateOpenRegistry();
        var diagnostics = new List<PendingRequestDiagnostic>();
        registry.DiagnosticObserver = diagnostics.Add;

        var registration = registry.RegisterMethod("proof-id", "sessions.list");
        var take = registry.TakeForResponse("proof-id");

        Assert.True(registration.Accepted);
        Assert.Equal(PendingResponseDisposition.Active, take.Disposition);
        Assert.Collection(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal(PendingRequestDiagnosticStage.Registered, diagnostic.Stage);
                Assert.Equal("proof-id", diagnostic.RequestId);
                Assert.Equal("sessions.list", diagnostic.Method);
                Assert.Equal(PendingRequestKind.Method, diagnostic.Kind);
                Assert.Null(diagnostic.Disposition);
            },
            diagnostic =>
            {
                Assert.Equal(PendingRequestDiagnosticStage.ResponseClassified, diagnostic.Stage);
                Assert.Equal("proof-id", diagnostic.RequestId);
                Assert.Equal("sessions.list", diagnostic.Method);
                Assert.Equal(PendingRequestKind.Method, diagnostic.Kind);
                Assert.Equal(PendingResponseDisposition.Active, diagnostic.Disposition);
            });
    }

    [Fact]
    public void MethodRegisterTakeAndRemove_AreAtomicAndTombstoned()
    {
        using var registry = CreateOpenRegistry();

        Assert.True(registry.RegisterMethod("take-id", "health").Accepted);
        var take = registry.TakeForResponse("take-id");

        Assert.Equal(PendingResponseDisposition.Active, take.Disposition);
        Assert.Equal(PendingRequestKind.Method, take.Request!.Kind);
        Assert.Equal("health", take.Request.Method);
        Assert.Equal(0, registry.ActiveCount);
        Assert.Equal(
            PendingResponseDisposition.Tombstoned,
            registry.TakeForResponse("take-id").Disposition);

        Assert.True(registry.RegisterMethod("remove-id", "sessions.list").Accepted);
        Assert.True(registry.Remove("remove-id"));
        Assert.False(registry.Remove("remove-id"));
        Assert.Equal(
            PendingResponseDisposition.Tombstoned,
            registry.TakeForResponse("remove-id").Disposition);
    }

    [Fact]
    public void WizardRegisterTake_PreservesTypedCompletion()
    {
        using var registry = CreateOpenRegistry();
        var completion = NewCompletion<JsonElement>();

        Assert.True(registry.RegisterWizard("wizard-id", "wizard.next", completion).Accepted);
        var take = registry.TakeForResponse("wizard-id");

        Assert.Equal(PendingRequestKind.Wizard, take.Request!.Kind);
        Assert.Equal("wizard.next", take.Request.Method);
        Assert.Same(completion, take.Request.WizardCompletion);
    }

    [Fact]
    public void ChatSendRegisterTake_PreservesTypedCompletion()
    {
        using var registry = CreateOpenRegistry();
        var completion = NewCompletion<ChatSendResult>();

        Assert.True(registry.RegisterChatSend("chat-id", completion).Accepted);
        var take = registry.TakeForResponse("chat-id");

        Assert.Equal(PendingRequestKind.ChatSend, take.Request!.Kind);
        Assert.Equal("chat.send", take.Request.Method);
        Assert.Same(completion, take.Request.ChatSendCompletion);
    }

    [Fact]
    public void ApprovalRegisterTake_PreservesTypedCompletion()
    {
        using var registry = CreateOpenRegistry();
        var completion = NewCompletion<bool>();

        Assert.True(registry.RegisterApproval("approval-id", completion).Accepted);
        var take = registry.TakeForResponse("approval-id");

        Assert.Equal(PendingRequestKind.ApprovalResolve, take.Request!.Kind);
        Assert.Equal("exec.approval.resolve", take.Request.Method);
        Assert.Same(completion, take.Request.ApprovalCompletion);
    }

    [Fact]
    public void SessionSnapshotRegisterTake_PreservesTypedCompletion()
    {
        using var registry = CreateOpenRegistry();
        var completion = NewCompletion<SessionInfo[]>();

        Assert.True(registry.RegisterSessionSnapshot("sessions-id", completion).Accepted);
        var take = registry.TakeForResponse("sessions-id");

        Assert.Equal(PendingRequestKind.SessionSnapshot, take.Request!.Kind);
        Assert.Equal("sessions.list", take.Request.Method);
        Assert.Same(completion, take.Request.SessionSnapshotCompletion);
    }

    [Fact]
    public async Task Cancel_RemovesTombstonesAndCancelsTypedOwner()
    {
        using var registry = CreateOpenRegistry();
        var completion = NewCompletion<ChatSendResult>();
        Assert.True(registry.RegisterChatSend("cancel-id", completion).Accepted);

        Assert.True(
            registry.Cancel(
                "cancel-id",
                new OperationCanceledException("caller canceled")));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await completion.Task);
        Assert.Equal("caller canceled", exception.Message);
        Assert.Equal(0, registry.ActiveCount);
        Assert.Equal(
            PendingResponseDisposition.Tombstoned,
            registry.TakeForResponse("cancel-id").Disposition);
    }

    [Fact]
    public async Task CloseForDisconnect_CancelsEveryTypedKindAndClosesRegistration()
    {
        using var registry = CreateOpenRegistry();
        var wizard = NewCompletion<JsonElement>();
        var chat = NewCompletion<ChatSendResult>();
        var approval = NewCompletion<bool>();
        var sessions = NewCompletion<SessionInfo[]>();

        Assert.True(registry.RegisterMethod("method", "health").Accepted);
        Assert.True(registry.RegisterWizard("wizard", "wizard.status", wizard).Accepted);
        Assert.True(registry.RegisterChatSend("chat", chat).Accepted);
        Assert.True(registry.RegisterApproval("approval", approval).Accepted);
        Assert.True(registry.RegisterSessionSnapshot("sessions", sessions).Accepted);

        registry.CloseForDisconnect();

        Assert.False(registry.IsAcceptingRegistrations);
        Assert.Equal(0, registry.ActiveCount);
        Assert.False(registry.RegisterMethod("after-close", "health").Accepted);
        await AssertCanceledWithMessage(wizard.Task, "wizard response");
        await AssertCanceledWithMessage(chat.Task, "Request canceled");
        await AssertCanceledWithMessage(approval.Task, "exec.approval.resolve response");
        await AssertCanceledWithMessage(sessions.Task, "sessions.list response");
        Assert.Equal(
            PendingResponseDisposition.Tombstoned,
            registry.TakeForResponse("method").Disposition);
    }

    [Fact]
    public async Task Dispose_CancelsActiveRequestsAndPermanentlyClosesRegistration()
    {
        var registry = CreateOpenRegistry();
        var completion = NewCompletion<JsonElement>();
        Assert.True(registry.RegisterWizard("dispose-id", "wizard.cancel", completion).Accepted);

        registry.Dispose();
        registry.Reopen();

        Assert.True(registry.IsDisposed);
        Assert.False(registry.IsAcceptingRegistrations);
        Assert.False(registry.RegisterMethod("after-dispose", "health").Accepted);
        await AssertCanceledWithMessage(completion.Task, "wizard response");
    }

    [Fact]
    public void CompletedIdLedger_EvictsInFifoOrderAtCapacity()
    {
        using var registry = new PendingRequestRegistry(completedIdCapacity: 3);
        registry.Reopen();

        for (var index = 0; index < 4; index++)
        {
            var requestId = $"id-{index}";
            Assert.True(registry.RegisterMethod(requestId, "health").Accepted);
            Assert.Equal(
                PendingResponseDisposition.Active,
                registry.TakeForResponse(requestId).Disposition);
        }

        Assert.Equal(3, registry.CompletedCount);
        Assert.Equal(
            PendingResponseDisposition.Ownerless,
            registry.TakeForResponse("id-0").Disposition);
        Assert.Equal(
            PendingResponseDisposition.Tombstoned,
            registry.TakeForResponse("id-1").Disposition);
    }

    [Fact]
    public void RequestIdReuse_RemovesOldTombstoneAndNewRegistrationOwnsResponse()
    {
        using var registry = CreateOpenRegistry();
        Assert.True(registry.RegisterMethod("reused", "health").Accepted);
        Assert.Equal(
            PendingResponseDisposition.Active,
            registry.TakeForResponse("reused").Disposition);

        Assert.True(registry.RegisterMethod("reused", "sessions.list").Accepted);
        Assert.Equal(0, registry.CompletedCount);

        var reused = registry.TakeForResponse("reused");
        Assert.Equal(PendingResponseDisposition.Active, reused.Disposition);
        Assert.Equal("sessions.list", reused.Request!.Method);
    }

    [Fact]
    public async Task ActiveIdReplacement_CancelsPriorCompletionAndInstallsNewOwner()
    {
        using var registry = CreateOpenRegistry();
        var prior = NewCompletion<JsonElement>();
        var replacement = NewCompletion<bool>();
        var priorRegistration =
            registry.RegisterWizard("same-id", "wizard.next", prior);
        Assert.True(priorRegistration.Accepted);

        Assert.True(registry.RegisterApproval("same-id", replacement).Accepted);

        await AssertCanceledWithMessage(prior.Task, "registered again");
        Assert.False(registry.Remove(priorRegistration));
        var take = registry.TakeForResponse("same-id");
        var request = Assert.IsType<PendingRequest>(take.Request);
        Assert.Equal(PendingRequestKind.ApprovalResolve, request.Kind);
        Assert.Same(replacement, request.ApprovalCompletion);
        request.ApprovalCompletion!.TrySetResult(true);
        Assert.True(await replacement.Task);
    }

    [Fact]
    public void UnknownResponseId_IsOwnerlessAndNeverClaimsAnActiveOwner()
    {
        using var registry = CreateOpenRegistry();
        Assert.True(registry.RegisterMethod("active", "health").Accepted);

        var unknown = registry.TakeForResponse("unknown");

        Assert.Equal(PendingResponseDisposition.Ownerless, unknown.Disposition);
        Assert.Null(unknown.Request);
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public async Task ReopenAfterDisconnect_AcceptsNewGenerationButDisposeCannotReopen()
    {
        var registry = CreateOpenRegistry();
        var disconnected = NewCompletion<JsonElement>();
        Assert.True(registry.RegisterWizard("old", "wizard.status", disconnected).Accepted);
        registry.CloseForDisconnect();

        registry.Reopen();
        Assert.True(registry.IsAcceptingRegistrations);
        Assert.True(registry.RegisterMethod("new", "health").Accepted);
        Assert.Equal(
            PendingResponseDisposition.Active,
            registry.TakeForResponse("new").Disposition);
        await AssertCanceledWithMessage(disconnected.Task, "wizard response");

        registry.Dispose();
        registry.Reopen();
        Assert.False(registry.IsAcceptingRegistrations);
        Assert.False(registry.RegisterMethod("never", "health").Accepted);
    }

    [Fact]
    public void ConcurrentRegisterAndTake_LeavesNoActiveRequestsAndBoundsTombstones()
    {
        using var registry = CreateOpenRegistry();
        const int requestCount = 2_000;
        var failures = new ConcurrentQueue<string>();

        Parallel.For(
            0,
            requestCount,
            index =>
            {
                var requestId = $"concurrent-{index}";
                if (!registry.RegisterMethod(requestId, "health").Accepted)
                {
                    failures.Enqueue($"register:{requestId}");
                    return;
                }

                var take = registry.TakeForResponse(requestId);
                if (take.Disposition != PendingResponseDisposition.Active ||
                    take.Request?.Method != "health")
                {
                    failures.Enqueue($"take:{requestId}");
                }
            });

        Assert.Empty(failures);
        Assert.Equal(0, registry.ActiveCount);
        Assert.Equal(PendingRequestRegistry.DefaultCompletedIdCapacity, registry.CompletedCount);
    }

    [Fact]
    public async Task ConcurrentDisconnectAndRegistration_CompletesEveryTypedOwner()
    {
        using var registry = CreateOpenRegistry();
        const int requestCount = 512;
        var completions = Enumerable
            .Range(0, requestCount)
            .Select(_ => NewCompletion<JsonElement>())
            .ToArray();
        using var start = new ManualResetEventSlim();

        var registrations = Task.Run(
            () => Parallel.For(
                0,
                requestCount,
                index =>
                {
                    start.Wait();
                    registry.RegisterWizard(
                        $"race-{index}",
                        "wizard.status",
                        completions[index]);
                }));
        var disconnect = Task.Run(
            () =>
            {
                start.Wait();
                registry.CloseForDisconnect();
            });

        start.Set();
        await Task.WhenAll(registrations, disconnect);

        Assert.Equal(0, registry.ActiveCount);
        Assert.All(completions, completion => Assert.True(completion.Task.IsCompleted));
        foreach (var completion in completions)
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await completion.Task);
    }

    private static PendingRequestRegistry CreateOpenRegistry()
    {
        var registry = new PendingRequestRegistry();
        registry.Reopen();
        return registry;
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertCanceledWithMessage<T>(
        Task<T> task,
        string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await task);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
