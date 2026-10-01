using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>The transport owns authentication and connection lifetime, never this client.</summary>
public interface IGatewayAiSetupTransport
{
    GatewayAiSetupRoute Route { get; }
    long Generation { get; }
    bool IsConnected { get; }
    IReadOnlyCollection<string> Methods { get; }
    IReadOnlyCollection<string> OperatorScopes { get; }
    void RequireRestartAuthority(GatewayAiSetupRoute expected)
    {
        if (Route != expected)
            throw new SetupNativeOwnershipException();
    }
    Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>
    /// Admit cancellation before dispatch, then drain the bounded remote mutation.
    /// A caller cancelling its wait is not evidence that a Gateway write was cancelled.
    /// </summary>
    Task<JsonElement> RequestMutationAsync(string method, object parameters, int timeoutMs,
        CancellationToken cancellationToken, Action? beforeDispatch = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        beforeDispatch?.Invoke();
        return RequestAsync(method, parameters, timeoutMs, CancellationToken.None);
    }
}

public enum GatewayAiSetupPhase
{
    Idle, Choosing, Running, Prepared, VerificationRequired, Verified, Cancelled,
    Rejected, Uncertain, ClassicWizardRequired
}

public enum GatewayAiSetupChoiceKind { Candidate, ManualProvider, Auth, Prepare }

public sealed record GatewayAiSetupChoice(GatewayAiSetupChoiceKind Kind, string Id, string Label, string? ModelRef = null);

public sealed class GatewayAiSetupDetection
{
    public required GatewayAiSetupCandidate[] Candidates { get; init; }
    public required GatewayAiSetupProvider[] ManualProviders { get; init; }
    public GatewayAiSetupUnavailableCandidate[] UnavailableCandidates { get; init; } = [];
    public GatewayAiSetupProvider[] AuthOptions { get; init; } = [];
    public GatewayAiSetupProvider[] PrepareOptions { get; init; } = [];
    public GatewayAiSetupProvider[] RecommendedInstalls { get; init; } = [];
    public GatewayAiSetupNativeCatalog[] NativeSessionCatalogs { get; init; } = [];
    public bool NativeSessionCatalogPreferenceRequired { get; init; }
    public required string Workspace { get; init; }
    public string? ConfiguredModel { get; init; }
    public string? SetupModel { get; init; }
    public string? UtilityModel { get; init; }
    public required bool SetupComplete { get; init; }
}

public sealed record GatewayAiSetupCandidate(
    string Kind, string Label, string Detail, string ModelRef, bool Recommended,
    bool? Credentials = null, string? BrandId = null, string? Icon = null, string? Website = null,
    string? ModelTarget = null);

public sealed record GatewayAiSetupProvider(
    string Id, string Label, string? Hint = null, string? GroupLabel = null,
    string? BrandId = null, string? Icon = null, string? Website = null,
    string? Kind = null, bool Featured = false, string? ActionLabel = null, string? ModelTarget = null);

public sealed record GatewayAiSetupUnavailableCandidate(
    string Id, string Label, string Detail, string Reason, string? AuthOptionId = null,
    string? ManualProviderId = null, string? BrandId = null, string? Icon = null, string? Website = null);

public sealed record GatewayAiSetupNativeCatalog(string PluginId, string Label, string? Detail = null);
public sealed record GatewayAiSetupWizardOption(JsonElement Value, string Label, string? Hint = null);
public sealed record GatewayAiSetupDeviceCode(string Code, int? ExpiresInMinutes = null, string? Message = null);

/// <summary>Shared wizard wire contract. Sensitive values must not be echoed or exported.</summary>
public sealed class GatewayAiSetupWizardStep
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? Format { get; init; }
    public GatewayAiSetupWizardOption[] Options { get; init; } = [];
    public JsonElement? InitialValue { get; init; }
    public string? Placeholder { get; init; }
    public bool Sensitive { get; init; }
    public string? Executor { get; init; }
    public string? ExternalUrl { get; init; }
    public GatewayAiSetupDeviceCode? DeviceCode { get; init; }
}

public sealed class GatewayAiSetupWizardResult
{
    public string? SessionId { get; init; }
    public required bool Done { get; init; }
    public GatewayAiSetupWizardStep? Step { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? PreparedModelRef { get; init; }
    public GatewayAiSetupModelActivation? ModelActivation { get; init; }
    public GatewayAiSetupActivationRejection? ActivationRejection { get; init; }
}

public sealed record GatewayAiSetupModelActivation(
    string ModelRef, bool GatewayRestartRequired = false, string? ModelTarget = null);
public sealed record GatewayAiSetupActivationRejection(string Disposition, string Status);

public sealed class GatewayAiSetupVerification
{
    public required bool Ok { get; init; }
    public string? ModelRef { get; init; }
    public string? ModelTarget { get; init; }
    public double? LatencyMs { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
}

public sealed class GatewayAiSetupActivation
{
    public required bool Ok { get; init; }
    public string? ModelRef { get; init; }
    public string? ModelTarget { get; init; }
    public bool GatewayRestartRequired { get; init; }
    public double? LatencyMs { get; init; }
    public string[]? Lines { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? Disposition { get; init; }
}
