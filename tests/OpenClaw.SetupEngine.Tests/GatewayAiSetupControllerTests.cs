using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayAiSetupControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPreparation_ChainsOnlyExactReceiptAndRetainedConsent(bool consent)
    {
        var (client, controller, transport) = await CreateAsync(requireConsent: true);
        client.SelectPrepareOption("local/setup");
        transport.StartReply = (method, parameters) => method == "openclaw.setup.prepare.start"
            ? Json(new { sessionId = Session(parameters), done = true, status = "done", preparedModelRef = "local/exact" })
            : Json(new { sessionId = Session(parameters), done = false, status = "running",
                step = new { id = "promotion", type = "confirm", executor = "client", initialValue = false } });
        await controller.StartSelectedAsync(null, consent);
        Assert.Equal(["openclaw.setup.detect", "openclaw.setup.prepare.start", "openclaw.setup.activate.start"],
            transport.Calls.Select(call => call.Method));
        var activation = transport.Calls.Last().Parameters;
        Assert.Equal("provider-auto:local%2Fsetup", activation.GetProperty("kind").GetString());
        Assert.Equal("local/exact", activation.GetProperty("modelRef").GetString());
        Assert.Equal(consent, activation.GetProperty("nativeSessionCatalogsEnabled").GetBoolean());
        Assert.NotEqual(Session(transport.Calls[1].Parameters), Session(activation));
        Assert.Equal(GatewayAiSetupPhase.Running, client.Phase);
        Assert.Equal("promotion", client.Wizard!.Step!.Id);
        Assert.DoesNotContain(transport.Calls, call => call.Method == "wizard.next");
    }

    [Fact]
    public async Task PreparedAfterReconnect_IsRetainedWithoutActivationOrReplay()
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectPrepareOption("local/setup");
        transport.StartReply = (_, parameters) =>
        {
            transport.Generation++;
            return Json(new { sessionId = Session(parameters), done = true, status = "done", preparedModelRef = "local/exact" });
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartSelectedAsync(null, null));
        Assert.DoesNotContain(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
        // The client rejects the stale reply before it can become an authoritative receipt.
        Assert.Equal(GatewayAiSetupPhase.Uncertain, client.Phase);
        Assert.NotNull(client.SessionId);
        Assert.Null(controller.TakeAutomaticAuthUri());
    }

    [Fact]
    public async Task LostStartReply_IsNeverAutomaticallyReplayed()
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectAuthOption("login");
        transport.StartReply = (_, _) => throw new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => controller.StartSelectedAsync(null, null));
        await controller.WaitForInputAsync();
        Assert.Equal(2, transport.Calls.Count);
        Assert.Equal(GatewayAiSetupPhase.Uncertain, client.Phase);
        Assert.NotNull(client.SessionId);
        Assert.Null(controller.TakeAutomaticAuthUri());
    }

    [Fact]
    public async Task ReconciledPreparedReceipt_RequiresOneExplicitActivationWithoutPreparationReplay()
    {
        var (client, controller, transport) = await CreateAsync(requireConsent: true);
        client.SelectPrepareOption("local/setup");
        transport.StartReply = (_, _) => throw new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => controller.StartSelectedAsync(null, false));
        transport.Polls.Enqueue(Json(new { done = true, status = "done", preparedModelRef = "local/exact" }));
        await client.RefreshAsync();
        await controller.WaitForInputAsync();
        Assert.Equal(GatewayAiSetupPhase.Prepared, client.Phase);
        Assert.Equal(1, transport.Calls.Count(call => call.Method == "openclaw.setup.prepare.start"));
        Assert.DoesNotContain(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
        transport.StartReply = (_, parameters) => Json(new
        {
            sessionId = Session(parameters), done = false,
            step = new { id = "essential-promotion", type = "confirm", executor = "client" },
        });
        await controller.ActivatePreparedExplicitlyAsync();
        Assert.Equal(1, transport.Calls.Count(call => call.Method == "openclaw.setup.activate.start"));
        Assert.Equal("local/exact", transport.Calls.Last().Parameters.GetProperty("modelRef").GetString());
        Assert.False(transport.Calls.Last().Parameters.GetProperty("nativeSessionCatalogsEnabled").GetBoolean());
        Assert.Equal("essential-promotion", client.Wizard!.Step!.Id);
    }

    [Fact]
    public async Task PreparedFallback_CannotBypassChangedGatewayOrGeneration()
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectPrepareOption("local/setup");
        transport.StartReply = (_, _) => throw new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => controller.StartSelectedAsync(null, null));
        transport.Polls.Enqueue(Json(new { done = true, status = "done", preparedModelRef = "local/exact" }));
        await client.RefreshAsync();
        transport.Generation++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActivatePreparedExplicitlyAsync());
        Assert.Equal(GatewayAiSetupPhase.Prepared, client.Phase);
        Assert.DoesNotContain(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
    }

    [Theory]
    [InlineData("https://login.example/device", 1)]
    [InlineData("http://login.example/device", 0)]
    [InlineData("https://user:password@login.example/device", 0)]
    public async Task DeviceCodeProgress_OpensSafeUrlOnceAndNeverSendsAnAnswer(string url, int expectedLaunches)
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectAuthOption("login");
        transport.StartReply = (_, parameters) => Progress(Session(parameters), url);
        transport.Polls.Enqueue(Progress(null, url));
        transport.Polls.Enqueue(Progress(null, url));
        transport.Polls.Enqueue(Json(new { done = false, status = "running",
            step = new { id = "required-note", type = "note", executor = "client", message = "Required acknowledgment" } }));
        var launches = new List<Uri>();
        void Changed()
        {
            if (controller.TakeAutomaticAuthUri() is { } uri) launches.Add(uri);
        }
        await controller.StartSelectedAsync(null, null, Changed);
        Assert.Equal(expectedLaunches, launches.Count);
        Assert.All(transport.Calls.Where(call => call.Method == "wizard.next"),
            call => Assert.False(call.Parameters.TryGetProperty("answer", out _)));
        Assert.Equal("required-note", client.Wizard!.Step!.Id);
        Assert.Null(controller.TakeAutomaticAuthUri());
        Assert.False(transport.Calls[1].Parameters.TryGetProperty("nativeSessionCatalogsEnabled", out _));
    }

    [Fact]
    public async Task UncertainReconciliationAndReconnect_DoNotOpenBrowser()
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectAuthOption("login");
        transport.StartReply = (_, parameters) => Json(new { sessionId = Session(parameters), done = false,
            step = new { id = "url", type = "note", externalUrl = "https://login.example/" } });
        await controller.StartSelectedAsync(null, null);
        transport.Generation++;
        Assert.Null(controller.TakeAutomaticAuthUri());
        controller.StopAutomaticContinuation();
        transport.Polls.Enqueue(Progress(null, "https://login.example/new"));
        await client.RefreshAsync();
        transport.Polls.Enqueue(Json(new { done = false, step = new { id = "required", type = "confirm" } }));
        await controller.WaitForInputAsync();
        Assert.Null(controller.TakeAutomaticAuthUri());
        Assert.Equal(1, transport.Calls.Count(call => call.Method == "openclaw.setup.auth.start"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedRestart_WaitsForFreshHandshakeOrStopsAtBound(bool reconnect)
    {
        var (client, controller, transport) = await CreateAsync();
        client.SelectCandidate("existing", "provider/exact");
        transport.StartReply = (_, parameters) => Json(new { sessionId = Session(parameters), done = true, status = "done",
            modelActivation = new { modelRef = "provider/exact", gatewayRestartRequired = true } });
        await controller.StartSelectedAsync(null, null);
        if (reconnect)
            transport.Clock.OnTick = () => transport.Generation++;
        if (reconnect)
            await controller.WaitForExpectedRestartAsync();
        else
            await Assert.ThrowsAsync<TimeoutException>(() => controller.WaitForExpectedRestartAsync());
        Assert.Equal(!reconnect, client.WaitingForRestart);
        Assert.Equal(GatewayAiSetupPhase.VerificationRequired, client.Phase);
        Assert.Equal(2, transport.Calls.Count);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("identity-file")]
    [InlineData("signing-device")]
    [InlineData("session")]
    [InlineData("agent")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task ExpectedRestart_ValidatesPersistedAuthorityDuringClearedHandshake(string change)
    {
        using var temp = new TempDirectory();
        var identity = new DeviceIdentity(temp.Path);
        identity.Initialize();
        var active = new GatewayRecord { Id = "gateway", Url = "wss://restart.example" };
        var binding = new SetupGatewaySessionBinding(active);
        var sessionKey = "agent:main:main";
        var signingId = identity.DeviceId;
        var transport = new Transport { Configured = true };
        transport.RouteProvider = () => binding.GetRoute(active, temp.Path,
            transport.IsConnected ? sessionKey : null, transport.IsConnected ? signingId : null);
        transport.RestartAuthority = expected => binding.RequirePersistedAuthority(active, temp.Path, expected);
        var client = new GatewayAiSetupClient(transport);
        var controller = new GatewayAiSetupController(client, transport.Clock);
        await client.DetectAsync();
        client.SelectCandidate("existing", "provider/exact");
        transport.StartReply = (_, parameters) => Json(new
        {
            sessionId = Session(parameters), done = true, status = "done",
            modelActivation = new { modelRef = "provider/exact", gatewayRestartRequired = true },
        });
        await controller.StartSelectedAsync(null, null);
        transport.IsConnected = false;
        Assert.Null(transport.Route.IdentityBinding);
        Assert.Equal("", transport.Route.SessionKey);
        Assert.NotEqual(client.Route, transport.Route);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.VerifyAsync());
        using var cancellation = new CancellationTokenSource();
        var ticks = 0;
        transport.Clock.OnTick = () =>
        {
            if (++ticks == 1)
            {
                if (change == "gateway") active = active with { Id = "other" };
                if (change == "endpoint") active = active with { Url = "wss://other.example" };
                if (change == "identity-file") File.Delete(Path.Combine(temp.Path, "device-key-ed25519.json"));
                if (change == "cancel") cancellation.Cancel();
            }
            if (ticks < 2 || change is "timeout" or "cancel") return;
            if (change == "signing-device") signingId = "different-signing-device";
            if (change == "session") sessionKey = "agent:main:other";
            if (change == "agent") sessionKey = "agent:other:main";
            transport.Generation++;
            transport.IsConnected = true;
        };
        var wait = controller.WaitForExpectedRestartAsync(ct: cancellation.Token);
        if (change == "same")
        {
            await wait;
            Assert.True(ticks >= 2);
            Assert.True((await client.VerifyAsync()).Ok);
            Assert.Equal(GatewayAiSetupPhase.Verified, client.Phase);
        }
        else if (change == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        else if (change == "timeout")
            await Assert.ThrowsAsync<TimeoutException>(() => wait);
        else
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => wait);
        Assert.Single(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
        if (change != "same")
            Assert.DoesNotContain(transport.Calls, call => call.Method == "openclaw.setup.verify");
    }

    [Theory]
    [InlineData("progress", "client", false, null)]
    [InlineData("action", "gateway", true, null)]
    [InlineData("note", "client", false, "Continue.Content")]
    [InlineData("action", "client", false, "Continue.Content")]
    [InlineData("confirm", "client", false, "Submit")]
    [InlineData("text", "client", true, "Submit")]
    [InlineData("note", "client", true, "SignedIn")]
    public void PromptActions_RespectActualExecutor(string type, string executor, bool code, string? expected)
    {
        Assert.Equal(expected, GatewayAiSetupPresentation.GetPromptAction(new()
        {
            Id = "step", Type = type, Executor = executor, DeviceCode = code ? new("SYNTHETIC") : null,
        }));
    }

    [Theory]
    [InlineData(GatewayAiSetupPhase.Choosing, true, false, false)]
    [InlineData(GatewayAiSetupPhase.Running, true, false, false)]
    [InlineData(GatewayAiSetupPhase.Prepared, true, false, false)]
    [InlineData(GatewayAiSetupPhase.VerificationRequired, true, false, false)]
    [InlineData(GatewayAiSetupPhase.Uncertain, true, true, false)]
    [InlineData(GatewayAiSetupPhase.Prepared, false, false, true)]
    [InlineData(GatewayAiSetupPhase.VerificationRequired, false, false, true)]
    [InlineData(GatewayAiSetupPhase.Uncertain, false, false, true)]
    [InlineData(GatewayAiSetupPhase.Running, false, true, true)]
    public void ProviderDialog_OnlyOpensForAnActionOrRecovery(
        GatewayAiSetupPhase phase, bool busy, bool error, bool expected)
    {
        Assert.Equal(expected, GatewayAiSetupPresentation.ShowProviderDialog(null, phase, busy, error));
    }

    [Theory]
    [InlineData("progress", "gateway", true, false, false, false)]
    [InlineData("progress", "gateway", false, false, false, false)]
    [InlineData("text", "client", true, false, false, true)]
    [InlineData("text", "client", false, false, false, true)]
    [InlineData("select", "client", false, false, false, true)]
    [InlineData("multiselect", "client", false, false, false, true)]
    [InlineData("confirm", "client", false, false, false, true)]
    [InlineData("confirm", "client", true, false, false, true)]
    [InlineData("select", "client", true, false, false, true)]
    [InlineData("note", "client", false, false, false, true)]
    [InlineData("action", "gateway", false, false, false, false)]
    [InlineData("progress", "gateway", true, true, false, true)]
    [InlineData("progress", "gateway", true, false, true, true)]
    public void ProviderDialog_KeepsSignInInstructionsVisibleWhileGatewayPolls(
        string type, string executor, bool busy, bool code, bool link, bool expected)
    {
        var step = new GatewayAiSetupWizardStep
        {
            Id = "step", Type = type, Executor = executor, Message = "Provider status",
            DeviceCode = code ? new("SYNTHETIC") : null,
            ExternalUrl = link ? "https://provider.example/signin" : null,
        };
        Assert.Equal(expected, GatewayAiSetupPresentation.ShowProviderDialog(
            step, GatewayAiSetupPhase.Running, busy, hasError: false));
    }

    [Theory]
    [InlineData(GatewayAiSetupPhase.Uncertain, true, true, true)]
    [InlineData(GatewayAiSetupPhase.Uncertain, true, false, false)]
    [InlineData(GatewayAiSetupPhase.Uncertain, false, true, false)]
    [InlineData(GatewayAiSetupPhase.Running, true, false, true)]
    [InlineData(GatewayAiSetupPhase.VerificationRequired, true, true, false)]
    [InlineData(GatewayAiSetupPhase.Cancelled, true, true, false)]
    public void ProviderStep_RetainsOnlyAnActiveAnswerSubmission(
        GatewayAiSetupPhase phase, bool busy, bool submitting, bool expected)
    {
        var step = new GatewayAiSetupWizardStep { Id = "prompt", Type = "confirm", Executor = "client" };
        Assert.Equal(expected, GatewayAiSetupPresentation.ShowProviderStep(step, phase, busy, submitting));
        if (busy)
            Assert.Equal(expected, GatewayAiSetupPresentation.ShowProviderDialog(
                step, phase, busy, hasError: false, isSubmittingAnswer: submitting));
        Assert.False(GatewayAiSetupPresentation.ShowProviderStep(null, phase, busy, submitting));
    }

    private static async Task<(GatewayAiSetupClient, GatewayAiSetupController, Transport)> CreateAsync(bool requireConsent = false)
    {
        var transport = new Transport { RequireConsent = requireConsent };
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        return (client, new(client, transport.Clock), transport);
    }

    private static string Session(JsonElement parameters) => parameters.GetProperty("sessionId").GetString()!;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Progress(string? sessionId, string url) => Json(new
    {
        sessionId, done = false, status = "running",
        step = new { id = "device-poll", type = "progress", executor = "gateway", externalUrl = url,
            deviceCode = new { code = "SYNTHETIC", expiresInMinutes = 5 } },
    });

    private sealed class Transport : IGatewayAiSetupTransport
    {
        private GatewayAiSetupRoute _route = new("gateway", "main", "authority");
        public Func<GatewayAiSetupRoute>? RouteProvider { get; set; }
        public Action<GatewayAiSetupRoute>? RestartAuthority { get; set; }
        public GatewayAiSetupRoute Route { get => RouteProvider?.Invoke() ?? _route; set => _route = value; }
        public long Generation { get; set; } = 1;
        public bool IsConnected { get; set; } = true;
        public bool Configured { get; set; }
        public void RequireRestartAuthority(GatewayAiSetupRoute expected)
        {
            if (RestartAuthority is not null) RestartAuthority(expected);
            else if (Route != expected) throw new SetupNativeOwnershipException();
        }
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public IReadOnlyCollection<string> Methods =>
            ["openclaw.setup.detect", "openclaw.setup.verify", "openclaw.setup.auth.start",
             "openclaw.setup.prepare.start", "openclaw.setup.activate.start", "wizard.next", "wizard.cancel"];
        public bool RequireConsent { get; init; }
        public FastClock Clock { get; } = new();
        public List<(string Method, JsonElement Parameters)> Calls { get; } = [];
        public Queue<JsonElement> Polls { get; } = [];
        public Func<string, JsonElement, JsonElement>? StartReply { get; set; }
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = Json(parameters);
            Calls.Add((method, payload));
            return Task.FromResult(method == "openclaw.setup.detect" ? Json(new
            {
                candidates = new[] { new { kind = "existing", label = "Existing", detail = "", modelRef = "provider/exact", recommended = false } },
                manualProviders = Array.Empty<object>(),
                authOptions = new[] { new { id = "login", label = "Sign in" } },
                prepareOptions = new[] { new { id = "local/setup", label = "Set up and use" } },
                workspace = "synthetic", setupComplete = Configured, configuredModel = Configured ? "provider/exact" : null,
                nativeSessionCatalogPreferenceRequired = RequireConsent,
            }) : method == "openclaw.setup.verify" ? Json(new { ok = true, modelRef = "provider/exact", latencyMs = 1 })
                : method.EndsWith(".start", StringComparison.Ordinal) ? StartReply!(method, payload) : Polls.Dequeue());
        }
    }

    private sealed class FastClock : TimeProvider
    {
        private long _ticks;
        public Action? OnTick { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(value =>
            {
                Interlocked.Add(ref _ticks, dueTime.Ticks);
                OnTick?.Invoke();
                callback(value);
            }, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}
