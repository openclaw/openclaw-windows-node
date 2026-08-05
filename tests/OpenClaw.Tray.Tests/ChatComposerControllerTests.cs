using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Characterization tests for <see cref="ChatComposerController"/>: local admission,
/// exact provider delegation, delayed-send draft/attachment survival, lifecycle
/// command routing (including attachments bypassing lifecycle parsing), reset
/// confirmation gating, queue/model/thinking/catalog delegation, and exactly-once
/// disposal with fenced late completions.
/// </summary>
public sealed class ChatComposerControllerTests
{
    private static ChatThread MakeThread(string id = "session-1") =>
        new()
        {
            Id = id,
            Title = "Test Session",
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
        };

    private static ChatComposerInputs MakeInputs(long revision = 1, ChatThread? thread = null, string connectionState = "connected") =>
        new(
            revision,
            connectionState,
            false,
            thread ?? MakeThread(),
            System.Array.Empty<ChatThread>(),
            System.Array.Empty<string>(),
            null,
            false,
            System.Array.Empty<ChatQueuedMessage>(),
            null,
            false);

    private static (ChatComposerViewModel Vm, ChatComposerController Controller, FakeChatComposerRuntimePort Port, ChatComposerHostActions HostActions)
        MakeController(ChatComposerHostActions? hostActions = null, RecordingUiDispatcher? dispatcher = null)
    {
        var vm = new ChatComposerViewModel(dispatcher ?? new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs());
        var port = new FakeChatComposerRuntimePort();
        var actions = hostActions ?? new ChatComposerHostActions(null, null, null, null, null);
        var controller = new ChatComposerController(vm, port, actions);
        return (vm, controller, port, actions);
    }

    [Fact]
    public async Task SendAsync_EmptySubmitIsBlocked()
    {
        var (_, controller, port, _) = MakeController();

        var accepted = await controller.SendAsync();

        Assert.False(accepted);
        Assert.Equal(0, port.SendMessageCallCount);
    }

