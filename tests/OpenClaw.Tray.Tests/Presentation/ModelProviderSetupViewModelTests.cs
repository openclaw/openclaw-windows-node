using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class ModelProviderSetupViewModelTests
{
    private static readonly ModelProviderCapabilities All = new(true, true, true);

    [Fact]
    public void ParseAuthStatus_ListsConnectableProvidersWithConnectedState()
    {
        using var payload = JsonDocument.Parse("""
            {
              "ts": 1,
              "providers": [
                { "provider": "openai", "displayName": "OpenAI", "status": "ok",
                  "profiles": [ { "profileId": "openai:default", "type": "oauth", "status": "ok" } ] }
              ],
              "providerCapabilities": [
                { "provider": "openai", "apiKeySupported": true, "quickApiKeySetup": true,
                  "loginOptions": [
                    { "id": "openai/codex", "brandId": "openai", "label": "Sign in with ChatGPT", "kind": "oauth", "featured": true },
                    { "id": "openai/odd", "brandId": "openai", "label": "Unknown", "kind": "x", "featured": false }
                  ] },
                { "provider": "anthropic", "apiKeySupported": true, "quickApiKeySetup": true },
                { "provider": "nologin", "apiKeySupported": false, "quickApiKeySetup": false }
              ]
            }
            """);

        IReadOnlyList<ModelProviderCatalogEntry> catalog = ModelProviderWire.ParseAuthStatus(payload.RootElement);

        Assert.Collection(catalog,
            anthropic =>
            {
                Assert.Equal("anthropic", anthropic.Provider);
                Assert.False(anthropic.IsConfigured);
                Assert.Empty(anthropic.LoginOptions);
            },
            openai =>
            {
                Assert.Equal("OpenAI", openai.DisplayName);
                Assert.True(openai.IsConfigured);
                ModelProviderLoginOption option = Assert.Single(openai.LoginOptions);
                Assert.Equal("openai/codex", option.Id);
            });
    }

    [Fact]
    public void BuildCustomProviderPatch_WritesNoAuthOnlyForKeylessCompletions()
    {
        var keyless = new CustomModelProviderDraft("local", " http://127.0.0.1:8000/v1 ", "openai-completions", null, ["m"]);
        Assert.Equal(
            """{"models":{"providers":{"local":{"baseUrl":"http://127.0.0.1:8000/v1","api":"openai-completions","auth":"none","models":[{"id":"m","name":"m (Custom Provider)","contextWindow":128000,"maxTokens":4096,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"reasoning":false}]}}}}""",
            ModelProviderWire.BuildCustomProviderPatch(keyless).ToJsonString());

        var keyed = new CustomModelProviderDraft("claude-proxy", "https://proxy.example/v1", "anthropic-messages", "k", ["a", "b"]);
        string json = ModelProviderWire.BuildCustomProviderPatch(keyed).ToJsonString();
        Assert.Contains("\"apiKey\":\"k\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"auth\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"b\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My Endpoint", "http://h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.InvalidProviderId))]
    [InlineData("-edge", "http://h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.InvalidProviderId))]
    [InlineData("openai", "http://h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.ProviderIdReserved))]
    [InlineData("llamacpp", "http://h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.ProviderIdReserved))]
    [InlineData("local", "ftp://h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.InvalidBaseUrl))]
    [InlineData("local", "https://user:pw@h/v1", "openai-completions", null, "m", nameof(CustomProviderValidationError.InvalidBaseUrl))]
    [InlineData("local", "https://h/v1", "anthropic-messages", null, "m", nameof(CustomProviderValidationError.ApiKeyRequired))]
    [InlineData("local", "https://h/v1", "openai-completions", null, " , ", nameof(CustomProviderValidationError.MissingModels))]
    [InlineData("local", "https://h/v1", "openai-responses", "k", "a b", nameof(CustomProviderValidationError.InvalidModelId))]
    [InlineData("local-2", "https://h/v1", "openai-responses", "k", "org/model-a, model-b", nameof(CustomProviderValidationError.None))]
    public void Validate_RejectsUnsafeDrafts(
        string id, string url, string api, string? key, string models, string expected)
    {
        var draft = new CustomModelProviderDraft(id, url, api, key, ModelProviderWire.ParseModelIds(models));
        Assert.Equal(Enum.Parse<CustomProviderValidationError>(expected), ModelProviderWire.Validate(draft, ["openai"]));
    }

    [Fact]
    public void ProviderExists_DetectsExistingProviderKey()
    {
        using var config = JsonDocument.Parse("""{"models":{"providers":{"custom":{}}}}""");
        Assert.True(ModelProviderWire.ProviderExists(config.RootElement, "custom"));
        Assert.False(ModelProviderWire.ProviderExists(config.RootElement, "other"));
    }

    [Fact]
    public async Task AddCustomProvider_InvalidDraftSendsNothing()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        var vm = Create(gateway);

        Assert.False(await vm.AddCustomProviderAsync("Bad Id", "http://h/v1", "openai-completions", null, "m"));

        Assert.Equal(ModelProviderStatus.Invalid, vm.Status);
        Assert.Equal(CustomProviderValidationError.InvalidProviderId, vm.ValidationError);
        Assert.DoesNotContain(gateway.Calls, call => call.StartsWith("custom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddCustomProvider_ReservesProvidersKnownFromTheModelList()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        var vm = Create(gateway, knownProviders: ["openrouter"]);

        Assert.False(await vm.AddCustomProviderAsync("openrouter", "http://h/v1", "openai-completions", null, "m"));
        Assert.Equal(CustomProviderValidationError.ProviderIdReserved, vm.ValidationError);

        Assert.True(await vm.AddCustomProviderAsync("my-box", "http://h/v1", "openai-completions", "", "m"));
        Assert.Equal(ModelProviderStatus.CustomProviderAdded, vm.Status);
        CustomModelProviderDraft sent = Assert.Single(gateway.CustomDrafts);
        Assert.Null(sent.ApiKey);
    }

    [Fact]
    public async Task SignIn_PollsGatewayStepsThenAnswersAndCompletes()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        gateway.LoginResults.Enqueue(Running(new GatewayAiSetupWizardStep { Id = "wait", Type = "progress" }));
        gateway.LoginResults.Enqueue(Running(new GatewayAiSetupWizardStep { Id = "key", Type = "text", Sensitive = true }));
        gateway.LoginResults.Enqueue(new GatewayAiSetupWizardResult { Done = true, Status = "done" });
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();
        int catalogLoads = gateway.CatalogLoads;

        await vm.StartLoginAsync();

        Assert.True(vm.LoginActive);
        Assert.Equal(GatewayAiSetupPhase.Running, vm.LoginPhase);
        Assert.Equal("key", vm.LoginStep?.Id);
        Assert.False(vm.LoginBusy);

        await vm.SubmitLoginAnswerAsync("key", JsonSerializer.SerializeToElement("secret"));

        Assert.False(vm.LoginActive);
        Assert.Equal(ModelProviderStatus.SignedIn, vm.Status);
        Assert.Equal("Sign in with Example", vm.StatusSubject);
        Assert.Equal(["start:example/browser", "next:", "next:key=\"secret\""], gateway.Calls);
        Assert.True(gateway.CatalogLoads > catalogLoads);
    }

    [Fact]
    public async Task SignIn_CancelRequiresGatewayConfirmation()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        gateway.LoginResults.Enqueue(Running(new GatewayAiSetupWizardStep { Id = "key", Type = "text" }));
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();
        await vm.StartLoginAsync();

        gateway.CancelStatuses.Enqueue("running");
        await vm.CancelLoginAsync();
        Assert.True(vm.LoginActive);
        Assert.Equal(ModelProviderLoginError.CancelUnconfirmed, vm.LoginError);
        Assert.Equal(GatewayAiSetupPhase.Uncertain, vm.LoginPhase);

        gateway.CancelStatuses.Enqueue("cancelled");
        await vm.CancelLoginAsync();
        Assert.False(vm.LoginActive);
        Assert.Equal(ModelProviderStatus.SignInCancelled, vm.Status);
    }

    [Fact]
    public async Task SignIn_UnsupportedStepStopsWithoutFurtherRequests()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        gateway.LoginResults.Enqueue(Running(new GatewayAiSetupWizardStep { Id = "w", Type = "widget" }));
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();

        await vm.StartLoginAsync();

        Assert.True(vm.LoginActive);
        Assert.Equal(GatewayAiSetupPhase.Uncertain, vm.LoginPhase);
        Assert.Equal(ModelProviderLoginError.UnsupportedStep, vm.LoginError);
        Assert.Equal(["start:example/browser"], gateway.Calls);
        await vm.SubmitLoginAnswerAsync("w", null);
        Assert.Equal(["start:example/browser"], gateway.Calls);
    }

    [Fact]
    public async Task SignIn_GatewayErrorEndsWithItsMessage()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        gateway.LoginResults.Enqueue(new GatewayAiSetupWizardResult { Done = true, Status = "error", Error = "Provider refused." });
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();

        await vm.StartLoginAsync();

        Assert.False(vm.LoginActive);
        Assert.Equal(ModelProviderStatus.SignInFailed, vm.Status);
        Assert.Equal("Provider refused.", vm.StatusDetail);
    }

    [Fact]
    public async Task SaveApiKey_WithoutCapabilitySendsNothing()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = new(false, true, true) };
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();

        Assert.False(vm.CanSaveApiKey);
        Assert.False(await vm.SaveApiKeyAsync("k"));
        Assert.Empty(gateway.ApiKeys);
    }

    [Fact]
    public async Task SaveApiKey_TrimsKeyAndReportsGatewayWarning()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All, ApiKeyWarning = "Restart to apply." };
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();

        Assert.True(await vm.SaveApiKeyAsync("  sk-test  "));

        Assert.Equal([("example", "sk-test")], gateway.ApiKeys);
        Assert.Equal(ModelProviderStatus.ApiKeySaved, vm.Status);
        Assert.Equal("Example", vm.StatusSubject);
        Assert.Equal("Restart to apply.", vm.StatusDetail);
    }

    [Fact]
    public async Task UnchangedCatalogReload_KeepsCatalogInstanceSoTheSelectorIsNotRebuilt()
    {
        var gateway = new FakeModelProviderGateway { Capabilities = All };
        var vm = Create(gateway);
        await vm.LoadCatalogAsync();
        IReadOnlyList<ModelProviderCatalogEntry> first = vm.Catalog;

        await vm.LoadCatalogAsync();

        Assert.Same(first, vm.Catalog);
        Assert.Equal("example", vm.SelectedProviderId);
        Assert.Equal("example/browser", vm.SelectedLoginOptionId);
    }

    private static ModelProviderSetupViewModel Create(FakeModelProviderGateway gateway, IEnumerable<string>? knownProviders = null)
    {
        var vm = new ModelProviderSetupViewModel(gateway, () => knownProviders ?? [], TimeSpan.Zero);
        vm.UpdateCapabilities();
        return vm;
    }

    private static GatewayAiSetupWizardResult Running(GatewayAiSetupWizardStep step) =>
        new() { Done = false, Status = "running", Step = step };
}

internal sealed class FakeModelProviderGateway : IModelProviderGatewayService
{
    public ModelProviderCapabilities Capabilities { get; set; } = ModelProviderCapabilities.None;
    public IReadOnlyList<ModelProviderCatalogEntry> Catalog { get; set; } =
    [
        new("example", "Example", true, false, [new ModelProviderLoginOption("example/browser", "Sign in with Example", "oauth", null)]),
    ];
    public int CatalogLoads { get; private set; }
    public string? ApiKeyWarning { get; set; }
    public List<(string Provider, string Key)> ApiKeys { get; } = [];
    public List<CustomModelProviderDraft> CustomDrafts { get; } = [];
    public Queue<GatewayAiSetupWizardResult> LoginResults { get; } = new();
    public Queue<string?> CancelStatuses { get; } = new();
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<ModelProviderCatalogEntry>> LoadCatalogAsync()
    {
        CatalogLoads++;
        // A fresh but equal list, as a new Gateway response would produce.
        return Task.FromResult<IReadOnlyList<ModelProviderCatalogEntry>>(
            Catalog.Select(entry => entry with { LoginOptions = entry.LoginOptions.ToArray() }).ToArray());
    }

    public Task<string?> SetApiKeyAsync(string provider, string apiKey)
    {
        ApiKeys.Add((provider, apiKey));
        return Task.FromResult(ApiKeyWarning);
    }

    public Task AddCustomProviderAsync(CustomModelProviderDraft draft)
    {
        Calls.Add("custom:" + draft.ProviderId);
        CustomDrafts.Add(draft);
        return Task.CompletedTask;
    }

    public Task<GatewayAiSetupWizardResult> StartLoginAsync(string sessionId, string authChoice)
    {
        Calls.Add("start:" + authChoice);
        return Task.FromResult(LoginResults.Dequeue());
    }

    public Task<GatewayAiSetupWizardResult> NextLoginAsync(string sessionId, string? stepId, JsonElement? value)
    {
        Calls.Add(stepId is null ? "next:" : $"next:{stepId}={value?.GetRawText()}");
        return Task.FromResult(LoginResults.Dequeue());
    }

    public Task<string?> CancelLoginAsync(string sessionId)
    {
        Calls.Add("cancel");
        return Task.FromResult(CancelStatuses.Dequeue());
    }
}
