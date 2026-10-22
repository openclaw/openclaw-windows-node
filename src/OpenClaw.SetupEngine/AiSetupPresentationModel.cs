namespace OpenClaw.SetupEngine;

/// <summary>
/// Presentation-only groups from one successful Gateway discovery. Missing/failed
/// discovery is not an empty catalog, and displaying a group never selects it.
/// </summary>
public sealed record AiSetupPresentationModel
{
    public bool HasDetection { get; init; }
    public bool DiscoveryFailed { get; init; }
    public IReadOnlyList<GatewayAiSetupCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupProvider> RecommendedInstalls { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupUnavailableCandidate> UnavailableCandidates { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupProvider> PrepareOptions { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupProvider> FeaturedSignIn { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupProvider> ManualProviders { get; init; } = [];
    public IReadOnlyList<GatewayAiSetupProvider> MoreSignIn { get; init; } = [];
    public bool NativeSessionCatalogPreferenceRequired { get; init; }
    public bool HasLocalChoice { get; init; }
    public bool NoUsableCandidates => HasDetection && !DiscoveryFailed && Candidates.Count == 0 && !HasLocalChoice;
    public bool ShowApiKeyForm { get; init; }
    public bool HasUtilityChoices { get; init; }
    public bool HasChoices => HasLocalChoice || Candidates.Count + PrepareOptions.Count + FeaturedSignIn.Count +
        ManualProviders.Count + MoreSignIn.Count > 0;

    public static AiSetupPresentationModel Create(
        GatewayAiSetupDetection? detection,
        IReadOnlySet<GatewayAiSetupChoiceKind> supportedChoices,
        bool discoveryFailed = false, bool apiKeyFormRequested = false,
        string? gatewayId = null, string? localGatewayId = null, string? localModelRef = null,
        bool hasLocalChoice = false)
    {
        if (detection is null || discoveryFailed)
            return new() { DiscoveryFailed = discoveryFailed };

        var candidates = supportedChoices.Contains(GatewayAiSetupChoiceKind.Candidate)
            ? detection.Candidates.Where(candidate => candidate.ModelTarget is null &&
                !(gatewayId is not null && gatewayId == localGatewayId && localModelRef is not null &&
                  candidate.ModelRef == localModelRef)).ToArray() : [];
        var auth = supportedChoices.Contains(GatewayAiSetupChoiceKind.Auth)
            ? detection.AuthOptions.Where(option => option.ModelTarget is null).ToArray() : [];
        var manual = supportedChoices.Contains(GatewayAiSetupChoiceKind.ManualProvider)
            ? detection.ManualProviders.Where(option => option.ModelTarget is null).ToArray() : [];
        return new()
        {
            HasDetection = true,
            HasLocalChoice = hasLocalChoice,
            HasUtilityChoices = detection.Candidates.Any(candidate => candidate.ModelTarget is not null) ||
                detection.ManualProviders.Concat(detection.AuthOptions).Concat(detection.PrepareOptions)
                    .Any(option => option.ModelTarget is not null),
            Candidates = candidates.ToArray(),
            RecommendedInstalls = candidates.Length == 0
                ? detection.RecommendedInstalls.Where(option =>
                    GatewayAiSetupPresentation.TryGetExternalUri(option.Website, out _)).ToArray() : [],
            UnavailableCandidates = detection.UnavailableCandidates.ToArray(),
            PrepareOptions = supportedChoices.Contains(GatewayAiSetupChoiceKind.Prepare)
                ? detection.PrepareOptions.Where(option => option.ModelTarget is null).ToArray() : [],
            FeaturedSignIn = auth.Where(option => option.Featured).ToArray(),
            MoreSignIn = auth.Where(option => !option.Featured).ToArray(),
            ManualProviders = manual.ToArray(),
            NativeSessionCatalogPreferenceRequired = detection.NativeSessionCatalogPreferenceRequired,
            ShowApiKeyForm = manual.Length > 0 && (candidates.Length == 0 && !hasLocalChoice || apiKeyFormRequested),
        };
    }
}