    [Fact]
    public async Task SendAsync_DisconnectedIsBlocked()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs(connectionState: "disconnected"));
        vm.SetDraft("hello");
        var port = new FakeChatComposerRuntimePort();
        var controller = new ChatComposerController(vm, port, new ChatComposerHostActions(null, null, null, null, null));

        var accepted = await controller.SendAsync();

        Assert.False(accepted);
        Assert.Equal(0, port.SendMessageCallCount);
    }

    [Fact]
    public async Task SendAsync_OrdinaryMessage_CallsProviderExactlyOnceAndClearsDraft()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("hello world");

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(1, port.SendMessageCallCount);
        Assert.Equal(("session-1", "hello world", (IReadOnlyList<ChatAttachment>)System.Array.Empty<ChatAttachment>()), port.LastSendMessageCall);
        Assert.Equal(string.Empty, vm.Draft);
    }

    [Fact]
    public async Task SendAsync_AttachmentOnlySubmission_SendsEmptyMessageAndClearsAttachment()
    {
        var (vm, controller, port, _) = MakeController();
        var attachment = new ChatAttachment { FileName = "a.png" };
        vm.AddAttachments(new[] { attachment });

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(1, port.SendMessageCallCount);
        Assert.Empty(vm.PendingAttachments);
    }

    [Fact]
    public async Task SendAsync_EditDuringDelayedSend_DoesNotClearTheEditedDraft()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("original");
        port.SendMessageGate = new TaskCompletionSource<bool>();

        var sendTask = controller.SendAsync();
        Assert.Equal(1, port.SendMessageCallCount);

        // User keeps typing while the send is still in flight.
        vm.SetDraft("original edited further");

        port.SendMessageGate.SetResult(true);
        var accepted = await sendTask;

        Assert.True(accepted);
        Assert.Equal("original edited further", vm.Draft);
    }

    [Fact]
    public async Task SendAsync_AttachmentAddedWhileInFlight_SurvivesAcceptedSend()
    {
        var (vm, controller, port, _) = MakeController();
        var submitted = new ChatAttachment { FileName = "submitted.png" };
        vm.AddAttachments(new[] { submitted });
        port.SendMessageGate = new TaskCompletionSource<bool>();

        var sendTask = controller.SendAsync();
        var addedLater = new ChatAttachment { FileName = "added-later.png" };
        controller.AddAttachment(addedLater);

        port.SendMessageGate.SetResult(true);
        await sendTask;

        Assert.Single(vm.PendingAttachments);
        Assert.Same(addedLater, vm.PendingAttachments[0]);
    }

    [Fact]
    public async Task SendAsync_RejectedSend_PreservesDraftAndAttachments()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("keep me");
        var attachment = new ChatAttachment { FileName = "keep.png" };
        vm.AddAttachments(new[] { attachment });
        port.SendMessageGate = new TaskCompletionSource<bool>();
        port.SendMessageGate.SetResult(false);

        var accepted = await controller.SendAsync();

        Assert.False(accepted);
        Assert.Equal("keep me", vm.Draft);
        Assert.Single(vm.PendingAttachments);
    }

    [Fact]
    public async Task SendAsync_OneOperationAtATime_SecondCallWhileSendingIsBlocked()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("first");
        port.SendMessageGate = new TaskCompletionSource<bool>();

        var firstSend = controller.SendAsync();
        var secondAccepted = await controller.SendAsync();

        Assert.False(secondAccepted);
        Assert.Equal(1, port.SendMessageCallCount);

        port.SendMessageGate.SetResult(true);
        await firstSend;
    }

    [Fact]
    public async Task SendAsync_TwiceBeforeVmDrain_WithHeldDispatcher_OnlyOnePortInvocationThenAllowedAfterCompletion()
    {
        // Reproduces the exact gap the controller-owned interlocked send gate
        // closes: with a dispatcher that does not currently have thread access
        // and holds enqueued work (simulating an async-dispatched UI thread),
        // ChatComposerViewModel.SetSending(true)'s Mutate() call is only
        // *queued*, not yet applied — so IsSending cannot be relied upon to
        // block a second concurrent SendAsync() call issued before that queued
        // mutation drains. The controller must gate single-flight send with its
        // own state (_sendGate), independent of the VM's rendered/projected
        // IsSending value.
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var (vm, controller, port, _) = MakeController(dispatcher: dispatcher);
        vm.SetDraft("first");
        dispatcher.FlushPending(); // apply the draft so SendAsync's synchronous admission checks see it.
        port.SendMessageGate = new TaskCompletionSource<bool>();

        var firstSend = controller.SendAsync();

        // The VM's SetSending(true) mutation is only queued on the held
        // dispatcher right now, not yet applied — proving this is exactly the
        // scenario where rendered VM state cannot be trusted as the
        // single-flight guard.
        Assert.False(vm.IsSending);

        var secondAccepted = await controller.SendAsync();

        Assert.False(secondAccepted);
        Assert.Equal(1, port.SendMessageCallCount);

        port.SendMessageGate.SetResult(true);
        var firstAccepted = await firstSend;
        dispatcher.FlushPending();

        Assert.True(firstAccepted);

        // After the first send has fully completed (the gate is released in
        // SendAsync's finally block), a further send must be allowed again.
        port.SendMessageGate = new TaskCompletionSource<bool>();
        vm.SetDraft("second");
        dispatcher.FlushPending();

        var thirdSend = controller.SendAsync();
        port.SendMessageGate.SetResult(true);
        var thirdAccepted = await thirdSend;

        Assert.True(thirdAccepted);
        Assert.Equal(2, port.SendMessageCallCount);
    }

    [Fact]
    public void Disposed_FieldIsDeclaredVolatile()
    {
        // Structural guard for the cross-lock/no-lock disposal-visibility fix:
        // _disposed has no lock protecting it at all in this class (there is no
        // lock anywhere in ChatComposerController), so every read/write relies
        // entirely on `volatile` for cross-thread visibility. This fails if a
        // future edit ever removes the keyword.
        var field = typeof(ChatComposerController).GetField(
            "_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Contains(typeof(IsVolatile), field!.GetRequiredCustomModifiers());
    }

    [Fact]
    public async Task SendAsync_NewCommand_HandsCanonicalSessionKeyToBoundSelection()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("/new");
        port.ExecuteLifecycleGate = new TaskCompletionSource<ChatLifecycleCommandResult>();
        port.ExecuteLifecycleGate.SetResult(new ChatLifecycleCommandResult(
            ChatLifecycleCommandKind.New,
            Succeeded: true,
            NewSessionKey: "new-session-key"));
        string? handedOff = null;
        controller.BindSelectionHandoff(key => handedOff = key);

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(1, port.ExecuteLifecycleCallCount);
        Assert.Equal(ChatLifecycleCommandKind.New, port.LastLifecycleCall!.Value.Command);
        Assert.Equal("new-session-key", handedOff);
        Assert.Equal(0, port.SendMessageCallCount);
    }

    [Fact]
    public async Task SendAsync_Compact_UsesQueuePathNotLifecycleExecute()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("/compact");

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(1, port.EnqueueCompactCallCount);
        Assert.Equal(0, port.ExecuteLifecycleCallCount);
        Assert.Equal(0, port.SendMessageCallCount);
    }

    [Fact]
    public async Task SendAsync_Compact_DisposedWhileEnqueueInFlight_DoesNotReportAccepted()
    {
        // /compact must recheck disposal/generation after its await too, exactly
        // like the ordinary-send and lifecycle-execute paths: an enqueue that
        // resolves true after the controller was disposed must not be reported (or
        // treated) as an accepted send outcome.
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("/compact");
        port.EnqueueCompactGate = new TaskCompletionSource<bool>();

        var sendTask = controller.SendAsync();
        Assert.Equal(1, port.EnqueueCompactCallCount);
        controller.Dispose();
        port.EnqueueCompactGate.SetResult(true);
        var accepted = await sendTask;

        Assert.False(accepted);
    }

    [Fact]
    public async Task AllPublicEntryPoints_NoOpAfterDispose_WithZeroProviderHostOrViewModelCalls()
    {
        var voiceRequestCalls = 0;
        var confirmResetCalls = 0;
        var speakerMuteCalls = 0;
        var actions = new ChatComposerHostActions(
            ConfirmResetAsync: (_, _) => { confirmResetCalls++; return Task.FromResult(true); },
            AttachmentPickerRequest: null,
            VoiceCaptureRequest: (_, _) => { voiceRequestCalls++; return Task.FromResult<string?>("x"); },
            SettingsNavigation: null,
            SpeakerMuteChanged: _ => speakerMuteCalls++);
        var (vm, controller, port, _) = MakeController(actions);
        var handoffCalls = 0;
        controller.BindSelectionHandoff(_ => handoffCalls++);

        controller.Dispose();
        Assert.True(controller.IsDisposed);

        // Re-bind after dispose must also no-op (BindSelectionHandoff itself checks
        // disposed before touching the field).
        var rebindHandoffCalls = 0;
        controller.BindSelectionHandoff(_ => rebindHandoffCalls++);

        controller.SelectChannel("some-thread");
        controller.Stop();
        controller.CancelQueuedMessage("q1");
        controller.SetModel("model-x");
        controller.ClearModel();
        controller.SetThinkingLevel("high");
        controller.RequestCommandCatalog();
        controller.AddAttachment(new ChatAttachment { FileName = "a.png" });
        controller.AddAttachments(new[] { new ChatAttachment { FileName = "b.png" } });
        controller.RemoveAttachment(new ChatAttachment { FileName = "c.png" });
        controller.ToggleSpeakerMuted();
        controller.StartVoiceRecording();
        controller.StopVoiceRecording();
        var sendAccepted = await controller.SendAsync();
        var sendCoreAccepted = await controller
            .SendCoreAsync("thread", "Title", "hello", Array.Empty<ChatAttachment>());

        // Zero calls reached the port, the host actions, or mutated the view model.
        Assert.Equal(0, port.SendMessageCallCount);
        Assert.Equal(0, port.EnqueueCompactCallCount);
        Assert.Equal(0, port.ExecuteLifecycleCallCount);
        Assert.Equal(0, port.StopCallCount);
        Assert.Equal(0, port.CancelQueuedCallCount);
        Assert.Equal(0, port.SetModelCallCount);
        Assert.Equal(0, port.ClearModelCallCount);
        Assert.Equal(0, port.SetThinkingLevelCallCount);
        Assert.Equal(0, port.EnsureCommandCatalogCallCount);
        Assert.Equal(0, voiceRequestCalls);
        Assert.Equal(0, confirmResetCalls);
        Assert.Equal(0, speakerMuteCalls);
        Assert.Equal(0, handoffCalls);
        Assert.Equal(0, rebindHandoffCalls);
        Assert.Empty(vm.PendingAttachments);
        Assert.False(vm.IsSpeakerMuted);
        Assert.False(vm.IsRecording);
        Assert.False(sendAccepted);
        Assert.False(sendCoreAccepted);
    }

    [Fact]
    public async Task SendAsync_Reset_ConfirmationDeclined_DoesNotExecute()
    {
        var actions = new ChatComposerHostActions(
            ConfirmResetAsync: (_, _) => Task.FromResult(false),
            null, null, null, null);
        var (vm, controller, port, _) = MakeController(actions);
        vm.SetDraft("/reset");

        var accepted = await controller.SendAsync();

        Assert.False(accepted);
        Assert.Equal(0, port.ExecuteLifecycleCallCount);
    }

    [Fact]
    public async Task SendAsync_Reset_ConfirmationAccepted_Executes()
    {
        var actions = new ChatComposerHostActions(
            ConfirmResetAsync: (_, _) => Task.FromResult(true),
            null, null, null, null);
        var (vm, controller, port, _) = MakeController(actions);
        vm.SetDraft("/reset");
        port.ExecuteLifecycleGate = new TaskCompletionSource<ChatLifecycleCommandResult>();
        port.ExecuteLifecycleGate.SetResult(new ChatLifecycleCommandResult(ChatLifecycleCommandKind.Reset, Succeeded: true));

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(ChatLifecycleCommandKind.Reset, port.LastLifecycleCall!.Value.Command);
    }

    [Fact]
    public async Task SendAsync_Reset_DisposedWhileConfirmationDialogOpen_DoesNotExecute()
    {
        // Guards against a controller disposed (e.g. provider replaced/host torn
        // down) while the user is still looking at the reset confirmation dialog:
        // the confirmation resuming with "true" must not still execute the
        // destructive reset command against a disposed controller/port.
        var confirmGate = new TaskCompletionSource<bool>();
        var actions = new ChatComposerHostActions(
            ConfirmResetAsync: (_, _) => confirmGate.Task,
            null, null, null, null);
        var (vm, controller, port, _) = MakeController(actions);
        vm.SetDraft("/reset");

        var sendTask = controller.SendAsync();
        controller.Dispose();
        confirmGate.SetResult(true);
        var accepted = await sendTask;

        Assert.False(accepted);
        Assert.Equal(0, port.ExecuteLifecycleCallCount);
    }

    [Fact]
    public async Task SendAsync_New_DisposedBeforeLifecycleExecuteCompletes_DoesNotInvokeSelectionHandoff()
    {
        // Guards against a "/new" whose ExecuteLifecycleCommandAsync resolves after
        // dispose: the stale session-key handoff must not fire into a torn-down root.
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("/new");
        port.ExecuteLifecycleGate = new TaskCompletionSource<ChatLifecycleCommandResult>();
        string? handedOff = null;
        controller.BindSelectionHandoff(key => handedOff = key);

        var sendTask = controller.SendAsync();
        controller.Dispose();
        port.ExecuteLifecycleGate.SetResult(new ChatLifecycleCommandResult(
            ChatLifecycleCommandKind.New,
            Succeeded: true,
            NewSessionKey: "new-session-key"));
        var accepted = await sendTask;

        Assert.False(accepted);
        Assert.Null(handedOff);
    }

    [Fact]
    public async Task SendAsync_AttachmentsBypassLifecycleParsing()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("/new");
        vm.AddAttachments(new[] { new ChatAttachment { FileName = "a.png" } });

        var accepted = await controller.SendAsync();

        Assert.True(accepted);
        Assert.Equal(0, port.ExecuteLifecycleCallCount);
        Assert.Equal(1, port.SendMessageCallCount);
        Assert.Equal("/new", port.LastSendMessageCall!.Value.Message);
    }

    [Fact]
    public void Stop_DelegatesExactlyOnceWithCurrentThreadId()
    {
        var (_, controller, port, _) = MakeController();

        controller.Stop();

        // FireAndForget invokes the port call synchronously (only the awaiting of
        // its result is deferred), so the call count is observable immediately —
        // this also preserves call order for rapid successive invocations.
        Assert.Equal(1, port.StopCallCount);
        Assert.Equal("session-1", port.LastStopThreadId);
    }

    [Fact]
    public void CancelQueuedMessage_DelegatesExactId()
    {
        var (_, controller, port, _) = MakeController();

        controller.CancelQueuedMessage("queued-42");

        Assert.Equal(1, port.CancelQueuedCallCount);
        Assert.Equal(("session-1", "queued-42"), port.LastCancelQueuedCall);
    }

    [Fact]
    public void SetModel_DelegatesConcreteModel()
    {
        var (_, controller, port, _) = MakeController();

        controller.SetModel("gpt-5");

        Assert.Equal(1, port.SetModelCallCount);
        Assert.Equal(0, port.ClearModelCallCount);
        Assert.Equal(("session-1", "gpt-5"), port.LastSetModelCall);
    }

    [Fact]
    public void ClearModel_CallsExplicitClearNotSet()
    {
        var (_, controller, port, _) = MakeController();

        controller.ClearModel();

        Assert.Equal(1, port.ClearModelCallCount);
        Assert.Equal(0, port.SetModelCallCount);
    }

    [Fact]
    public void SetModel_RapidSuccessiveCalls_InvokeThePortSynchronouslyInCallOrder()
    {
        // The port call itself must happen synchronously at the call site (only
        // awaiting its completion is deferred), so two rapid model picks reach the
        // provider in the order the user made them, not in whatever order a
        // thread-pool-deferred invocation happens to schedule them.
        var (_, controller, port, _) = MakeController();

        controller.SetModel("first-pick");
        controller.SetModel("second-pick");

        Assert.Equal(new[] { "first-pick", "second-pick" }, port.SetModelCallOrder);
    }

    [Fact]
    public void SetThinkingLevel_DelegatesExactLevel()
    {
        var (_, controller, port, _) = MakeController();

        controller.SetThinkingLevel("high");

        Assert.Equal(1, port.SetThinkingLevelCallCount);
        Assert.Equal(("session-1", "high"), port.LastSetThinkingLevelCall);
    }

    [Fact]
    public void RequestCommandCatalog_DelegatesToPort()
    {
        var (_, controller, port, _) = MakeController();

        controller.RequestCommandCatalog();

        Assert.Equal(1, port.EnsureCommandCatalogCallCount);
    }

    [Fact]
    public async Task StartVoiceRecording_AppendsTranscriptOnCompletion()
    {
        var voiceGate = new TaskCompletionSource<string?>();
        var actions = new ChatComposerHostActions(
            null,
            null,
            VoiceCaptureRequest: (_, _) => voiceGate.Task,
            null,
            null);
        var (vm, controller, _, _) = MakeController(actions);

        controller.StartVoiceRecording();
        Assert.True(vm.IsRecording);

        voiceGate.SetResult("hello from voice");
        await Task.Delay(20);

        Assert.False(vm.IsRecording);
        Assert.Equal("hello from voice", vm.Draft);
    }

    [Fact]
    public async Task Dispose_CancelsVoiceAndFencesLateCompletionFromMutatingViewModel()
    {
        var voiceGate = new TaskCompletionSource<string?>();
        var actions = new ChatComposerHostActions(
            null,
            null,
            VoiceCaptureRequest: (ct, _) => voiceGate.Task,
            null,
            null);
        var (vm, controller, _, _) = MakeController(actions);
        controller.StartVoiceRecording();

        controller.Dispose();
        Assert.True(controller.IsDisposed);

        // Late completion arrives after dispose; it must not mutate the view model.
        voiceGate.SetResult("late transcript");
        await Task.Delay(20);

        Assert.Equal(string.Empty, vm.Draft);
    }

    [Fact]
    public async Task SendAsync_AfterDispose_ReturnsFalseWithoutCallingPort()
    {
        var (vm, controller, port, _) = MakeController();
        vm.SetDraft("hello");
        controller.Dispose();

        var accepted = await controller.SendAsync();

        Assert.False(accepted);
        Assert.Equal(0, port.SendMessageCallCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (_, controller, _, _) = MakeController();

        controller.Dispose();
        var exception = Record.Exception(controller.Dispose);

        Assert.Null(exception);
    }
}
