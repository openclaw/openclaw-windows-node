namespace OpenClaw.SetupEngine.Tests;

public sealed class LlamaAppFreshSetupTests
{
    private static GatewayAiSetupDetection Fresh() => new()
    {
        Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false,
    };

    [Theory]
    [InlineData(LocalAiOnboardingState.SetUp, true)]
    [InlineData(LocalAiOnboardingState.Unsupported, true)]
    [InlineData(LocalAiOnboardingState.Checking, false)]
    [InlineData(LocalAiOnboardingState.Unknown, false)]
    [InlineData(LocalAiOnboardingState.UnsupportedGateway, false)]
    [InlineData(LocalAiOnboardingState.StartAndUse, false)]
    [InlineData(LocalAiOnboardingState.Use, false)]
    [InlineData(LocalAiOnboardingState.Repair, false)]
    [InlineData(LocalAiOnboardingState.Reconcile, false)]
    [InlineData(LocalAiOnboardingState.ManagementBlocked, false)]
    public void Preference_RequiresConclusiveFreshLocalEvidence(LocalAiOnboardingState state, bool expected) =>
        Assert.Equal(expected, LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), new(state)));

    [Fact]
    public void Preference_NeverReplacesExistingProviderDefaultOrRecovery()
    {
        var local = new LocalAiOnboardingSnapshot(LocalAiOnboardingState.SetUp);
        Assert.True(LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), local));
        Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(null, local));
        Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), null));
        Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), local with { HasInstallationEvidence = true }));
        Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), local with { ReceiptIdentity = "damaged-receipt" }));
        Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(Fresh(), local with { ModelRef = "llamacpp/managed" }));
        foreach (var detection in new[]
        {
            new GatewayAiSetupDetection { Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = true },
            new GatewayAiSetupDetection { Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false, ConfiguredModel = "ollama/existing" },
            new GatewayAiSetupDetection { Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false, SetupModel = "llamacpp/managed" },
            new GatewayAiSetupDetection { Candidates = [], ManualProviders = [], Workspace = "/test", SetupComplete = false, UtilityModel = "embedding/model" },
            new GatewayAiSetupDetection { Candidates = [new("existing-model", "Existing", "", "llamacpp/managed", false)], ManualProviders = [], Workspace = "/test", SetupComplete = false },
            new GatewayAiSetupDetection { Candidates = [], UnavailableCandidates = [new("existing", "Existing", "Sign in again", "auth")], ManualProviders = [], Workspace = "/test", SetupComplete = false },
        })
            Assert.False(LlamaAppSetupPresentation.PreferForFreshSetup(detection, local));
    }

}
