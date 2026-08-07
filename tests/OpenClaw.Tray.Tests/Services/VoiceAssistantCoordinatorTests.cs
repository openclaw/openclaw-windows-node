using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.VoiceAssistant;

namespace OpenClaw.Tray.Tests.Services;

public sealed class VoiceAssistantCoordinatorTests
{
    [Fact]
    public async Task BackToBackMatches_ProduceOneSend()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, first request");
        input.Emit("OpenClaw, second request");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        Assert.Equal(new[] { "First request" }, chat.Requests);
    }

    [Fact]
    public async Task QueuedDisposition_IsCanceledAndNeverWaits()
    {
        var input = new FakeInput();
        var chat = new FakeChat { Disposition = VoiceAssistantSendDisposition.Queued };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, queued request");
        await WaitUntilAsync(() => chat.Canceled == 1);

        Assert.NotEqual(VoiceAssistantState.WaitingForReply, coordinator.State);
        Assert.Empty(speaker.Spoken);
    }

    [Fact]
    public async Task TerminatedDisposition_ResumesListeningWithoutCancel()
    {
        var input = new FakeInput();
        var chat = new FakeChat { Disposition = VoiceAssistantSendDisposition.Terminated };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, fast failure");
        await WaitUntilAsync(() => chat.Requests.Count == 1);
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        Assert.Single(chat.Requests);
        Assert.Equal(0, chat.Canceled);
        Assert.Empty(speaker.Spoken);
    }

    [Fact]
    public async Task DirectReceiptInvalidatedBeforeRegistration_ResumesListeningWithoutCancel()
    {
        var input = new FakeInput();
        var chat = new FakeChat { InvalidateBeforeSendReturns = true };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, fast invalidation");
        await WaitUntilAsync(() => chat.Requests.Count == 1);
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        Assert.Equal(0, chat.Canceled);
        Assert.Empty(speaker.Spoken);
    }

    [Fact]
    public async Task BufferedResponseBeforeReceiptRegistration_IsSpoken()
    {
        var input = new FakeInput();
        var chat = new FakeChat { BufferedResponse = "fast response" };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, answer quickly");
        await WaitUntilAsync(() =>
            speaker.Spoken.Count == 1 &&
            coordinator.State == VoiceAssistantState.WakeListening);

        Assert.Equal(["fast response"], speaker.Spoken);
    }

    [Fact]
    public async Task ReentrantInvalidation_PublishesWaitingBeforeUnavailable()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        var notifications = new List<VoiceAssistantState>();
        var notificationsGate = new object();
        coordinator.StateChanged += state =>
        {
            if (state == VoiceAssistantState.WaitingForReply)
                chat.InvalidateLastTurn();
            lock (notificationsGate)
                notifications.Add(state);
        };
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, invalidate while publishing");
        await WaitUntilAsync(() => chat.Requests.Count == 1);
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        VoiceAssistantState[] captured;
        lock (notificationsGate)
            captured = notifications.ToArray();
        Assert.True(
            Array.IndexOf(captured, VoiceAssistantState.WaitingForReply) <
            Array.IndexOf(captured, VoiceAssistantState.Unavailable));
    }

    [Fact]
    public async Task MatchingFinal_IsClaimedAndDuplicateIsSuppressedWithoutReplay()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, answer once");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);
        var notification = chat.CreateMatchingNotification("the answer");

        Assert.True(coordinator.TryClaimResponse(notification));
        Assert.True(coordinator.TryClaimResponse(notification));
        await WaitUntilAsync(() => speaker.Spoken.Count == 1);

        Assert.Equal(new[] { "the answer" }, speaker.Spoken);
    }

    [Fact]
    public async Task TrackedFinalWithoutGatewayMetadata_IsClaimedAndRestartsListening()
    {
        var input = new FakeInput();
        var chat = new FakeChat { AllowMissingMetadataFallback = true };
        var speaker = new FakeSpeaker { BlockUntilReleased = true };
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, answer without metadata");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);
        var notification = chat.CreateMatchingNotification("fallback answer");
        notification.OpenClawId = null;
        notification.OpenClawSeq = null;

        Assert.True(coordinator.TryClaimResponse(notification));
        Assert.False(coordinator.TryClaimResponse(new OpenClawNotification
        {
            IsChat = true,
            SessionKey = "other",
            Message = "fallback answer",
            FullMessage = "fallback answer"
        }));
        Assert.True(coordinator.TryClaimResponse(notification));
        speaker.Release();
        await WaitUntilAsync(() =>
            speaker.Spoken.Count == 1 &&
            coordinator.State == VoiceAssistantState.WakeListening);

        Assert.True(input.Starts >= 2);
        Assert.Equal(new[] { "fallback answer" }, speaker.Spoken);
    }

    [Fact]
    public async Task SeparateMetadataFreeTurns_WithSameReply_AreBothSpoken()
    {
        var input = new FakeInput();
        var chat = new FakeChat { AllowMissingMetadataFallback = true };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, first request");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);
        var first = chat.CreateMatchingNotification("Okay");
        first.OpenClawId = null;
        first.OpenClawSeq = null;
        Assert.True(coordinator.TryClaimResponse(first));
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        input.Emit("OpenClaw, second request");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);
        var second = chat.CreateMatchingNotification("Okay");
        second.OpenClawId = null;
        second.OpenClawSeq = null;
        Assert.True(coordinator.TryClaimResponse(second));
        await WaitUntilAsync(() => speaker.Spoken.Count == 2);

        Assert.Equal(new[] { "Okay", "Okay" }, speaker.Spoken);
    }

    [Fact]
    public async Task MetadataFreeReply_AfterTrackedTurnCompletes_IsNotClaimed()
    {
        var input = new FakeInput();
        var chat = new FakeChat { AllowMissingMetadataFallback = true };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();

        input.Emit("OpenClaw, tracked request");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);
        var notification = chat.CreateMatchingNotification("Okay");
        notification.OpenClawId = null;
        notification.OpenClawSeq = null;
        Assert.True(coordinator.TryClaimResponse(notification));
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        Assert.False(coordinator.TryClaimResponse(notification));
        Assert.Equal(new[] { "Okay" }, speaker.Spoken);
    }

    [Fact]
    public async Task UnrelatedOrUncorrelatedFinal_IsNotClaimed()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, expected request");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        Assert.False(coordinator.TryClaimResponse(new OpenClawNotification
        {
            IsChat = true,
            SessionKey = "main",
            OpenClawId = "unrelated",
            OpenClawSeq = 12,
            Message = "wrong"
        }));
        Assert.False(coordinator.TryClaimResponse(new OpenClawNotification
        {
            IsChat = true,
            SessionKey = "main",
            Message = "missing identity"
        }));
        Assert.Empty(speaker.Spoken);
    }

    [Fact]
    public async Task Timeout_DropsTurnAndRestartsListening()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(
            input,
            chat,
            speaker,
            timeout: TimeSpan.FromMilliseconds(20));
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, never answered");

        await WaitUntilAsync(() => input.Starts >= 2);

        Assert.Equal(VoiceAssistantState.WakeListening, coordinator.State);
        Assert.Empty(speaker.Spoken);
    }

    [Fact]
    public async Task ModeOffDuringSpeaking_CancelsPlaybackAndStopsListening()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker { BlockUntilCanceled = true };
        var configuration = new VoiceAssistantConfiguration(true, true, "OpenClaw");
        await using var coordinator = Create(
            input,
            chat,
            speaker,
            configuration: () => configuration);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, stop speaking");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        Assert.True(coordinator.TryClaimResponse(chat.CreateMatchingNotification("long answer")));
        await WaitUntilAsync(() => speaker.Started);
        configuration = configuration with { Enabled = false };
        await coordinator.ReconcileAsync();

        await WaitUntilAsync(() => speaker.Canceled);
        Assert.Equal(VoiceAssistantState.Off, coordinator.State);
        Assert.Equal(1, chat.Canceled);
    }

    [Fact]
    public async Task DisconnectDuringWaiting_CancelsTurnAndPauses()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, wait for disconnect");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        chat.ReadySessionKey = null;
        await coordinator.ReconcileAsync();

        Assert.Equal(VoiceAssistantState.Unavailable, coordinator.State);
        Assert.Equal(1, chat.Canceled);
    }

    [Fact]
    public async Task OwnRunBusyTransition_DoesNotCancelWaitingTurn()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, keep waiting");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        chat.CanSendDirectly = false;
        chat.ActiveRunId = chat.LastReceipt!.GatewayRunId;
        chat.NotifyAvailabilityChanged();
        await Task.Delay(30);

        Assert.Equal(VoiceAssistantState.WaitingForReply, coordinator.State);
        Assert.Equal(0, chat.Canceled);
    }

    [Fact]
    public async Task ExactTurnInvalidation_RecoversWithoutSecondCancel()
    {
        var input = new FakeInput();
        var chat = new FakeChat();
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);
        await coordinator.ReconcileAsync();
        input.Emit("OpenClaw, stop this turn");
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WaitingForReply);

        chat.InvalidateLastTurn();
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);

        Assert.Equal(0, chat.Canceled);
    }

    [Fact]
    public async Task StartupReadinessArrival_StartsWakeListeningWithoutSettingsToggle()
    {
        var input = new FakeInput();
        var chat = new FakeChat { ReadySessionKey = null };
        var speaker = new FakeSpeaker();
        await using var coordinator = Create(input, chat, speaker);

        await coordinator.ReconcileAsync();
        Assert.Equal(VoiceAssistantState.Unavailable, coordinator.State);

        chat.SetReadySessionKey("main");

        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);
        Assert.Equal(1, input.Starts);

        chat.SetReadySessionKey(null);
        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.Unavailable);
        chat.SetReadySessionKey("main");

        await WaitUntilAsync(() => coordinator.State == VoiceAssistantState.WakeListening);
        Assert.Equal(2, input.Starts);
    }

    private static VoiceAssistantCoordinator Create(
        FakeInput input,
        FakeChat chat,
        FakeSpeaker speaker,
        TimeSpan? timeout = null,
        Func<VoiceAssistantConfiguration>? configuration = null) =>
        new(
            input,
            chat,
            speaker,
            configuration ?? (() => new VoiceAssistantConfiguration(
                Enabled: true,
                LocalPrerequisitesReady: true,
                WakePhrase: "OpenClaw")),
            timeout);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class FakeInput : IVoiceAssistantInput
    {
        public event Action<string>? UtteranceCompleted;
        public event Action? CaptureAvailable;

        public int Starts { get; private set; }
        public int Stops { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }

        public void Emit(string transcript) => UtteranceCompleted?.Invoke(transcript);
        public void ReleaseCapture() => CaptureAvailable?.Invoke();
    }

    private sealed class FakeChat : IVoiceAssistantChatTurnClient
    {
        private VoiceAssistantTurnReceipt? _lastReceipt;
        private readonly HashSet<string> _invalidated = new(StringComparer.Ordinal);

        public VoiceAssistantSendDisposition Disposition { get; set; } = VoiceAssistantSendDisposition.Direct;
        public bool InvalidateBeforeSendReturns { get; set; }
        public bool AllowMissingMetadataFallback { get; set; }
        public string? BufferedResponse { get; set; }
        public string? ReadySessionKey { get; set; } = "main";
        public bool CanSendDirectly { get; set; } = true;
        public string? ActiveRunId { get; set; }
        public List<string> Requests { get; } = new();
        public int Canceled { get; private set; }
        public VoiceAssistantTurnReceipt? LastReceipt => _lastReceipt;

        public event Action? ReadinessChanged;
        public event Action<VoiceAssistantTurnInvalidation>? TurnInvalidated;

        public string? GetReadySessionKey() => ReadySessionKey;
        public VoiceAssistantAvailability GetAvailability() =>
            new(
                IsUsable: ReadySessionKey is not null,
                SessionKey: ReadySessionKey,
                CanSendDirectly: ReadySessionKey is not null && CanSendDirectly,
                ActiveRunId: ActiveRunId);

        public void SetReadySessionKey(string? sessionKey)
        {
            ReadySessionKey = sessionKey;
            ReadinessChanged?.Invoke();
        }

        public void NotifyAvailabilityChanged() => ReadinessChanged?.Invoke();

        public Task<VoiceAssistantTurnReceipt> SendAsync(
            string sessionKey,
            string request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            _lastReceipt = new VoiceAssistantTurnReceipt(
                Disposition,
                sessionKey,
                $"message-{Requests.Count}",
                $"run-{Requests.Count}",
                PreSendSequence: 10);
            if (InvalidateBeforeSendReturns)
            {
                _invalidated.Add(_lastReceipt.LocalMessageId);
                TurnInvalidated?.Invoke(new VoiceAssistantTurnInvalidation(
                    _lastReceipt.SessionKey,
                    _lastReceipt.GatewayRunId!,
                    _lastReceipt.LocalMessageId));
            }
            return Task.FromResult(_lastReceipt);
        }

        public Task CancelAsync(
            VoiceAssistantTurnReceipt receipt,
            CancellationToken cancellationToken)
        {
            Canceled++;
            return Task.CompletedTask;
        }

        public bool IsTurnInvalidated(VoiceAssistantTurnReceipt receipt) =>
            _invalidated.Contains(receipt.LocalMessageId);

        public bool TryTakeBufferedResponse(
            VoiceAssistantTurnReceipt receipt,
            out string responseText)
        {
            responseText = BufferedResponse ?? string.Empty;
            BufferedResponse = null;
            return responseText.Length > 0;
        }

        public bool IsResponseForTurn(
            VoiceAssistantTurnReceipt receipt,
            OpenClawNotification notification) =>
            notification.OpenClawId == $"reply-{receipt.GatewayRunId}" ||
            AllowMissingMetadataFallback &&
                notification.OpenClawId is null &&
                notification.OpenClawSeq is null;

        public OpenClawNotification CreateMatchingNotification(string message) =>
            new()
            {
                IsChat = true,
                SessionKey = _lastReceipt!.SessionKey,
                OpenClawId = $"reply-{_lastReceipt.GatewayRunId}",
                OpenClawSeq = 11,
                Message = message,
                FullMessage = message
            };

        public void InvalidateLastTurn()
        {
            var receipt = _lastReceipt!;
            _invalidated.Add(receipt.LocalMessageId);
            TurnInvalidated?.Invoke(new VoiceAssistantTurnInvalidation(
                receipt.SessionKey,
                receipt.GatewayRunId!,
                receipt.LocalMessageId));
        }
    }

    private sealed class FakeSpeaker : IVoiceAssistantSpeaker
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Spoken { get; } = new();
        public bool BlockUntilCanceled { get; set; }
        public bool BlockUntilReleased { get; set; }
        public bool Started { get; private set; }
        public bool Canceled { get; private set; }

        public async Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            Spoken.Add(text);
            Started = true;
            if (BlockUntilReleased)
            {
                await _release.Task.WaitAsync(cancellationToken);
                return;
            }
            if (!BlockUntilCanceled)
                return;

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled = true;
                throw;
            }
        }

        public void Release() => _release.TrySetResult(true);
    }
}
