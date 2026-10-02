using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Focused setup state for one gateway/agent route. Detection is presentation only;
/// mutations require a selected server-provided choice. Uncertain writes are never replayed.
/// </summary>
public sealed class GatewayAiSetupClient(
    IGatewayAiSetupTransport transport, string? expectedConfiguredModelRef = null,
    SetupCompletionIntent configuredCompletionIntent = SetupCompletionIntent.Dashboard,
    bool requiresManagedLocalAi = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GatewayAiSetupRoute _route = transport.Route;
    private long _operation;
    private bool _busy;
    private long _detectionGeneration;
    private long _attemptGeneration;
    private GatewayAiSetupDetection? _before;
    private string? _expectedModel;
    private bool _hasActivationReceipt;
    private readonly SetupCompletionIntent _configuredCompletionIntent = configuredCompletionIntent;
    private SetupCompletionIntent _completionIntent = configuredCompletionIntent;
    private bool _requiresManagedLocalAi;
    private long _verifiedGeneration;
    public bool GatewayRestartRequired { get; private set; }

    public GatewayAiSetupPhase Phase { get; private set; }
    public GatewayAiSetupDetection? Detection { get; private set; }
    public GatewayAiSetupChoice? Selection { get; private set; }
    public GatewayAiSetupWizardResult? Wizard { get; private set; }
    public string? SessionId { get; private set; }
    public string? VerifiedModelRef { get; private set; }
    public bool IsBusy => _busy;
    public GatewayAiSetupRoute Route => _route;
    public long ConnectionGeneration => transport.Generation;
    public bool WaitingForRestart => GatewayRestartRequired &&
        (transport.Generation == _attemptGeneration || !transport.IsConnected);
    public bool CanLeaveForLocalAi => !_busy && Phase is not (GatewayAiSetupPhase.Running or
        GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.VerificationRequired);
    public bool RequiresReconciliation => Phase is GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.VerificationRequired;

    public GatewayAiSetupCompletion GetVerifiedCompletion()
    {
        EnsureAuthorized();
        if (_busy || Phase != GatewayAiSetupPhase.Verified || VerifiedModelRef is not { Length: > 0 } model ||
            transport.Generation != _verifiedGeneration || string.IsNullOrWhiteSpace(_route.EndpointBinding) ||
            !SetupCompletionAuthority.IsValid(_route.IdentityBinding, _route.SessionKey, _route.AgentId))
            throw new InvalidOperationException("AI setup completion requires a current, exact-model verification.");
        return new(_completionIntent, _route.GatewayId, _route.EndpointBinding,
            model, _route.AgentId, _verifiedGeneration, IdentityBinding: _route.IdentityBinding,
            SessionKey: _route.SessionKey, RequiresManagedLocalAi: _requiresManagedLocalAi);
    }

    internal void RequireSameRoute()
    {
        if (transport.Route != _route)
            throw new InvalidOperationException("The gateway or agent changed during AI setup.");
    }

    internal void RequireExpectedRestartAuthority()
    {
        if (!GatewayRestartRequired || Phase != GatewayAiSetupPhase.VerificationRequired)
            throw new InvalidOperationException("No expected Gateway restart is pending.");
        transport.RequireRestartAuthority(_route);
        if (transport.IsConnected)
            RequireSameRoute();
    }

    public bool HasMethod(string method) => transport.Methods.Contains(method, StringComparer.Ordinal);
    public bool SupportsInteractiveActivation => HasMethod("openclaw.setup.activate.start");
    public bool SupportsChoice(GatewayAiSetupChoiceKind kind) => kind switch
    {
        GatewayAiSetupChoiceKind.Auth => HasWizardMethods && HasMethod("openclaw.setup.auth.start"),
        GatewayAiSetupChoiceKind.Prepare => HasWizardMethods && HasMethod("openclaw.setup.prepare.start"),
        _ => SupportsInteractiveActivation
            ? HasWizardMethods
            : HasMethod("openclaw.setup.activate")
    };
    private bool HasWizardMethods => HasMethod("wizard.next") && HasMethod("wizard.cancel");

    public async Task<GatewayAiSetupDetection?> DetectAsync(CancellationToken ct = default)
    {
        EnsureChoicesAllowed();
        EnsureSettled();
        EnsureAuthorized();
        Selection = null;
        Phase = GatewayAiSetupPhase.Idle;
        if (!HasMethod("openclaw.setup.detect") || !HasMethod("openclaw.setup.verify") ||
            !Enum.GetValues<GatewayAiSetupChoiceKind>().Any(SupportsChoice))
        {
            Phase = GatewayAiSetupPhase.ClassicWizardRequired;
            return null;
        }
        GatewayAiSetupDetection detection;
        try
        {
            detection = await ExecuteAsync<GatewayAiSetupDetection>("openclaw.setup.detect", RouteParams(), 40_000, ct);
        }
        catch (InvalidOperationException error) when (
            error.Message.Equals("unknown method: openclaw.setup.detect", StringComparison.OrdinalIgnoreCase))
        {
            Phase = GatewayAiSetupPhase.ClassicWizardRequired;
            return null;
        }
        ValidateDetection(detection);
        Detection = detection;
        _detectionGeneration = transport.Generation;
        Selection = null;
        Phase = GatewayAiSetupPhase.Choosing;
        return detection;
    }

    public void SelectCandidate(string kind, string modelRef)
    {
        var matches = RequireDetection().Candidates.Where(c => c.Kind == kind && c.ModelRef == modelRef).ToArray();
        var candidate = matches.SingleOrDefault(c => c.ModelTarget is null) ?? matches.Single();
        RequireMainModel(candidate.ModelTarget);
        Select(new(GatewayAiSetupChoiceKind.Candidate, kind, candidate.Label, modelRef));
    }

    public void SelectManualProvider(string id) => SelectProvider(GatewayAiSetupChoiceKind.ManualProvider, id, RequireDetection().ManualProviders);
    public void SelectAuthOption(string id) => SelectProvider(GatewayAiSetupChoiceKind.Auth, id, RequireDetection().AuthOptions);
    public void SelectPrepareOption(string id) => SelectProvider(GatewayAiSetupChoiceKind.Prepare, id, RequireDetection().PrepareOptions);

    public void SelectPreparedModel()
    {
        if (Phase != GatewayAiSetupPhase.Prepared || Wizard?.PreparedModelRef is not { Length: > 0 } modelRef ||
            Selection is not { Kind: GatewayAiSetupChoiceKind.Prepare } prepare || !SupportsInteractiveActivation)
            throw new InvalidOperationException("No prepared model is available for exact interactive activation.");
        Select(new(GatewayAiSetupChoiceKind.Candidate,
            "provider-auto:" + Uri.EscapeDataString(prepare.Id), prepare.Label, modelRef));
    }

    private void SelectProvider(GatewayAiSetupChoiceKind kind, string id, GatewayAiSetupProvider[] options)
    {
        var matches = options.Where(p => p.Id == id).ToArray();
        var provider = matches.SingleOrDefault(p => p.ModelTarget is null) ?? matches.Single();
        RequireMainModel(provider.ModelTarget);
        Select(new(kind, id, provider.Label));
    }

    private static void RequireMainModel(string? target)
    {
        if (target is not null)
            throw new NotSupportedException("This choice is not available for the main assistant. Utility model setup is not supported here.");
    }

    public void EnsureLocalAiCanStart(string gatewayId)
    {
        EnsureSettled();
        EnsureAuthorized();
        EnsureMethod("openclaw.setup.verify");
        if (_route.GatewayId != gatewayId)
            throw new InvalidOperationException("Local AI setup requires the same Gateway.");
    }

    private void Select(GatewayAiSetupChoice choice)
    {
        EnsureChoicesAllowed();
        EnsureSettled();
        EnsureAuthorized();
        if (_detectionGeneration != transport.Generation)
            throw new InvalidOperationException("The gateway reconnected. Refresh the choices before selecting.");
        if (!SupportsChoice(choice.Kind))
            throw new NotSupportedException("This gateway does not advertise the selected setup method.");
        Selection = choice;
        VerifiedModelRef = null;
        Phase = GatewayAiSetupPhase.Choosing;
    }

    public async Task StartSelectedAsync(string? apiKey = null, bool? nativeSessionCatalogsEnabled = null, CancellationToken ct = default)
    {
        EnsureChoicesAllowed();
        EnsureSettled();
        EnsureAuthorized();
        ct.ThrowIfCancellationRequested();
        var choice = Selection ?? throw new InvalidOperationException("Choose an AI setup option first.");
        if (_detectionGeneration != transport.Generation)
            throw new InvalidOperationException("The gateway reconnected. Refresh the choices before starting.");
        if (Detection!.NativeSessionCatalogPreferenceRequired && nativeSessionCatalogsEnabled is null)
            throw new InvalidOperationException("Choose whether to discover native conversations first.");
        if (choice.Kind == GatewayAiSetupChoiceKind.ManualProvider && string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Enter a provider credential.", nameof(apiKey));

        var method = choice.Kind switch
        {
            GatewayAiSetupChoiceKind.Auth => "openclaw.setup.auth.start",
            GatewayAiSetupChoiceKind.Prepare => "openclaw.setup.prepare.start",
            _ => SupportsInteractiveActivation ? "openclaw.setup.activate.start" : "openclaw.setup.activate"
        };
        EnsureMethod(method);
        var parameters = RouteParams();
        parameters["workspace"] = Detection.Workspace;
        if (nativeSessionCatalogsEnabled is not null)
            parameters["nativeSessionCatalogsEnabled"] = nativeSessionCatalogsEnabled.Value;
        if (choice.Kind is GatewayAiSetupChoiceKind.Auth or GatewayAiSetupChoiceKind.Prepare)
            parameters["authChoice"] = choice.Id;
        else if (choice.Kind == GatewayAiSetupChoiceKind.ManualProvider)
        {
            parameters["kind"] = "api-key";
            parameters["authChoice"] = choice.Id;
            parameters["apiKey"] = apiKey;
        }
        else
        {
            parameters["kind"] = choice.Id;
            // Older direct-activation schemas reject modelRef. The interactive
            // method advertisement is the upstream client's exact-model capability.
            if (SupportsInteractiveActivation)
                parameters["modelRef"] = choice.ModelRef;
        }

        SessionId = method.EndsWith(".start", StringComparison.Ordinal) ? Guid.NewGuid().ToString() : null;
        if (SessionId is not null)
            parameters["sessionId"] = SessionId;
        _before = Detection;
        _completionIntent = choice is { Kind: GatewayAiSetupChoiceKind.Candidate, Id: "existing-model" }
            ? SetupCompletionIntent.Dashboard : SetupCompletionIntent.CustodianOnboarding;
        _requiresManagedLocalAi = false;
        _expectedModel = choice.ModelRef;
        _attemptGeneration = transport.Generation;
        _hasActivationReceipt = false;
        GatewayRestartRequired = false;
        Wizard = null;
        Phase = GatewayAiSetupPhase.Uncertain;
        if (SessionId is not null)
        {
            var result = await ExecuteAsync<GatewayAiSetupWizardResult>(method, parameters, 1_200_000, ct);
            if (result.SessionId != SessionId)
                throw new InvalidDataException("The gateway returned a different setup session.");
            Apply(result);
        }
        else
        {
            var result = await ExecuteAsync<GatewayAiSetupActivation>(method, parameters, 1_200_000, ct);
            ApplyActivation(result);
        }
    }

    /// <summary>Polls without replaying an answer, including after a lost start reply.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        EnsureAuthorized();
        if (SessionId is null)
            throw new InvalidOperationException("There is no owned setup session to refresh.");
        if (Phase is not (GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain))
            throw new InvalidOperationException("The setup session is already settled.");
        if (transport.Generation != _attemptGeneration && _expectedModel is not null)
        {
            Phase = GatewayAiSetupPhase.Uncertain;
            var verification = await VerifyAsync(ct);
            if (!verification.Ok)
                throw new InvalidOperationException("The reconnected gateway could not verify the selected model.");
            return;
        }
        var result = await ExecuteAsync<GatewayAiSetupWizardResult>("wizard.next", new { sessionId = SessionId }, 1_200_000, ct);
        Apply(result);
    }

    public async Task NextAsync(string stepId, JsonElement? value = null, CancellationToken ct = default)
    {
        EnsureAuthorized();
        if (Phase != GatewayAiSetupPhase.Running || Wizard?.Step is not { } step || step.Id != stepId)
            throw new InvalidOperationException("This answer does not belong to the visible setup step.");
        if (step.Type == "progress" || step.Executor == "gateway")
            throw new InvalidOperationException("Gateway-owned steps must be refreshed, not answered.");
        var answer = new Dictionary<string, object?> { ["stepId"] = stepId };
        if (value is not null)
            answer["value"] = value.Value;
        Phase = GatewayAiSetupPhase.Uncertain;
        var result = await ExecuteAsync<GatewayAiSetupWizardResult>("wizard.next",
            new { sessionId = SessionId, answer }, 1_200_000, ct);
        Apply(result);
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        EnsureAuthorized();
        if (SessionId is null)
            throw new InvalidOperationException("The activation outcome must be reconciled; it has no cancellable wizard.");
        EnsureMethod("wizard.cancel");
        var generation = transport.Generation;
        var operation = ++_operation;
        Phase = GatewayAiSetupPhase.Uncertain;
        var payload = await transport.RequestAsync("wizard.cancel", new { sessionId = SessionId }, 15_000, ct);
        EnsureCurrent(operation, generation);
        var status = payload.GetProperty("status").GetString();
        // Cancellation is locked during persistent effects. A running/done/error
        // status is not proof of rollback, even when wizard.cancel returned OK.
        if (status == "cancelled")
        {
            Phase = GatewayAiSetupPhase.Cancelled;
            SessionId = null;
            Wizard = null;
        }
    }

    /// <summary>Explicitly verifies an already selected configured route, without activation.</summary>
    public async Task<GatewayAiSetupVerification> VerifyConfiguredAsync(string expectedModelRef, CancellationToken ct = default)
    {
        EnsureSettled();
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelRef);
        if (expectedConfiguredModelRef is not null && expectedModelRef != expectedConfiguredModelRef)
            throw new InvalidOperationException("This setup session is bound to a different configured model.");
        EnsureAuthorized();
        _expectedModel = expectedModelRef;
        _completionIntent = _configuredCompletionIntent;
        _requiresManagedLocalAi = requiresManagedLocalAi;
        var result = await VerifyRouteAsync(expectedModelRef, ct);
        Phase = result.Ok ? GatewayAiSetupPhase.Verified : GatewayAiSetupPhase.Rejected;
        return result;
    }

    /// <summary>
    /// Reconciles against the same gateway authority and exact model. An uncertain
    /// start needs a changed persisted route plus a new handshake; detecting an
    /// already working old model cannot prove that the attempted write settled.
    /// </summary>
    public async Task<GatewayAiSetupVerification> VerifyAsync(CancellationToken ct = default)
    {
        EnsureAuthorized();
        if (!RequiresReconciliation || string.IsNullOrWhiteSpace(_expectedModel))
            throw new InvalidOperationException("No exact activation outcome is available. Refresh or cancel the owned wizard.");
        if (GatewayRestartRequired && transport.Generation == _attemptGeneration)
            throw new InvalidOperationException("The saved model requires a gateway restart. Refresh after the gateway reconnects.");
        var after = await ExecuteAsync<GatewayAiSetupDetection>("openclaw.setup.detect", RouteParams(), 40_000, ct);
        ValidateDetection(after);
        if (!after.SetupComplete || after.ConfiguredModel != _expectedModel ||
            (!_hasActivationReceipt && (transport.Generation == _attemptGeneration ||
                _before is null || (_before.SetupComplete && _before.ConfiguredModel == _expectedModel))))
            throw new InvalidOperationException("The activation outcome is still uncertain. Do not start another setup operation.");
        var result = await VerifyRouteAsync(_expectedModel, ct);
        if (result.Ok)
        {
            Phase = GatewayAiSetupPhase.Verified;
            SessionId = null;
        }
        return result;
    }

    private async Task<GatewayAiSetupVerification> VerifyRouteAsync(string expected, CancellationToken ct)
    {
        var generation = transport.Generation;
        VerifiedModelRef = null;
        _verifiedGeneration = 0;
        var result = await ExecuteAsync<GatewayAiSetupVerification>("openclaw.setup.verify", RouteParams(), 120_000, ct);
        ct.ThrowIfCancellationRequested();
        EnsureAuthorized();
        if (transport.Generation != generation)
            throw new InvalidOperationException("The Gateway reconnected during model verification.");
        if (result.Ok && (result.ModelRef != expected || result.LatencyMs is null || result.ModelTarget is not null))
            throw new InvalidDataException("Verification did not confirm the selected model.");
        if (!result.Ok && (string.IsNullOrWhiteSpace(result.Status) || string.IsNullOrWhiteSpace(result.Error)))
            throw new InvalidDataException("The gateway returned an incomplete verification failure.");
        VerifiedModelRef = result.Ok ? result.ModelRef : null;
        _verifiedGeneration = result.Ok ? generation : 0;
        return result;
    }

    private void Apply(GatewayAiSetupWizardResult result)
    {
        Phase = GatewayAiSetupPhase.Uncertain;
        Wizard = result;
        if (!result.Done)
        {
            if (result.Status is not (null or "running") || result.ModelActivation is not null ||
                result.ActivationRejection is not null || result.PreparedModelRef is not null)
                throw new InvalidDataException("The gateway returned inconsistent setup progress.");
            if (result.Step is { } step && (string.IsNullOrWhiteSpace(step.Id) ||
                step.Type is not ("note" or "text" or "select" or "multiselect" or "confirm" or "progress" or "action") ||
                step.Executor is not (null or "client" or "gateway")))
                throw new InvalidDataException("The gateway returned an unsupported wizard step.");
            Phase = GatewayAiSetupPhase.Running;
            return;
        }
        if (result.Status == "done" && result.ModelActivation is { } activation &&
            !string.IsNullOrWhiteSpace(activation.ModelRef) && result.ActivationRejection is null &&
            result.PreparedModelRef is null && string.IsNullOrEmpty(result.Error))
        {
            AcceptActivation(activation);
            return;
        }
        if (result.Status == "done" && result.PreparedModelRef is { Length: > 0 } &&
            Selection?.Kind == GatewayAiSetupChoiceKind.Prepare && result.ModelActivation is null &&
            result.ActivationRejection is null && string.IsNullOrEmpty(result.Error))
        {
            Phase = GatewayAiSetupPhase.Prepared;
            SessionId = null;
            return;
        }
        if (result.Status == "cancelled" && result.ModelActivation is null &&
            result.ActivationRejection is null && result.PreparedModelRef is null)
        {
            Phase = GatewayAiSetupPhase.Cancelled;
            SessionId = null;
            return;
        }
        if (result.Status == "error" && result.ModelActivation is null && result.PreparedModelRef is null &&
            IsRejection(result.ActivationRejection))
        {
            Phase = GatewayAiSetupPhase.Rejected;
            SessionId = null;
            return;
        }
        Phase = GatewayAiSetupPhase.Uncertain;
        throw new InvalidDataException("Setup ended without an authoritative activation outcome.");
    }

    private void ApplyActivation(GatewayAiSetupActivation result)
    {
        if (result.Ok && !string.IsNullOrWhiteSpace(result.ModelRef))
            AcceptActivation(new(result.ModelRef, result.GatewayRestartRequired, result.ModelTarget));
        else if (!result.Ok && IsRejection(new(result.Disposition ?? "", result.Status ?? "")))
            Phase = GatewayAiSetupPhase.Rejected;
        else
            throw new InvalidDataException("The gateway did not confirm an activation outcome.");
    }

    private void AcceptActivation(GatewayAiSetupModelActivation activation)
    {
        if (activation.ModelTarget is not null)
            throw new InvalidDataException("The gateway activated a utility model, not the selected main assistant.");
        if (_expectedModel is not null && _expectedModel != activation.ModelRef)
            throw new InvalidDataException("The gateway activated a different model than the selected route.");
        _expectedModel = activation.ModelRef;
        _hasActivationReceipt = true;
        GatewayRestartRequired = activation.GatewayRestartRequired;
        // Even a live activation receipt needs verification on the current route.
        Phase = GatewayAiSetupPhase.VerificationRequired;
        SessionId = null;
    }

    private static bool IsRejection(GatewayAiSetupActivationRejection? rejection) =>
        rejection is { Disposition: "rejected-before-promotion" } &&
        rejection.Status is "auth" or "rate_limit" or "billing" or "timeout" or "format" or "unavailable" or "unknown";

    private async Task<T> ExecuteAsync<T>(string method, object parameters, int timeoutMs, CancellationToken ct)
    {
        if (_busy)
            throw new InvalidOperationException("A setup request is already running.");
        EnsureAuthorized();
        EnsureMethod(method);
        ct.ThrowIfCancellationRequested();
        var operation = ++_operation;
        var generation = transport.Generation;
        _busy = true;
        try
        {
            var payload = await transport.RequestAsync(method, parameters, timeoutMs, ct);
            ct.ThrowIfCancellationRequested();
            EnsureCurrent(operation, generation);
            return payload.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("The gateway returned an empty setup result.");
        }
        finally { _busy = false; }
    }

    private void EnsureCurrent(long operation, long generation)
    {
        if (operation != _operation || generation != transport.Generation || transport.Route != _route)
            throw new InvalidOperationException("The setup response belongs to an obsolete gateway connection or operation.");
        EnsureAuthorized();
    }

    private void EnsureAuthorized()
    {
        if (transport.Route != _route)
            throw new InvalidOperationException("The gateway or agent changed. This setup session cannot be reused.");
        if (!transport.IsConnected || !transport.OperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("AI setup requires a connected operator with operator.admin permission.");
    }

    private void EnsureSettled()
    {
        if (_busy || Phase is GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.VerificationRequired)
            throw new InvalidOperationException("Finish, cancel, or reconcile the current setup operation first.");
    }

    private void EnsureChoicesAllowed()
    {
        if (expectedConfiguredModelRef is not null)
            throw new InvalidOperationException("This setup session only verifies its selected configured model.");
    }

    private void EnsureMethod(string method)
    {
        if (!HasMethod(method))
            throw new NotSupportedException("The gateway does not advertise " + method + ".");
    }

    private GatewayAiSetupDetection RequireDetection() =>
        Detection ?? throw new InvalidOperationException("Detect AI setup choices first.");

    private Dictionary<string, object?> RouteParams()
    {
        var parameters = new Dictionary<string, object?>();
        if (_route.AgentId is not null)
            parameters["agentId"] = _route.AgentId;
        return parameters;
    }

    private static void ValidateDetection(GatewayAiSetupDetection detection)
    {
        if (detection.Candidates is null || detection.ManualProviders is null || string.IsNullOrWhiteSpace(detection.Workspace) ||
            detection.Candidates.Any(c => string.IsNullOrWhiteSpace(c.Kind) || string.IsNullOrWhiteSpace(c.ModelRef) || string.IsNullOrWhiteSpace(c.Label)) ||
            detection.ManualProviders.Concat(detection.AuthOptions).Concat(detection.PrepareOptions)
                .Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Label)))
            throw new InvalidDataException("The gateway returned incomplete AI setup choices.");
    }
}
