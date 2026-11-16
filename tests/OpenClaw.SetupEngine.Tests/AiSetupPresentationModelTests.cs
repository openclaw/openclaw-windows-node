namespace OpenClaw.SetupEngine.Tests;

public sealed class AiSetupPresentationModelTests
{
    [Fact]
    public void NativeRecovery_IsErrorOnlyAndNeverNormalProgressOrVerified()
    {
        foreach (var phase in Enum.GetValues<GatewayAiSetupPhase>())
        {
            Assert.Equal(phase is GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.Rejected,
                GatewayAiSetupPresentation.ShowNativeRecovery(phase, isBusy: false, hasError: false));
            Assert.Equal(phase != GatewayAiSetupPhase.Verified,
                GatewayAiSetupPresentation.ShowNativeRecovery(phase, isBusy: false, hasError: true));
            Assert.False(GatewayAiSetupPresentation.ShowNativeRecovery(phase, isBusy: true, hasError: false));
            Assert.False(GatewayAiSetupPresentation.ShowNativeRecovery(phase, isBusy: true, hasError: true));
        }
    }

    private static readonly IReadOnlySet<GatewayAiSetupChoiceKind> AllChoices =
        Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet();

    [Fact]
    public void MixedDetection_KeepsEveryGroupDistinctAndServerOrdered()
    {
        var detection = Detection(withCandidates: true);
        var view = AiSetupPresentationModel.Create(detection, AllChoices);
        Assert.Equal(detection.Candidates, view.Candidates);
        Assert.Equal(detection.UnavailableCandidates, view.UnavailableCandidates);
        Assert.Equal(detection.PrepareOptions, view.PrepareOptions);
        Assert.Equal(detection.ManualProviders, view.ManualProviders);
        Assert.Equal(["featured"], view.FeaturedSignIn.Select(option => option.Id));
        Assert.Equal(["additional", "last"], view.MoreSignIn.Select(option => option.Id));
        Assert.Empty(view.RecommendedInstalls);
        Assert.False(view.ShowApiKeyForm);
        Assert.False(view.NoUsableCandidates);
        Assert.True(view.HasChoices);
        Assert.True(view.NativeSessionCatalogPreferenceRequired);
    }

