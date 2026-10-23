using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class ModelSignInSessionTests
{
    private const string Status = """
        {"providers":[{"provider":"fixture","displayName":"Fixture","status":"missing"}],
         "providerCapabilities":[{"loginOptions":[{"id":"fixture-login","label":"Fixture login"}]},
         {"loginOptions":[{"id":"fixture-login","label":"Duplicate"}]}]}
        """;
    private const string Question = """
        {"done":false,"status":"running","step":{"id":"question-1","type":"text","sensitive":true,"executor":"client"}}
        """;
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static TaskCompletionSource<JsonElement> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ReadAndLogin_SendPinnedAgentChoiceAndClientGeneratedId()
    {
        var transport = new Transport();
        var owner = new ModelSignInSession(transport, "research");
        await owner.RefreshAsync();
        Assert.Single(owner.AuthStatus!.LoginOptions);
        Assert.Equal("""{"agentId":"research"}""", transport.Calls[0].Parameters.GetRawText());
        await owner.StartAsync("fixture-login");
        var call = transport.Calls[1];
        Assert.Equal("models.authLogin", call.Method);
        Assert.Equal(["sessionId", "agentId", "authChoice"], call.Parameters.EnumerateObject().Select(p => p.Name));
        Assert.True(Guid.TryParse(call.Parameters.GetProperty("sessionId").GetString(), out _));
        Assert.Equal("research", call.Parameters.GetProperty("agentId").GetString());
        Assert.Equal("fixture-login", call.Parameters.GetProperty("authChoice").GetString());
        Assert.Equal(26 * 60 * 1000, call.Timeout);
        Assert.Equal("question-1", owner.Step!.Id);
        Assert.False(owner.Busy);
        Assert.False(owner.CanLogin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync("fixture-login"));
    }

    [Theory]
    [InlineData("models.authLogin")]
    [InlineData("wizard.next")]
    [InlineData("wizard.cancel")]
    public async Task MissingMethodOrAdmin_BlocksAdmission(string missing)
    {
        var transport = new Transport { Unsupported = missing };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        Assert.False(owner.CanLogin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync("fixture-login"));
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task MissingRead_DoesNotDispatchOrPretendStatusIsEmpty()
    {
        var transport = new Transport { Unsupported = "models.authStatus" };
        var owner = new ModelSignInSession(transport, "main");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(owner.RefreshAsync);
        Assert.Null(owner.AuthStatus);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task UnofferedChoice_DoesNotDispatchMutation()
    {
        var transport = new Transport();
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync("arbitrary-provider"));
        Assert.Single(transport.Calls);
    }

    [Theory]
    [InlineData("done", ModelSignInNotice.Finished)]
    [InlineData("error", ModelSignInNotice.Ended)]
    [InlineData("cancelled", ModelSignInNotice.Ended)]
    public async Task TerminalResponses_ReReadAccountEvenAfterErrorOrCancellation(string status, ModelSignInNotice notice)
    {
        var transport = new Transport();
        var owner = new ModelSignInSession(transport, "main");
        var changed = 0;
        owner.AuthChanged += (_, _) => changed++;
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        transport.Next = Json($$"""{"done":true,"status":"{{status}}","error":"synthetic saved-then-error"}""");
        await owner.AnswerAsync(Json("\"synthetic-answer\""));
        Assert.Null(owner.SessionId);
        Assert.Null(owner.Step);
        Assert.Equal(notice, owner.Notice);
        Assert.Equal(2, transport.Calls.Count(call => call.Method == "models.authStatus"));
        var answer = Assert.Single(transport.Calls, call => call.Method == "wizard.next").Parameters;
        Assert.Equal("question-1", answer.GetProperty("answer").GetProperty("stepId").GetString());
        Assert.Equal("synthetic-answer", answer.GetProperty("answer").GetProperty("value").GetString());
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task LostStartReply_RetainsIdAndPreventsReplayUntilExplicitCancel()
    {
        var transport = new Transport { StartFailure = new TimeoutException() };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => owner.StartAsync("fixture-login"));
        var id = owner.SessionId;
        Assert.NotNull(id);
        Assert.Equal(ModelSignInNotice.Uncertain, owner.Notice);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RefreshAsync);
        await owner.CancelAsync();
        var cancel = Assert.Single(transport.Calls, c => c.Method == "wizard.cancel");
        Assert.Equal(id, cancel.Parameters.GetProperty("sessionId").GetString());
        Assert.True(cancel.Parameters.GetProperty("closeInput").GetBoolean());
        Assert.Null(owner.SessionId);
        Assert.Equal(ModelSignInNotice.Ended, owner.Notice);
    }

    [Fact]
    public async Task CloseDuringAdmission_RetainsIdAndCancelsAgainAfterLateStart()
    {
        var held = Signal();
        var transport = new Transport { HeldStart = held.Task };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        var start = owner.StartAsync("fixture-login");
        var id = owner.SessionId;
        await owner.CloseAsync();
        Assert.Equal(id, owner.SessionId);
        held.SetResult(Json($$$"""{"sessionId":"{{{id}}}","done":false,"status":"running","step":{"id":"late","type":"note"}}"""));
        await start;
        Assert.Equal(2, transport.Calls.Count(call => call.Method == "wizard.cancel"));
        Assert.All(transport.Calls.Where(c => c.Method == "wizard.cancel"), call =>
            Assert.Equal(id, call.Parameters.GetProperty("sessionId").GetString()));
        Assert.Null(owner.SessionId);
        Assert.Null(owner.AuthStatus);
        Assert.Null(owner.Step);
    }

    [Fact]
    public async Task ContextReplacement_RetiresViewAndNeverCancelsOnReplacementConnection()
    {
        var held = Signal();
        var transport = new Transport { HeldStart = held.Task };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        var start = owner.StartAsync("fixture-login");
        var id = owner.SessionId;
        transport.Current = false;
        transport.ConnectionCurrent = false;
        await owner.RetireIfStaleAsync();
        held.SetResult(Json($$$"""{"sessionId":"{{{id}}}","done":false,"status":"running","step":{"id":"stale","type":"text"}}"""));
        await start;
        Assert.DoesNotContain(transport.Calls, c => c.Method == "wizard.cancel");
        Assert.Null(owner.Step);
        Assert.Null(owner.AuthStatus);
        Assert.Equal(ModelSignInNotice.ContextChanged, owner.Notice);
        Assert.Equal(id, owner.SessionId);
        Assert.False(owner.CanLogin);
    }

    [Fact]
    public async Task OldStatusReply_DoesNotReplaceNewConversationState()
    {
        var held = Signal();
        var transport = new Transport { HeldStatus = held.Task };
        var owner = new ModelSignInSession(transport, "main");
        var read = owner.RefreshAsync();
        transport.Current = false;
        held.SetResult(Json(Status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => read);
        Assert.Null(owner.AuthStatus);
    }

    [Fact]
    public async Task RunningCancelAcknowledgement_DoesNotClaimCancellationOrAllowRestart()
    {
        var transport = new Transport { Cancel = Json("""{"status":"running"}""") };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        await owner.CancelAsync();
        Assert.NotNull(owner.SessionId);
        Assert.False(owner.CanLogin);
        Assert.Equal(ModelSignInNotice.CancellationUnconfirmed, owner.Notice);
        Assert.Single(transport.Calls, c => c.Method == "models.authStatus");
    }

    [Fact]
    public async Task GatewayExecutor_PollsWithoutAnswerAndNeverSubmitsStaleStep()
    {
        var transport = new Transport
        {
            StartStep = Json("""{"done":false,"status":"running","step":{"id":"progress","type":"progress","executor":"gateway"}}""")
        };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        Assert.Equal("question-1", owner.Step!.Id);
        var next = Assert.Single(transport.Calls, c => c.Method == "wizard.next");
        Assert.False(next.Parameters.TryGetProperty("answer", out _));
    }

    [Fact]
    public async Task RejectedLogin_ClearsOnlyDefinitePreAdmissionFailure()
    {
        var transport = new Transport { StartFailure = new GatewayModelSignInException("INVALID_REQUEST", null, false, null) };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await Assert.ThrowsAsync<GatewayModelSignInException>(() => owner.StartAsync("fixture-login"));
        Assert.Null(owner.SessionId);
        Assert.Equal("INVALID_REQUEST", owner.Failure!.Code);
    }

    [Fact]
    public async Task InvalidAnswerAndMismatchedSession_FailClosed()
    {
        var transport = new Transport();
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        await Assert.ThrowsAsync<ArgumentException>(() => owner.AnswerAsync(Json("true")));
        Assert.DoesNotContain(transport.Calls, c => c.Method == "wizard.next");
        await owner.CancelAsync();
        transport.HeldStart = Task.FromResult(Json("""{"sessionId":"wrong","done":true,"status":"done"}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => owner.StartAsync("fixture-login"));
        Assert.NotNull(owner.SessionId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"providers":null}""")]
    [InlineData("""{"providers":[null]}""")]
    [InlineData("""{"providers":[],"providerCapabilities":[null]}""")]
    [InlineData("""{"providers":[],"providerCapabilities":[{"loginOptions":[null]}]}""")]
    [InlineData("""{"providers":[],"providerCapabilities":[{"loginOptions":[{"label":"Missing ID"}]}]}""")]
    public async Task MalformedStatus_FailsExplicitlyAndNeverAdmits(string payload)
    {
        var owner = new ModelSignInSession(new Transport { HeldStatus = Task.FromResult(Json(payload)) }, "main");
        var error = await Record.ExceptionAsync(owner.RefreshAsync);
        Assert.True(error is JsonException or InvalidDataException, error?.ToString());
        Assert.Null(owner.AuthStatus);
        Assert.False(owner.CanLogin);
    }

    [Fact]
    public async Task OptionalWizardStatus_AdmitsQuestionButDoesNotClaimTerminalSuccess()
    {
        var transport = new Transport
        {
            StartStep = Json("""{"done":false,"step":{"id":"question-1","type":"text"}}"""),
            Next = Json("""{"done":true}""")
        };
        var owner = new ModelSignInSession(transport, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        Assert.Equal("question-1", owner.Step!.Id);
        await owner.AnswerAsync(Json("\"synthetic\""));
        Assert.Null(owner.SessionId);
        Assert.Equal(ModelSignInNotice.Ended, owner.Notice);
        Assert.Equal(2, transport.Calls.Count(call => call.Method == "models.authStatus"));
    }

    [Theory]
    [InlineData("""{"done":false,"step":{"id":"question-1","type":"select","options":[null]}}""")]
    [InlineData("""{"done":false,"step":{"id":"question-1","type":"text","executor":"other"}}""")]
    [InlineData("""{"step":{"id":"question-1","type":"text"}}""")]
    public async Task MalformedWizard_RetainsUncertainIdWithoutRenderingQuestion(string payload)
    {
        var owner = new ModelSignInSession(new Transport { StartStep = Json(payload) }, "main");
        await owner.RefreshAsync();
        var error = await Record.ExceptionAsync(() => owner.StartAsync("fixture-login"));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString());
        Assert.NotNull(owner.SessionId);
        Assert.Null(owner.Step);
        Assert.Equal(ModelSignInNotice.Uncertain, owner.Notice);
    }

    [Fact]
    public async Task SensitiveInitialValue_IsNotRetainedByQuestionOwner()
    {
        var owner = new ModelSignInSession(new Transport
        {
            StartStep = Json("""{"done":false,"step":{"id":"question-1","type":"text","sensitive":true,"initialValue":"synthetic-secret"}}""")
        }, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        Assert.Null(owner.Step!.InitialValue);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status":"running"}""")]
    public async Task MissingOrRunningCancelAck_DoesNotConfirmCancellation(string payload)
    {
        var owner = new ModelSignInSession(new Transport { Cancel = Json(payload) }, "main");
        await owner.RefreshAsync();
        await owner.StartAsync("fixture-login");
        var error = await Record.ExceptionAsync(owner.CancelAsync);
        Assert.True(error is null or JsonException);
        Assert.NotNull(owner.SessionId);
        Assert.Equal(ModelSignInNotice.CancellationUnconfirmed, owner.Notice);
        Assert.False(owner.CanLogin);
    }

    private sealed class Transport : IModelSignInTransport
    {
        public bool Current = true;
        public bool ConnectionCurrent = true;
        public string? Unsupported;
        public Exception? StartFailure;
        public Task<JsonElement>? HeldStart;
        public Task<JsonElement>? HeldStatus;
        public JsonElement StartStep = Json(Question);
        public JsonElement Next = Json(Question);
        public JsonElement Cancel = Json("""{"status":"cancelled"}""");
        public List<(string Method, JsonElement Parameters, int Timeout)> Calls { get; } = [];
        public bool IsCurrent => Current;
        public bool IsConnectionCurrent => ConnectionCurrent;
        public bool Supports(string method, string scope) => method != Unsupported;
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs)
        {
            var payload = Assert.IsType<JsonElement>(parameters);
            Calls.Add((method, payload, timeoutMs));
            return method switch
            {
                "models.authStatus" => HeldStatus ?? Task.FromResult(Json(Status)),
                "models.authLogin" => StartFailure is not null ? Task.FromException<JsonElement>(StartFailure) :
                    HeldStart ?? Task.FromResult(Json(StartStep.GetRawText().Insert(1,
                        $"\"sessionId\":\"{payload.GetProperty("sessionId").GetString()}\","))),
                "wizard.next" => Task.FromResult(Next),
                "wizard.cancel" => Task.FromResult(Cancel),
                _ => throw new InvalidOperationException("Unexpected synthetic method.")
            };
        }
    }
}
