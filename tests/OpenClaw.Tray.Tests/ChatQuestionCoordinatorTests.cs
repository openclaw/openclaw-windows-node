using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ChatQuestionCoordinatorTests
{
    [Fact]
    public void MalformedEvent_TriggersBoundedReadRecovery()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway, clock);
        owner.Start();
        gateway.ProtocolError();
        Assert.NotNull(owner.Snapshot().Error);
        gateway.List = () => Task.FromResult(new GatewayQuestionList(true, [Request()]));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(owner.Snapshot().Error);
        Assert.Equal("request-1", Assert.Single(owner.Snapshot().Prompts).Request.Id);
        Assert.Equal(0, gateway.Resolutions);
    }

    [Fact]
    public void TombstoneCapacity_EvictsOldestResolution_NotReusedDictionarySlot()
    {
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        for (var i = 0; i < 260; i++)
            gateway.ResolveEvent($"request-{i}", "answered");
        gateway.Request(Request("request-257"));
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }

    [Fact]
    public void FailedRefresh_RetriesAtOneTwoFourSeconds_ThenLeavesManualRecovery()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var gateway = new Gateway { List = () =>
        {
            calls++;
            return Task.FromException<GatewayQuestionList>(new TimeoutException());
        }};
        using var owner = new ChatQuestionCoordinator(gateway, clock);
        owner.Start();
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, calls);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(4, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(4, calls);
        Assert.NotNull(owner.Snapshot().Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retry_IsCancelledOnDisconnectOrDispose(bool dispose)
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var gateway = new Gateway { List = () =>
        {
            calls++;
            return Task.FromException<GatewayQuestionList>(new TimeoutException());
        }};
        using var owner = new ChatQuestionCoordinator(gateway, clock);
        owner.Start();
        if (dispose) owner.Dispose();
        else gateway.Status(false);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Refresh_CoalescesConcurrentCalls_AndPublishesRecoveredSiblingsImmediately()
    {
        var slow = new TaskCompletionSource<GatewayQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request("slow"));
        gateway.Request(Request("answered"));
        gateway.Request(Request("missing"));
        gateway.Get = id => id switch
        {
            "slow" => slow.Task,
            "missing" => Task.FromException<GatewayQuestionRequest>(new GatewayQuestionUnavailableException()),
            _ => Task.FromResult(Request(id) with { Status = "answered" }),
        };
        var refreshing = owner.RefreshAsync();
        Assert.Same(refreshing, owner.RefreshAsync());
        Assert.False(refreshing.IsCompleted);
        Assert.Equal("answered", owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "answered").Request.Status);
        Assert.Equal("unavailable", owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "missing").Request.Status);
        slow.SetResult(Request("slow"));
        await refreshing;
        gateway.Request(Request("missing"));
        Assert.Equal("unavailable", owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "missing").Request.Status);
    }

    [Fact]
    public async Task FailedAnswer_AutoRefreshesStatus_WithoutResendingAnswer()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway { Resolve = (_, _) => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatQuestionCoordinator(gateway, clock);
        owner.Start();
        gateway.Request(Request());
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.True(Assert.Single(owner.Snapshot().Prompts).NeedsRefresh);
        gateway.List = () => Task.FromResult(new GatewayQuestionList(true, [Request() with { Status = "answered" }]));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
        Assert.Equal(1, gateway.Resolutions);
    }

    [Fact]
    public async Task IndividualRecoveryFailure_DoesNotHideSuccess_AndRetriesOnlyRecovery()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway, clock);
        owner.Start();
        gateway.Request(Request("retry"));
        gateway.Request(Request("answered"));
        gateway.Get = id => id == "retry"
            ? Task.FromException<GatewayQuestionRequest>(new TimeoutException())
            : Task.FromResult(Request(id) with { Status = "answered" });
        await owner.RefreshAsync();
        Assert.Equal("answered", owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "answered").Request.Status);
        Assert.True(owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "retry").NeedsRefresh);
        gateway.Get = id => Task.FromResult(Request(id) with { Status = "cancelled" });
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("cancelled", owner.Snapshot().Prompts.Single(prompt => prompt.Request.Id == "retry").Request.Status);
        Assert.Equal(0, gateway.Resolutions);
    }

    [Fact]
    public async Task UnavailableSubmitReply_DoesNotReplaceKnownLiveAnswer()
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Resolve = (_, _) => reply.Task };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var submitting = owner.SubmitAsync("session-a", "main", "request-1", null);
        gateway.ResolveEvent("request-1", "answered");
        reply.SetException(new GatewayQuestionUnavailableException());
        await submitting;
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }

    [Fact]
    public async Task LateUnavailableRecovery_CannotOverwriteLiveResolution()
    {
        var reply = new TaskCompletionSource<GatewayQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Get = _ => reply.Task };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var refreshing = owner.RefreshAsync();
        gateway.ResolveEvent("request-1", "answered");
        reply.SetException(new GatewayQuestionUnavailableException());
        await refreshing;
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }
    private static GatewayQuestionRequest Request(string id = "request-1", string session = "session-a") =>
        new(id, session, "main", "run", 1000, DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            "pending", [new("choice", "Platform", "Choose", [new("Windows", "win", null), new("Linux", "linux", null)],
                false, false, false, false, false, [], null, false)]);

    [Fact]
    public async Task PendingQuestion_AnswersByRpcAndWaitsForAcknowledgement()
    {
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Resolve = (_, _) => reply.Task;
        var sending = owner.SubmitAsync("session-a", "main", "request-1", new Dictionary<string, string[]> { ["choice"] = ["win"] });
        Assert.True(Assert.Single(owner.Snapshot().Prompts).Submitting);
        Assert.Equal("pending", Assert.Single(owner.Snapshot().Prompts).Request.Status);
        reply.SetResult("answered");
        await sending;
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
        Assert.Equal("win", gateway.Sent!["choice"][0]);
    }

    [Fact]
    public async Task SubmissionFailure_KeepsPromptAndShowsRecoverableError()
    {
        var gateway = new Gateway { Resolve = (_, _) => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        var prompt = Assert.Single(owner.Snapshot().Prompts);
        Assert.Equal("pending", prompt.Request.Status);
        Assert.False(prompt.Submitting);
        Assert.NotNull(prompt.Error);
    }

    [Fact]
    public async Task WrongConversationOrAgent_CannotRespond()
    {
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SubmitAsync("session-b", "main", "request-1", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SubmitAsync("session-a", "other", "request-1", null));
        Assert.Equal(0, gateway.Resolutions);
    }

    [Fact]
    public async Task DuplicateClick_SendsExactlyOnce()
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Resolve = (_, _) => reply.Task };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var sending = owner.SubmitAsync("session-a", "main", "request-1", null);
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.Equal(1, gateway.Resolutions);
        reply.SetResult("cancelled");
        await sending;
    }

    [Fact]
    public async Task DuplicateRequestAndRefresh_DoNotReleaseSubmissionFenceOrMutateAnswers()
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Resolve = (_, _) => response.Task };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var answers = new Dictionary<string, string[]> { ["choice"] = ["win"] };
        var sending = owner.SubmitAsync("session-a", "main", "request-1", answers);
        answers["choice"][0] = "linux";
        gateway.Request(Request());
        gateway.List = () => Task.FromResult(new GatewayQuestionList(true, [Request()]));
        await owner.RefreshAsync();
        Assert.True(Assert.Single(owner.Snapshot().Prompts).Submitting);
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.Equal(1, gateway.Resolutions);
        Assert.Equal("win", gateway.Sent!["choice"][0]);
        response.SetResult("answered");
        await sending;
    }

    [Fact]
    public async Task ConcurrentLiveEvent_DoesNotDiscardUnrelatedRecoveredQuestion()
    {
        var response = new TaskCompletionSource<GatewayQuestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.List = () => response.Task;
        var refreshing = owner.RefreshAsync();
        gateway.Request(Request("live"));
        response.SetResult(new(true, [Request("recovered") with { CreatedAtMs = 2000 }]));
        await refreshing;
        Assert.Equal(new[] { "live", "recovered" }, owner.Snapshot().Prompts.Select(prompt => prompt.Request.Id));
    }

    [Fact]
    public async Task ReconnectAndUnconfirmedReply_RequireAuthoritativeRefreshBeforeRetry()
    {
        var response = new TaskCompletionSource<GatewayQuestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        gateway.Status(false);
        gateway.List = () => response.Task;
        gateway.Status(true);
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.Equal(0, gateway.Resolutions);
        Assert.True(Assert.Single(owner.Snapshot().Prompts).NeedsRefresh);
        response.SetResult(new(true, [Request()]));
        await owner.RefreshAsync();
        gateway.Resolve = (_, _) => Task.FromException<string>(new TimeoutException());
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.Equal(1, gateway.Resolutions);
        Assert.True(Assert.Single(owner.Snapshot().Prompts).NeedsRefresh);
    }

    [Fact]
    public async Task RecoveredTerminalRecord_CannotBeResurrectedByRequestedEvent()
    {
        var gateway = new Gateway { List = () => Task.FromResult(new GatewayQuestionList(true,
            [Request() with { Status = "answered" }])) };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        await owner.RefreshAsync();
        gateway.Request(Request());
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }

    [Fact]
    public void ResolvedBeforeRequested_DoesNotResurrectPrompt()
    {
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.ResolveEvent("request-1", "answered");
        gateway.Request(Request());
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }

    [Fact]
    public async Task Reconnect_HydratesPendingQuestions_AndRejectsOldReply()
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Resolve = (_, _) => response.Task };
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        var sending = owner.SubmitAsync("session-a", "main", "request-1", null);
        gateway.Status(false);
        gateway.List = () => Task.FromResult(new GatewayQuestionList(true, [Request()]));
        gateway.Status(true);
        response.SetResult("cancelled");
        await sending;
        Assert.Equal("pending", Assert.Single(owner.Snapshot().Prompts).Request.Status);
        Assert.False(Assert.Single(owner.Snapshot().Prompts).Submitting);
    }

    [Fact]
    public async Task LateList_CannotOverwriteLiveResolution()
    {
        var response = new TaskCompletionSource<GatewayQuestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request());
        gateway.List = () => response.Task;
        var refreshing = owner.RefreshAsync();
        gateway.ResolveEvent("request-1", "answered");
        response.SetResult(new(true, [Request()]));
        await refreshing;
        Assert.Equal("answered", Assert.Single(owner.Snapshot().Prompts).Request.Status);
    }

    [Fact]
    public async Task ExpiredRequest_DisablesSubmission()
    {
        var gateway = new Gateway();
        using var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.Request(Request() with { ExpiresAtMs = 2000 });
        Assert.Equal("expired", Assert.Single(owner.Snapshot().Prompts).Request.Status);
        await owner.SubmitAsync("session-a", "main", "request-1", null);
        Assert.Equal(0, gateway.Resolutions);
    }

    [Fact]
    public async Task Disposal_FencesInFlightRefreshAndUnsubscribes()
    {
        var response = new TaskCompletionSource<GatewayQuestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway();
        var owner = new ChatQuestionCoordinator(gateway);
        owner.Start();
        gateway.List = () => response.Task;
        var refreshing = owner.RefreshAsync();
        owner.Dispose();
        response.SetResult(new(true, [Request()]));
        await refreshing;
        gateway.Request(Request());
        Assert.Empty(owner.Snapshot().Prompts);
    }

    [Fact]
    public void Draft_UsesWireValuesAndValidatesAllQuestions()
    {
        var request = Request();
        var draft = new ChatQuestionDraft();
        draft.Selected.Add("win");
        var answers = draft.Answers(request.Questions[0]);
        Assert.Equal("win", Assert.Single(answers));
        ChatQuestionDraft.Validate(request, new Dictionary<string, string[]> { ["choice"] = answers });
        Assert.Throws<ArgumentException>(() => ChatQuestionDraft.Validate(request,
            new Dictionary<string, string[]> { ["choice"] = ["Windows"] }));
        Assert.Throws<ArgumentException>(() => ChatQuestionDraft.Validate(request,
            new Dictionary<string, string[]> { ["choice"] = ["win", "linux"] }));
    }

    [Fact]
    public void Draft_MultiselectFreeTextAndLines_PreserveOrder()
    {
        var question = Request().Questions[0] with { MultiSelect = true, AllowOther = true, AnswerAsLines = true };
        var draft = new ChatQuestionDraft { FreeText = "custom\nsecond" };
        draft.Selected.Add("linux");
        draft.Selected.Add("win");
        Assert.Equal(new[] { "win", "linux", "custom", "second" }, draft.Answers(question));
    }

    [Fact]
    public void SecretFreeText_PreservesValueWhitespace()
    {
        var question = Request().Questions[0] with { IsSecret = true, AllowOther = true };
        var draft = new ChatQuestionDraft { FreeText = " leading-and-trailing " };
        Assert.Equal(" leading-and-trailing ", Assert.Single(draft.Answers(question)));
    }

    private sealed class Gateway : IChatQuestionGateway
    {
        public bool IsConnected { get; private set; } = true;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<GatewayQuestionEvent>? QuestionReceived;
        public event EventHandler? QuestionProtocolError;
        internal Func<Task<GatewayQuestionList>> List = () => Task.FromResult(new GatewayQuestionList(true, []));
        internal Func<string, IReadOnlyDictionary<string, string[]>?, Task<string>> Resolve =
            (_, answers) => Task.FromResult(answers is null ? "cancelled" : "answered");
        internal Func<string, Task<GatewayQuestionRequest>> Get = id =>
            Task.FromResult(ChatQuestionCoordinatorTests.Request(id) with { Status = "expired" });
        internal int Resolutions;
        internal IReadOnlyDictionary<string, string[]>? Sent;
        public Task<GatewayQuestionList> ListQuestionsAsync() => List();
        public Task<GatewayQuestionRequest> GetQuestionAsync(string id) => Get(id);
        public Task<string> ResolveQuestionAsync(string id, IReadOnlyDictionary<string, string[]>? answers)
        {
            Resolutions++;
            Sent = answers;
            return Resolve(id, answers);
        }
        internal void Request(GatewayQuestionRequest request) =>
            QuestionReceived?.Invoke(this, new(request.Id, request.Status, request));
        internal void ResolveEvent(string id, string status) => QuestionReceived?.Invoke(this, new(id, status));
        internal void ProtocolError() => QuestionProtocolError?.Invoke(this, EventArgs.Empty);
        internal void Status(bool connected)
        {
            IsConnected = connected;
            StatusChanged?.Invoke(this, connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected);
        }
    }
}