    [Fact]
    public void NoUsableCandidates_ExposesReturnedWebsitesButKeepsKeysCollapsed()
    {
        var detection = Detection(withCandidates: false);
        var view = AiSetupPresentationModel.Create(detection, AllChoices);
        Assert.True(view.NoUsableCandidates);
        Assert.False(view.ShowApiKeyForm);
        Assert.Equal(["website"], view.RecommendedInstalls.Select(option => option.Id));
        Assert.Single(view.UnavailableCandidates);
        Assert.Single(view.PrepareOptions);
        Assert.Single(view.ManualProviders);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ManualForm_WithCandidates_RequiresExplicitRequest(bool requested, bool visible)
    {
        var view = AiSetupPresentationModel.Create(Detection(true), AllChoices, apiKeyFormRequested: requested);
        Assert.Equal(visible, view.ShowApiKeyForm);
        Assert.Equal(["manual"], view.ManualProviders.Select(option => option.Id));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void LocalChoice_NeverAutomaticallyExpandsCredentials(bool localVisible, bool requested, bool expanded)
    {
        var view = AiSetupPresentationModel.Create(Detection(false), AllChoices,
            apiKeyFormRequested: requested, hasLocalChoice: localVisible);
        Assert.Equal(localVisible, view.HasLocalChoice);
        Assert.Equal(!localVisible, view.NoUsableCandidates);
        Assert.Equal(expanded, view.ShowApiKeyForm);
        Assert.Single(view.ManualProviders);
        Assert.Single(view.PrepareOptions);
    }

    [Fact]
    public void EmptySuccessfulDetection_IsNotAnErrorAndDoesNotInventChoices()
    {
        var view = AiSetupPresentationModel.Create(new()
        {
            Candidates = [], ManualProviders = [], Workspace = "workspace", SetupComplete = false,
        }, AllChoices);
        Assert.True(view.HasDetection);
        Assert.True(view.NoUsableCandidates);
        Assert.False(view.DiscoveryFailed);
        Assert.False(view.HasChoices);
        Assert.False(view.ShowApiKeyForm);
        Assert.Empty(view.RecommendedInstalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDiscovery_NeverPresentsAnEmptyOrStaleCatalog(bool previousDetection)
    {
        var view = AiSetupPresentationModel.Create(previousDetection ? Detection(false) : null,
            AllChoices, discoveryFailed: true, apiKeyFormRequested: true);
        Assert.True(view.DiscoveryFailed);
        Assert.False(view.HasDetection);
        Assert.False(view.NoUsableCandidates);
        Assert.False(view.HasChoices);
        Assert.False(view.ShowApiKeyForm);
        Assert.Empty(view.UnavailableCandidates);
        Assert.Empty(view.RecommendedInstalls);
    }

    [Fact]
    public void PendingDiscovery_IsNeitherEmptyNorFailed()
    {
        var view = AiSetupPresentationModel.Create(null, AllChoices);
        Assert.False(view.HasDetection);
        Assert.False(view.DiscoveryFailed);
        Assert.False(view.NoUsableCandidates);
    }

    [Fact]
    public void UnadvertisedActions_AreNotOfferedAndNeverChangeClassification()
    {
        var view = AiSetupPresentationModel.Create(Detection(true),
            new HashSet<GatewayAiSetupChoiceKind> { GatewayAiSetupChoiceKind.Auth });
        Assert.Empty(view.Candidates);
        Assert.Empty(view.ManualProviders);
        Assert.Empty(view.PrepareOptions);
        Assert.False(view.ShowApiKeyForm);
        Assert.Single(view.FeaturedSignIn);
        Assert.Equal(2, view.MoreSignIn.Count);
        Assert.Single(view.UnavailableCandidates);
    }

    [Fact]
    public void Projection_DoesNotMutateGatewayArraysOrInterpretCredentialFlags()
    {
        var detection = Detection(true);
        var candidates = detection.Candidates.ToArray();
        var auth = detection.AuthOptions.ToArray();
        _ = AiSetupPresentationModel.Create(detection, AllChoices);
        Assert.Equal(candidates, detection.Candidates);
        Assert.Equal(auth, detection.AuthOptions);
        Assert.Equal(2, detection.Candidates.Length);
        // The Gateway classifies candidates and unavailable discoveries, not Windows.
        Assert.False(detection.Candidates[1].Credentials);
    }

    [Theory]
    [InlineData("ollama/model", true)]
    [InlineData("lmstudio/model", true)]
    [InlineData("llama-cpp/model", true)]
    [InlineData("llamacpp/model", true)]
    [InlineData("ollama-cloud/model", false)]
    [InlineData("custom/model", false)]
    [InlineData("openai/model", false)]
    public void ModelServices_AreSeparatedWithoutInferringLocalityFromLabels(string modelRef, bool local)
    {
        var detection = new GatewayAiSetupDetection
        {
            Candidates = [new("existing-model", "Ollama on this PC", "Gateway supplied", modelRef, true)],
            ManualProviders = [], Workspace = "workspace", SetupComplete = false,
        };
        var account = AiSetupPresentationModel.Create(detection, AllChoices, path: AiSetupConnectionPath.Account);
        var service = AiSetupPresentationModel.Create(detection, AllChoices, path: AiSetupConnectionPath.LocalModel);
        Assert.Equal(local ? 0 : 1, account.Candidates.Count);
        Assert.Equal(local ? 1 : 0, service.Candidates.Count);
        Assert.Equal(modelRef, Assert.Single(account.Candidates.Concat(service.Candidates)).ModelRef);
    }

    [Fact]
    public void Paths_PreserveEveryProviderKindAndOrderingWithoutMutatingDetection()
    {
        var detection = new GatewayAiSetupDetection
        {
            Candidates = [], Workspace = "workspace", SetupComplete = false,
            AuthOptions = [new("featured", "Featured", Featured: true),
                new("ollama-login", "A service", BrandId: "ollama", Featured: true),
                new("custom", "Custom endpoint", Kind: "custom"), new("other", "Other")],
            ManualProviders = [new("openai", "Cloud key"), new("lmstudio", "Service key")],
            PrepareOptions = [new("llama-cpp", "Managed service"), new("future", "Unknown option")],
            UnavailableCandidates = [new("unready-service", "Service", "Missing", "missing", AuthOptionId: "ollama-login"),
                new("unready-account", "Account", "Missing", "missing")],
            RecommendedInstalls = [new("ollama", "Service", Website: "https://example.com/service"),
                new("cli", "Account CLI", Website: "https://example.com/cli")],
        };
        var account = AiSetupPresentationModel.Create(detection, AllChoices, hasLocalChoice: true, path: AiSetupConnectionPath.Account);
        var local = AiSetupPresentationModel.Create(detection, AllChoices, hasLocalChoice: true, path: AiSetupConnectionPath.LocalModel);
        Assert.Equal(["featured"], account.FeaturedSignIn.Select(p => p.Id));
        Assert.Equal(["custom", "other"], account.MoreSignIn.Select(p => p.Id));
        Assert.Equal(["ollama-login"], local.FeaturedSignIn.Select(p => p.Id));
        Assert.Equal(["openai"], account.ManualProviders.Select(p => p.Id));
        Assert.Equal(["lmstudio"], local.ManualProviders.Select(p => p.Id));
        Assert.Equal(["future"], account.PrepareOptions.Select(p => p.Id));
        Assert.Equal(["llama-cpp"], local.PrepareOptions.Select(p => p.Id));
        Assert.Equal(["unready-account"], account.UnavailableCandidates.Select(p => p.Id));
        Assert.Equal(["unready-service"], local.UnavailableCandidates.Select(p => p.Id));
        Assert.Equal(["cli"], account.RecommendedInstalls.Select(p => p.Id));
        Assert.Equal(["ollama"], local.RecommendedInstalls.Select(p => p.Id));
        Assert.False(account.HasLocalChoice);
        Assert.True(local.HasLocalChoice);
        Assert.False(account.ShowApiKeyForm);
        Assert.False(local.ShowApiKeyForm);
        Assert.Equal(4, detection.AuthOptions.Length);
    }

    private static GatewayAiSetupDetection Detection(bool withCandidates) => new()
    {
        Candidates = withCandidates
            ? [new("existing", "Existing", "Ready", "provider/model", true),
                new("server-choice", "Another", "Gateway supplied", "provider/other", false, Credentials: false)]
            : [],
        UnavailableCandidates = [new("not-ready", "Detected tool", "Sign-in required", "missing-credentials")],
        PrepareOptions = [new("prepare", "Local preparation")],
        ManualProviders = [new("manual", "Manual credential")],
        AuthOptions = [new("additional", "Additional"), new("featured", "Featured", Featured: true), new("last", "Last")],
        RecommendedInstalls =
        [
            new("website", "Returned website", Website: "https://provider.example/install"),
            new("missing", "No website"),
            new("unsafe", "Not a website", Website: "file:///C:/installer.exe"),
            new("credential", "Embedded credentials", Website: "https://user:password@provider.example"),
        ],
        Workspace = "workspace",
        SetupComplete = false,
        NativeSessionCatalogPreferenceRequired = true,
    };
}
