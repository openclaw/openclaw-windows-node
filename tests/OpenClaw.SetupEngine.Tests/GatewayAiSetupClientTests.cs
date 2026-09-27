using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayAiSetupClientTests
{
    private const string Detection = """
        {"candidates":[{"kind":"provider-auto:demo","label":"Demo AI","detail":"Saved login","modelRef":"demo/model","recommended":true}],
         "manualProviders":[{"id":"demo-key","label":"Demo key","groupLabel":"Demo"}],
         "authOptions":[{"id":"demo-login","label":"Sign in","kind":"device-code","featured":true}],
         "prepareOptions":[{"id":"demo-install","label":"Install local model","actionLabel":"Download"}],
         "workspace":"/home/test","setupComplete":false}
        """;
    private const string Activated = """{"done":true,"status":"done","modelActivation":{"modelRef":"demo/model","gatewayRestartRequired":true}}""";
    private const string Persisted = """{"candidates":[],"manualProviders":[],"workspace":"/home/test","setupComplete":true,"configuredModel":"demo/model"}""";
    private const string Verified = """{"ok":true,"modelRef":"demo/model","latencyMs":12}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeVerifier_ActualMissingApiOrEmptyResponseIsUnavailable(bool missingApi)
    {
        var transport = new FakeTransport();
        if (missingApi) transport.Methods = [];
        else transport.Replies.Enqueue(Json("null"));
        var client = new GatewayAiSetupClient(transport, "demo/model");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyModelAsync(client, "demo/model", CancellationToken.None));
        if (missingApi) Assert.IsType<NotSupportedException>(error.InnerException);
        else Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public async Task VerifiedModelWithoutAuthenticatedSigningAuthority_CannotIssueCompletion()
    {
        var transport = new FakeTransport();
        transport.Route = transport.Route with { IdentityBinding = null };
        transport.Replies.Enqueue(Json(Verified));
        var client = new GatewayAiSetupClient(transport, "demo/model");
        Assert.True((await client.VerifyConfiguredAsync("demo/model")).Ok);
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
    }

    [Fact]
    public async Task Detect_IsPresentationOnly_AndStartRequiresSelection()
    {
        var (client, transport) = await CreateAsync();
        Assert.Equal(GatewayAiSetupPhase.Choosing, client.Phase);
        Assert.Null(client.Selection);
        Assert.Equal("Demo AI", client.Detection!.Candidates[0].Label);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
        Assert.Single(transport.Calls);
        Assert.Equal("openclaw.setup.detect", transport.Calls[0].Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("operator.read")]
    [InlineData("node")]
    public async Task NonAdmin_NeverSendsFocusedRequest(string scope)
    {
        var transport = new FakeTransport { OperatorScopes = [scope] };
        var client = new GatewayAiSetupClient(transport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DetectAsync());
        Assert.Empty(transport.Calls);
        Assert.NotEqual(GatewayAiSetupPhase.ClassicWizardRequired, client.Phase);
    }

    [Fact]
    public async Task MissingFocusedMethods_ExplicitlySignalsClassicFallback()
    {
        var transport = new FakeTransport { Methods = ["wizard.start", "wizard.next", "wizard.cancel"] };
        var client = new GatewayAiSetupClient(transport);
        Assert.Null(await client.DetectAsync());
        Assert.Equal(GatewayAiSetupPhase.ClassicWizardRequired, client.Phase);
        Assert.Empty(transport.Calls);
    }

    [Theory]
    [InlineData(GatewayAiSetupChoiceKind.Candidate, "openclaw.setup.activate.start")]
    [InlineData(GatewayAiSetupChoiceKind.ManualProvider, "openclaw.setup.activate.start")]
    [InlineData(GatewayAiSetupChoiceKind.Auth, "openclaw.setup.auth.start")]
    [InlineData(GatewayAiSetupChoiceKind.Prepare, "openclaw.setup.prepare.start")]
    public async Task SelectedStarts_SendExactTypedParameters(GatewayAiSetupChoiceKind kind, string method)
    {
        var (client, transport) = await CreateAsync();
        switch (kind)
        {
            case GatewayAiSetupChoiceKind.Candidate: client.SelectCandidate("provider-auto:demo", "demo/model"); break;
            case GatewayAiSetupChoiceKind.ManualProvider: client.SelectManualProvider("demo-key"); break;
            case GatewayAiSetupChoiceKind.Auth: client.SelectAuthOption("demo-login"); break;
            case GatewayAiSetupChoiceKind.Prepare: client.SelectPrepareOption("demo-install"); break;
        }
        await client.StartSelectedAsync(kind == GatewayAiSetupChoiceKind.ManualProvider ? "test-secret" : null, false);
        var call = transport.Calls.Last();
        Assert.Equal(method, call.Method);
        Assert.Equal(client.SessionId, call.Parameters.GetProperty("sessionId").GetString());
        Assert.True(Guid.TryParse(client.SessionId, out _));
        Assert.Equal("agent-a", call.Parameters.GetProperty("agentId").GetString());
        Assert.Equal("/home/test", call.Parameters.GetProperty("workspace").GetString());
        Assert.False(call.Parameters.GetProperty("nativeSessionCatalogsEnabled").GetBoolean());
        var expectedNames = kind switch
        {
            GatewayAiSetupChoiceKind.Candidate => new[] { "agentId", "kind", "modelRef", "nativeSessionCatalogsEnabled", "sessionId", "workspace" },
            GatewayAiSetupChoiceKind.ManualProvider => ["agentId", "apiKey", "authChoice", "kind", "nativeSessionCatalogsEnabled", "sessionId", "workspace"],
            _ => ["agentId", "authChoice", "nativeSessionCatalogsEnabled", "sessionId", "workspace"]
        };
        Assert.Equal(expectedNames.Order(), call.Parameters.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("test-secret", client.Selection!.ToString());
    }

    [Fact]
    public async Task LegacyDirectActivation_OnlyWithoutInteractiveAdvertisement_OmitsExactModel()
    {
        var (client, transport) = await CreateAsync();
        transport.Methods = transport.Methods.Where(m => m != "openclaw.setup.activate.start").ToArray();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        transport.Replies.Enqueue(Json("""{"ok":true,"modelRef":"demo/model"}"""));
        await client.StartSelectedAsync();
        var call = transport.Calls.Last();
        Assert.Equal("openclaw.setup.activate", call.Method);
        Assert.False(call.Parameters.TryGetProperty("modelRef", out _));
        Assert.False(call.Parameters.TryGetProperty("sessionId", out _));
        Assert.Equal(GatewayAiSetupPhase.VerificationRequired, client.Phase);
    }

    [Fact]
    public async Task AdvertisedInteractiveWithMissingWizardSupport_DoesNotDowngrade()
    {
        var (client, transport) = await CreateAsync();
        transport.Methods = transport.Methods.Where(m => m != "wizard.cancel").ToArray();
        Assert.Throws<NotSupportedException>(() => client.SelectCandidate("provider-auto:demo", "demo/model"));
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task LostStartReply_RetainsClientSession_ForCancelWithoutReplay()
    {
        var (client, transport) = await CreateAsync();
        client.SelectAuthOption("demo-login");
        transport.Failure = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => client.StartSelectedAsync());
        var id = client.SessionId;
        Assert.NotNull(id);
        Assert.Equal(GatewayAiSetupPhase.Uncertain, client.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
        transport.Replies.Enqueue(Json("""{"status":"cancelled"}"""));
        await client.CancelAsync();
        Assert.Equal(id, transport.Calls.Last().Parameters.GetProperty("sessionId").GetString());
        Assert.Equal(GatewayAiSetupPhase.Cancelled, client.Phase);
        Assert.Single(transport.Calls, c => c.Method.EndsWith(".start"));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("done")]
    [InlineData("error")]
    public async Task CancelNonCancelledStatus_DoesNotReleaseUncertainMutation(string status)
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json($$"""{"status":"{{status}}"}"""));
        await client.CancelAsync();
        Assert.Equal(GatewayAiSetupPhase.Uncertain, client.Phase);
        Assert.NotNull(client.SessionId);
        Assert.Throws<InvalidOperationException>(() => client.SelectAuthOption("demo-login"));
    }

    [Theory]
    [InlineData("""{"done":true,"status":"done"}""")]
    [InlineData("""{"done":true,"status":"error","error":"post-commit failure"}""")]
    [InlineData("""{"done":true,"status":"done","modelActivation":{"modelRef":"wrong/model"}}""")]
    [InlineData("""{"done":true,"status":"done","modelActivation":{"modelRef":"demo/model","modelTarget":"utility"}}""")]
    [InlineData("""{"done":true,"status":"error","activationRejection":{"disposition":"unknown","status":"auth"}}""")]
    public async Task AmbiguousTerminal_NeverMeansVerifiedOrSafeToReplay(string payload)
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json(payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync());
        Assert.Equal(GatewayAiSetupPhase.Uncertain, client.Phase);
        Assert.Null(client.VerifiedModelRef);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DetectAsync());
    }

    [Fact]
    public async Task ExplicitRejection_ReleasesAttemptWithoutClaimingCredentialRollback()
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json("""{"done":true,"status":"error","error":"No access","activationRejection":{"disposition":"rejected-before-promotion","status":"auth"}}"""));
        await client.RefreshAsync();
        Assert.Equal(GatewayAiSetupPhase.Rejected, client.Phase);
        Assert.Equal("No access", client.Wizard!.Error);
        Assert.Null(client.VerifiedModelRef);
    }

    [Fact]
    public async Task PreparationIsNotActivation()
    {
        var (client, transport) = await CreateAsync();
        client.SelectPrepareOption("demo-install");
        await client.StartSelectedAsync();
        transport.Replies.Enqueue(Json("""{"done":true,"status":"done","preparedModelRef":"demo/local"}"""));
        await client.RefreshAsync();
        Assert.Equal(GatewayAiSetupPhase.Prepared, client.Phase);
        Assert.Equal("demo/local", client.Wizard!.PreparedModelRef);
        Assert.Null(client.VerifiedModelRef);
    }

    [Fact]
    public async Task WizardStep_PreservesDeviceCodeExternalLinkSensitiveAndTypedOptions()
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json("""
            {"done":false,"status":"running","step":{"id":"login","type":"text","title":"Sign in",
             "sensitive":true,"executor":"client","externalUrl":"https://example.com/device",
             "deviceCode":{"code":"DEMO-CODE","expiresInMinutes":15,"message":"Open your browser"},
             "options":[{"value":{"id":42},"label":"Demo","hint":"Hint"}],"placeholder":"Code","initialValue":"initial"}}
            """));
        await client.RefreshAsync();
        var step = client.Wizard!.Step!;
        Assert.True(step.Sensitive);
        Assert.Equal("client", step.Executor);
        Assert.Equal("DEMO-CODE", step.DeviceCode!.Code);
        Assert.Equal(15, step.DeviceCode.ExpiresInMinutes);
        Assert.Equal(42, step.Options[0].Value.GetProperty("id").GetInt32());
        Assert.Equal("https://example.com/device", step.ExternalUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NextAsync("stale", Json("true")));
        await client.NextAsync("login", Json("true"));
        var call = transport.Calls.Last();
        Assert.Equal("login", call.Parameters.GetProperty("answer").GetProperty("stepId").GetString());
        Assert.True(call.Parameters.GetProperty("answer").GetProperty("value").GetBoolean());
    }

    [Theory]
    [InlineData("progress", "client")]
    [InlineData("action", "gateway")]
    public async Task GatewayOrProgressStep_IsPolledWithoutAnswer(string type, string executor)
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(JsonSerializer.SerializeToElement(new { done = false, step = new { id = "p", type, executor } }));
        await client.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NextAsync("p"));
        await client.RefreshAsync();
        Assert.False(transport.Calls.Last().Parameters.TryGetProperty("answer", out _));
    }

    [Fact]
    public async Task ActivationReceipt_StillNeedsExactCurrentRouteVerification()
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json(Activated));
        await client.RefreshAsync();
        Assert.Equal(GatewayAiSetupPhase.VerificationRequired, client.Phase);
        Assert.Null(client.VerifiedModelRef);
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        transport.Replies.Enqueue(Json(Verified));
        Assert.True((await client.VerifyAsync()).Ok);
        Assert.Equal("demo/model", client.VerifiedModelRef);
    }

    [Fact]
    public async Task UnknownActivation_ReconcilesOnlyChangedPersistedExactRouteAfterReconnect()
    {
        var (client, transport) = await CreateAsync();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        transport.Failure = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => client.StartSelectedAsync());
        transport.Replies.Enqueue(Json(Persisted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyAsync());
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        transport.Replies.Enqueue(Json(Verified));
        Assert.True((await client.VerifyAsync()).Ok);
        Assert.Equal(GatewayAiSetupPhase.Verified, client.Phase);
    }

    [Fact]
    public async Task ExistingWorkingRoute_CannotProveUnknownActivationSettled()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection.Replace("\"setupComplete\":false", "\"setupComplete\":true,\"configuredModel\":\"demo/model\"")));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        transport.Failure = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => client.StartSelectedAsync());
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyAsync());
        Assert.DoesNotContain(transport.Calls, c => c.Method == "openclaw.setup.verify");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleReply_IsRejectedAcrossGenerationOrGatewayAgentRoute(bool changeGeneration)
    {
        var transport = new FakeTransport();
        var pending = new TaskCompletionSource<JsonElement>();
        transport.Pending = pending.Task;
        var client = new GatewayAiSetupClient(transport);
        var request = client.DetectAsync();
        if (changeGeneration) transport.Generation++;
        else transport.Route = transport.Route with { AgentId = "agent-b" };
        pending.SetResult(Json(Detection));
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Null(client.Detection);
    }

    [Fact]
    public async Task ExplicitConfiguredModelVerification_DoesNotActivateOrDetect()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Verified));
        var client = new GatewayAiSetupClient(transport);
        Assert.True((await client.VerifyConfiguredAsync("demo/model")).Ok);
        Assert.Equal("openclaw.setup.verify", Assert.Single(transport.Calls).Method);
        Assert.Equal(GatewayAiSetupPhase.Verified, client.Phase);
    }

    [Fact]
    public void Advertisement_DoesNotInventMethodsFromVersionOrMalformedFields()
    {
        Assert.Empty(GatewayServerMethodAdvertisement.Parse(Json("""{"server":{"version":"2026.9.1"}}""")));
        Assert.Equal(["wizard.next"], GatewayServerMethodAdvertisement.Parse(Json("""{"features":{"methods":["wizard.next",42,"wizard.next",""]}}""")));
    }

    [Fact]
    public async Task PreparedModel_RequiresExplicitSelection_ThenActivatesExactServerModel()
    {
        var (client, transport) = await CreateAsync();
        client.SelectPrepareOption("demo-install");
        await client.StartSelectedAsync();
        transport.Replies.Enqueue(Json("""{"done":true,"status":"done","preparedModelRef":"demo/local"}"""));
        await client.RefreshAsync();
        Assert.DoesNotContain(transport.Calls, c => c.Method == "openclaw.setup.activate.start");
        client.SelectPreparedModel();
        await client.StartSelectedAsync();
        var call = transport.Calls.Last();
        Assert.Equal("openclaw.setup.activate.start", call.Method);
        Assert.Equal("provider-auto:demo-install", call.Parameters.GetProperty("kind").GetString());
        Assert.Equal("demo/local", call.Parameters.GetProperty("modelRef").GetString());
    }

    [Fact]
    public async Task RestartReceipt_CannotVerifyBeforeFreshHandshake()
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(Json(Activated));
        await client.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyAsync());
        Assert.True(client.GatewayRestartRequired);
        Assert.DoesNotContain(transport.Calls, c => c.Method == "openclaw.setup.verify");
    }

    [Fact]
    public async Task CancellationInvalidatesLateStartReply()
    {
        var (client, transport) = await CreateAsync();
        client.SelectAuthOption("demo-login");
        var pending = new TaskCompletionSource<JsonElement>();
        transport.Pending = pending.Task;
        var start = client.StartSelectedAsync();
        var sessionId = client.SessionId;
        transport.Replies.Enqueue(Json("""{"status":"cancelled"}"""));
        await client.CancelAsync();
        pending.SetResult(JsonSerializer.SerializeToElement(new { sessionId, done = false, status = "running" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.Equal(GatewayAiSetupPhase.Cancelled, client.Phase);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task NewHandshakeInvalidatesPreviouslySelectedChoice()
    {
        var (client, transport) = await CreateAsync();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        transport.Generation++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task NativeCatalogConsent_IsExplicitAndNotAssumed()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection.Replace("\"setupComplete\":false", "\"setupComplete\":false,\"nativeSessionCatalogPreferenceRequired\":true")));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        client.SelectAuthOption("demo-login");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
        Assert.Single(transport.Calls);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("action")]
    [InlineData("confirm")]
    [InlineData("text")]
    [InlineData("select")]
    [InlineData("multiselect")]
    public async Task ClientOwnedProviderStep_IsNeverAcknowledgedByProgressController(string type)
    {
        var (client, transport) = await StartAsync();
        transport.Replies.Enqueue(JsonSerializer.SerializeToElement(new
        {
            done = false, status = "running",
            step = new { id = "essential-consent", type, executor = "client", title = "Provider consent" },
        }));
        await client.RefreshAsync();
        var calls = transport.Calls.Count;
        await new GatewayAiSetupController(client).WaitForInputAsync();
        Assert.Equal(calls, transport.Calls.Count);
        Assert.Equal(GatewayAiSetupPhase.Running, client.Phase);
        await client.NextAsync("essential-consent", type == "confirm" ? Json("false") : null);
        var answer = transport.Calls.Last().Parameters.GetProperty("answer");
        Assert.Equal("essential-consent", answer.GetProperty("stepId").GetString());
        if (type == "confirm")
            Assert.False(answer.GetProperty("value").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredCatalogPreference_ForwardsExactExplicitBoolean(bool consent)
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection.Replace("\"setupComplete\":false",
            "\"setupComplete\":false,\"nativeSessionCatalogPreferenceRequired\":true")));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        client.SelectAuthOption("demo-login");
        await client.StartSelectedAsync(nativeSessionCatalogsEnabled: consent);
        Assert.Equal(consent, transport.Calls.Last().Parameters.GetProperty("nativeSessionCatalogsEnabled").GetBoolean());
    }

    [Fact]
    public async Task OptionalCatalogPreference_RemainsAbsentWithoutAnExplicitValue()
    {
        var (client, transport) = await CreateAsync();
        client.SelectAuthOption("demo-login");
        await client.StartSelectedAsync();
        Assert.False(transport.Calls.Last().Parameters.TryGetProperty("nativeSessionCatalogsEnabled", out _));
    }

    [Fact]
    public async Task PreparedModel_AfterReconnectRetainsReceiptAndRejectsImplicitReselection()
    {
        var (client, transport) = await CreateAsync();
        client.SelectPrepareOption("demo-install");
        await client.StartSelectedAsync();
        transport.Replies.Enqueue(Json("""{"done":true,"status":"done","preparedModelRef":"demo/local"}"""));
        await client.RefreshAsync();
        transport.Generation++;
        Assert.Throws<InvalidOperationException>(client.SelectPreparedModel);
        Assert.Equal(GatewayAiSetupPhase.Prepared, client.Phase);
        Assert.Equal("demo/local", client.Wizard!.PreparedModelRef);
        Assert.DoesNotContain(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
    }

    [Fact]
    public async Task FailedRedetection_ClearsOldSelection()
    {
        var (client, transport) = await CreateAsync();
        client.SelectAuthOption("demo-login");
        transport.Failure = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => client.DetectAsync());
        Assert.Null(client.Selection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
    }

    [Theory]
    [InlineData("https://example.com/device", true)]
    [InlineData("http://localhost/login", true)]
    [InlineData("file:///C:/secret", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:secret@example.com", false)]
    [InlineData("not a url", false)]
    public void ExternalLinks_RequireSafeWebScheme(string value, bool expected) =>
        Assert.Equal(expected, GatewayAiSetupPresentation.TryGetExternalUri(value, out _));

    [Theory]
    [InlineData("Anthropic", "ProviderIcon-claude.svg")]
    [InlineData("openai-codex", "ProviderIcon-codex.svg")]
    [InlineData("../secret", null)]
    [InlineData("https://tracking.example/icon", null)]
    public void ProviderArtwork_OnlyUsesBundledSafeFileNames(string brand, string? expected) =>
        Assert.Equal(expected, GatewayAiSetupPresentation.GetBundledProviderIconFileName(brand));

    [Theory]
    [InlineData("select", "42", "Second")]
    [InlineData("multiselect", "[42]", "Second")]
    [InlineData("select", "{\"id\":1}", "First")]
    public void WizardInitialChoices_PreserveTypedServerDefaults(string type, string initial, string label)
    {
        var step = new GatewayAiSetupWizardStep
        {
            Id = "choice", Type = type, InitialValue = Json(initial),
            Options = [new(Json("""{"id":1}"""), "First"), new(Json("42"), "Second")]
        };
        Assert.Equal(label, Assert.Single(GatewayAiSetupPresentation.GetInitialOptions(step)).Label);
    }

    [Fact]
    public async Task ConfiguredModelMode_RetriesExactVerificationWithoutOfferingOtherProviders()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json("""{"ok":false,"status":"unavailable","error":"Not ready"}"""));
        var client = new GatewayAiSetupClient(transport, "demo/model");
        Assert.False((await client.VerifyConfiguredAsync("demo/model")).Ok);
        Assert.Equal(GatewayAiSetupPhase.Rejected, client.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DetectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSelectedAsync());
        transport.Replies.Enqueue(Json(Verified));
        Assert.True((await client.VerifyConfiguredAsync("demo/model")).Ok);
        Assert.Equal(2, transport.Calls.Count);
        Assert.All(transport.Calls, call => Assert.Equal("openclaw.setup.verify", call.Method));
        Assert.Equal("demo/model", client.VerifiedModelRef);
        Assert.Null(client.Detection);
    }

    [Fact]
    public async Task ConfiguredModelMode_RejectsDifferentModelWithoutGatewayCall()
    {
        var transport = new FakeTransport();
        var client = new GatewayAiSetupClient(transport, "demo/model");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyConfiguredAsync("other/model"));
        Assert.Empty(transport.Calls);
        Assert.Null(client.VerifiedModelRef);
    }

    [Fact]
    public async Task CancelledVerification_RejectsLateSuccessBeforePublishingReadyState()
    {
        var transport = new FakeTransport();
        var pending = new TaskCompletionSource<JsonElement>();
        transport.Pending = pending.Task;
        var client = new GatewayAiSetupClient(transport, "demo/model");
        using var cancellation = new CancellationTokenSource();
        var verify = client.VerifyConfiguredAsync("demo/model", cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(Json(Verified));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verify);
        Assert.NotEqual(GatewayAiSetupPhase.Verified, client.Phase);
        Assert.Null(client.VerifiedModelRef);
    }

    private static async Task<(GatewayAiSetupClient, FakeTransport)> CreateAsync()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        return (client, transport);
    }

    [Theory]
    [InlineData(GatewayAiSetupChoiceKind.Candidate)]
    [InlineData(GatewayAiSetupChoiceKind.ManualProvider)]
    [InlineData(GatewayAiSetupChoiceKind.Auth)]
    [InlineData(GatewayAiSetupChoiceKind.Prepare)]
    public async Task UtilityChoices_ArePreservedButCannotActivateAsMain(GatewayAiSetupChoiceKind kind)
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection.Replace("\"label\":", "\"modelTarget\":\"utility\",\"label\":")
            .Replace("\"setupComplete\":false", "\"setupComplete\":false,\"setupModel\":\"demo/utility\",\"utilityModel\":\"demo/utility\"")));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        Assert.Equal("demo/utility", client.Detection!.SetupModel);
        Assert.Equal("demo/utility", client.Detection.UtilityModel);
        Assert.Equal("utility", client.Detection.Candidates[0].ModelTarget);
        Assert.Throws<NotSupportedException>(() =>
        {
            switch (kind)
            {
                case GatewayAiSetupChoiceKind.Candidate: client.SelectCandidate("provider-auto:demo", "demo/model"); break;
                case GatewayAiSetupChoiceKind.ManualProvider: client.SelectManualProvider("demo-key"); break;
                case GatewayAiSetupChoiceKind.Auth: client.SelectAuthOption("demo-login"); break;
                case GatewayAiSetupChoiceKind.Prepare: client.SelectPrepareOption("demo-install"); break;
            }
        });
        Assert.Single(transport.Calls);
    }

    [Theory]
    [InlineData("""{"ok":true,"modelRef":"demo/model","modelTarget":"utility","latencyMs":12}""")]
    [InlineData("""{"ok":true,"modelRef":"other/model","latencyMs":12}""")]
    public async Task Verification_RejectsWrongRoleOrModel_WithoutHiddenFallback(string reply)
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(reply));
        var client = new GatewayAiSetupClient(transport, "demo/model");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.VerifyConfiguredAsync("demo/model"));
        Assert.Null(client.VerifiedModelRef);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("openclaw.setup.verify", call.Method);
        Assert.Equal(["agentId"], call.Parameters.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(GatewayAiSetupPhase.Running)]
    [InlineData(GatewayAiSetupPhase.Uncertain)]
    public async Task LocalAiSwitch_RejectsBusyProviderBeforeAnyLocalMutation(GatewayAiSetupPhase phase)
    {
        var (client, transport) = await CreateAsync();
        client.SelectAuthOption("demo-login");
        if (phase == GatewayAiSetupPhase.Uncertain)
            transport.Failure = new TimeoutException();
        try { await client.StartSelectedAsync(); }
        catch (TimeoutException) { }
        Assert.False(client.CanLeaveForLocalAi);
        Assert.Throws<InvalidOperationException>(() => client.EnsureLocalAiCanStart("gateway-a"));
        transport.Replies.Enqueue(Json("""{"status":"cancelled"}"""));
        await client.CancelAsync();
        Assert.True(client.CanLeaveForLocalAi);
        client.EnsureLocalAiCanStart("gateway-a");
        Assert.Throws<InvalidOperationException>(() => client.EnsureLocalAiCanStart("different"));
    }

    [Theory]
    [InlineData("existing-model", SetupCompletionIntent.Dashboard)]
    [InlineData("provider-auto:demo", SetupCompletionIntent.CustodianOnboarding)]
    public async Task Completion_UsesExplicitActivationKind_NotSetupComplete(
        string kind, SetupCompletionIntent intent)
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(Json(Detection.Replace("provider-auto:demo", kind)
            .Replace("\"setupComplete\":false", "\"setupComplete\":true,\"configuredModel\":\"demo/model\"")));
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        client.SelectCandidate(kind, "demo/model");
        await client.StartSelectedAsync();
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
        transport.Replies.Enqueue(Json(Activated));
        await client.RefreshAsync();
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        transport.Replies.Enqueue(Json(Verified));
        await client.VerifyAsync();
        var completion = client.GetVerifiedCompletion();
        Assert.Equal(intent, completion.Intent);
        Assert.Equal("gateway-a", completion.GatewayId);
        Assert.Equal("agent-a", completion.AgentId);
        Assert.Equal("demo/model", completion.ModelRef);
        Assert.Equal(transport.Route.IdentityBinding, completion.IdentityBinding);
        Assert.Equal(transport.Route.SessionKey, completion.SessionKey);
        Assert.Null(completion.ModelTarget);
        Assert.Equal(transport.Generation, completion.VerifiedGeneration);
        transport.Generation++;
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
    }

    [Theory]
    [InlineData(GatewayAiSetupChoiceKind.ManualProvider)]
    [InlineData(GatewayAiSetupChoiceKind.Auth)]
    [InlineData(GatewayAiSetupChoiceKind.Prepare)]
    public async Task FreshProviderChoices_RetainCustodianIntentThroughRestart(GatewayAiSetupChoiceKind kind)
    {
        var (client, transport) = await CreateAsync();
        switch (kind)
        {
            case GatewayAiSetupChoiceKind.ManualProvider: client.SelectManualProvider("demo-key"); break;
            case GatewayAiSetupChoiceKind.Auth: client.SelectAuthOption("demo-login"); break;
            case GatewayAiSetupChoiceKind.Prepare: client.SelectPrepareOption("demo-install"); break;
        }
        await client.StartSelectedAsync(kind == GatewayAiSetupChoiceKind.ManualProvider ? "synthetic" : null);
        if (kind == GatewayAiSetupChoiceKind.Prepare)
        {
            transport.Replies.Enqueue(Json("""{"done":true,"status":"done","preparedModelRef":"demo/model"}"""));
            await client.RefreshAsync();
            client.SelectPreparedModel();
            await client.StartSelectedAsync();
        }
        transport.Replies.Enqueue(Json(Activated));
        await client.RefreshAsync();
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        transport.Replies.Enqueue(Json(Verified));
        await client.VerifyAsync();
        Assert.Equal(SetupCompletionIntent.CustodianOnboarding, client.GetVerifiedCompletion().Intent);
    }

    [Theory]
    [InlineData(SetupCompletionIntent.Dashboard)]
    [InlineData(SetupCompletionIntent.CustodianOnboarding)]
    public async Task ConfiguredVerification_PreservesExplicitExistingOrFreshInstallIntent(SetupCompletionIntent intent)
    {
        var transport = new FakeTransport();
        var client = new GatewayAiSetupClient(transport, "demo/model", intent);
        transport.Replies.Enqueue(Json(Verified));
        await client.VerifyConfiguredAsync("demo/model");
        Assert.Equal(intent, client.GetVerifiedCompletion().Intent);
        Assert.Equal("openclaw.setup.verify", Assert.Single(transport.Calls).Method);
        transport.Route = transport.Route with { AgentId = "different-agent" };
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
    }

    [Fact]
    public async Task LostActivationReply_ReconcilesFreshIntentWithoutReplay()
    {
        var (client, transport) = await CreateAsync();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        transport.Failure = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => client.StartSelectedAsync());
        transport.Generation++;
        transport.Replies.Enqueue(Json(Persisted));
        transport.Replies.Enqueue(Json(Verified));
        await client.VerifyAsync();
        Assert.Equal(SetupCompletionIntent.CustodianOnboarding, client.GetVerifiedCompletion().Intent);
        Assert.Single(transport.Calls, call => call.Method == "openclaw.setup.activate.start");
    }

    [Fact]
    public async Task Reverification_DoesNotExposePreviousReceiptWhilePendingOrAfterFailure()
    {
        var transport = new FakeTransport();
        var client = new GatewayAiSetupClient(transport, "demo/model");
        transport.Replies.Enqueue(Json(Verified));
        await client.VerifyConfiguredAsync("demo/model");
        Assert.NotNull(client.GetVerifiedCompletion());
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Pending = reply.Task;
        var pending = client.VerifyConfiguredAsync("demo/model");
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
        reply.SetResult(Json("""{"ok":false,"status":"unavailable","error":"Not ready"}"""));
        Assert.False((await pending).Ok);
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
    }

    private static async Task<(GatewayAiSetupClient, FakeTransport)> StartAsync()
    {
        var (client, transport) = await CreateAsync();
        client.SelectCandidate("provider-auto:demo", "demo/model");
        await client.StartSelectedAsync();
        return (client, transport);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeTransport : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; set; } = new("gateway-a", "agent-a", "authority-a", new string('A', 64),
            new string('B', 64), "agent:agent-a:main");
        public long Generation { get; set; } = 1;
        public bool IsConnected { get; set; } = true;
        public IReadOnlyCollection<string> OperatorScopes { get; set; } = ["operator.admin"];
        public IReadOnlyCollection<string> Methods { get; set; } =
        [
            "openclaw.setup.detect", "openclaw.setup.verify", "openclaw.setup.activate",
            "openclaw.setup.activate.start", "openclaw.setup.auth.start", "openclaw.setup.prepare.start",
            "wizard.next", "wizard.cancel"
        ];
        public List<(string Method, JsonElement Parameters)> Calls { get; } = [];
        public Queue<JsonElement> Replies { get; } = [];
        public Exception? Failure { get; set; }
        public Task<JsonElement>? Pending { get; set; }
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToElement(parameters);
            Calls.Add((method, payload));
            if (Failure is { } failure)
            {
                Failure = null;
                return Task.FromException<JsonElement>(failure);
            }
            if (Pending is { } pending)
            {
                Pending = null;
                return pending;
            }
            return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() :
                Json(method.EndsWith(".start") ? $$"""{"sessionId":"{{payload.GetProperty("sessionId").GetString()}}","done":false,"status":"running"}"""
                    : """{"done":false,"status":"running"}"""));
        }
    }
}
