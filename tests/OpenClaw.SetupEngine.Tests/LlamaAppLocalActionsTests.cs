namespace OpenClaw.SetupEngine.Tests;

public sealed class LlamaAppLocalActionsTests
{
    private static readonly IReadOnlySet<GatewayAiSetupChoiceKind> AllChoices =
        Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet();

    [Theory]
    [InlineData(GatewayAiSetupChoiceKind.Prepare)]
    [InlineData(GatewayAiSetupChoiceKind.Auth)]
    public void PublicSetupOptions_KeepExactServerChoiceAndDoNotBecomeCloudSignIn(GatewayAiSetupChoiceKind kind)
    {
        var option = new GatewayAiSetupProvider("llama-app", "Llama.app", Kind: "custom", Featured: true);
        var detection = new GatewayAiSetupDetection
        {
            Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false,
            PrepareOptions = kind == GatewayAiSetupChoiceKind.Prepare ? [option] : [],
            AuthOptions = kind == GatewayAiSetupChoiceKind.Auth ? [option] : [],
        };
        var view = AiSetupPresentationModel.Create(detection, AllChoices);
        var local = Assert.Single(view.LocalAppOptions);
        Assert.Same(option, local.Provider);
        Assert.Equal(kind, local.Kind);
        Assert.True(view.HasChoices);
        Assert.Empty(view.PrepareOptions);
        Assert.Empty(view.FeaturedSignIn);
        Assert.Empty(view.MoreSignIn);
        Assert.Empty(AiSetupPresentationModel.Create(detection, new HashSet<GatewayAiSetupChoiceKind>()).LocalAppOptions);
    }

    [Fact]
    public void UtilityChoicesAndUnavailableDiscovery_CannotInventLlamaActions()
    {
        var detection = new GatewayAiSetupDetection
        {
            Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false,
            PrepareOptions = [new("llama-app", "Utility Llama", ModelTarget: "utility")],
            AuthOptions = [new("llama-app", "Utility Llama", ModelTarget: "utility")],
        };
        Assert.Empty(AiSetupPresentationModel.Create(detection, AllChoices).LocalAppOptions);
        Assert.Empty(AiSetupPresentationModel.Create(detection, AllChoices, discoveryFailed: true).LocalAppOptions);
    }

    [Fact]
    public void ManagedLlamaCppAndExternalLlamaApp_KeepDistinctIdentitiesAndCandidateOrder()
    {
        GatewayAiSetupCandidate[] candidates =
        [
            new("existing-model", "Managed", "", "llamacpp/managed", true),
            new("provider-auto:llama-app", "Llama.app", "", "llama-app/org/model:Q4", false),
        ];
        var detection = new GatewayAiSetupDetection
        {
            Candidates = candidates, ManualProviders = [], Workspace = "/test", SetupComplete = false,
            PrepareOptions = [new("llamacpp", "Managed"), new("ollama", "Ollama"), new("llama-app", "Llama.app")],
        };
        var view = AiSetupPresentationModel.Create(detection, AllChoices);
        Assert.Equal(candidates, view.Candidates);
        Assert.Equal(["llamacpp", "ollama"], view.PrepareOptions.Select(option => option.Id));
        Assert.Equal("llama-app", Assert.Single(view.LocalAppOptions).Provider.Id);
    }
}
