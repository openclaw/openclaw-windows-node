using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawTray.Services;

/// <summary>
/// Keeps the app-owned gateway from routing to a listener while the native
/// llama-server endpoint is absent, changing, or not owned by this companion.
/// </summary>
internal sealed class LocalAiGatewayProviderCoordinator : ILocalAiEndpointLifecycle
{
    private const int MaximumConfigBytes = 1024 * 1024;

    private readonly ILocalAiGatewayConfigurationTransport? _configuration;
    private readonly ILocalAiGatewayAtomicConfigurationTransport? _atomicConfiguration;
    private readonly Func<string>? _getApiKey;
    private readonly IOpenClawLogger _logger;
    private readonly Func<string, string>? _getString;

    public LocalAiGatewayProviderCoordinator(
        IWslCommandRunner commands,
        ILocalAiGatewayDistroResolver distroResolver,
        IOpenClawLogger logger,
        Func<string, string>? getString = null)
        : this(new WslLocalAiGatewayConfigurationTransport(
            commands ?? throw new ArgumentNullException(nameof(commands)),
            distroResolver ?? throw new ArgumentNullException(nameof(distroResolver))), logger, getString)
    {
    }

    public LocalAiGatewayProviderCoordinator(
        ILocalAiGatewayConfigurationTransport configuration,
        IOpenClawLogger logger,
        Func<string, string>? getString = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getString = getString;
    }

    public LocalAiGatewayProviderCoordinator(
        ILocalAiGatewayAtomicConfigurationTransport configuration,
        Func<string> getApiKey,
        IOpenClawLogger logger,
        Func<string, string>? getString = null)
    {
        _atomicConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _getApiKey = getApiKey ?? throw new ArgumentNullException(nameof(getApiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getString = getString;
    }

    public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        return QuiesceCoreAsync(install, reason, cancellationToken);
    }

    private async Task<LocalAiEndpointLifecycleResult> QuiesceCoreAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason,
        CancellationToken cancellationToken)
    {
        GatewayCapture current = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI gateway route could not be inspected.");

        string managedPrimary;
        try
        {
            managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
            LocalAiGatewayProviderDefinition.ValidateFallbackModel(
                install.Manifest.GatewayFallbackModel);
            if (current.ProviderExists)
            {
                _ = BuildProvider(install, ProviderShape.Current);
                if (!MatchesProvider(current.ProviderJson!, install, ProviderShape.Current) &&
                    !MatchesProvider(current.ProviderJson!, install, ProviderShape.Legacy))
                {
                    return Failed("The llamacpp provider was changed outside the companion; preserving it and refusing to cycle the managed endpoint.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }

        bool primaryIsManaged = current.PrimaryExists &&
            string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal);
        if (current.PrimaryExists &&
            !primaryIsManaged &&
            current.PrimaryModel!.StartsWith("llamacpp/", StringComparison.OrdinalIgnoreCase))
        {
            return Failed("The llamacpp primary model was changed outside the companion; preserving it and refusing to cycle the managed endpoint.");
        }

        string? expectedPrimary = current.PrimaryModel;
        // Withdrawal never waits on the provider-shape check: the runtime also
        // withdraws here to take the route off an untrusted or conflicting listener.
        bool retainManagedPrimary = reason == LocalAiQuiesceReason.EndpointCycle;
        if (_atomicConfiguration is not null)
        {
            if (primaryIsManaged && !retainManagedPrimary)
                expectedPrimary = install.Manifest.GatewayFallbackModel;
            var patch = new JsonObject();
            if (current.ProviderExists)
                patch["models"] = new JsonObject { ["providers"] = new JsonObject { ["llamacpp"] = null } };
            if (primaryIsManaged && !retainManagedPrimary)
                patch["agents"] = new JsonObject { ["defaults"] = new JsonObject
                {
                    ["model"] = new JsonObject { ["primary"] = expectedPrimary },
                } };
            if (patch.Count > 0)
            {
                var applied = await ApplyAtomicAsync(current, patch, cancellationToken).ConfigureAwait(false);
                if (!applied.Success) return applied;
            }
            return await VerifyQuiescedAsync(expectedPrimary, cancellationToken).ConfigureAwait(false);
        }
        if (primaryIsManaged && !retainManagedPrimary)
        {
            expectedPrimary = install.Manifest.GatewayFallbackModel;
            LocalAiEndpointLifecycleResult primaryResult = expectedPrimary is null
                ? await UnsetAsync(LocalAiGatewayProviderDefinition.PrimaryModelPath, cancellationToken)
                    .ConfigureAwait(false)
                : await SetPrimaryAsync(expectedPrimary, cancellationToken).ConfigureAwait(false);
            if (!primaryResult.Success)
                return primaryResult;
        }

        if (current.ProviderExists)
        {
            LocalAiEndpointLifecycleResult providerResult = await UnsetAsync(
                    LocalAiGatewayProviderDefinition.ProviderPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!providerResult.Success)
                return providerResult;
        }

        LocalAiEndpointLifecycleResult verified = await VerifyQuiescedAsync(expectedPrimary, cancellationToken)
            .ConfigureAwait(false);
        if (!verified.Success) return verified;

        // A prior Companion process may have removed the saved route without
        // confirming its restart. Even when disk lacks the provider, require a confirmed
        // restart before the runtime releases its owned listener.
        LocalAiGatewayCommandOutcome restarted = await _configuration!.RunAsync(
            ["gateway", "restart"], cancellationToken).ConfigureAwait(false);
        if (!restarted.Routed || !restarted.Result!.Success)
            return Failed("The managed Local AI route was removed from Gateway configuration, but the Gateway restart was not confirmed. Keep the listener running until its active route is reconciled.");
        return await VerifyQuiescedAsync(expectedPrimary, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalAiEndpointLifecycleResult> VerifyQuiescedAsync(
        string? expectedPrimary, CancellationToken cancellationToken)
    {
        GatewayCapture verified = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || verified.ProviderExists ||
            verified.PrimaryExists != (expectedPrimary is not null) ||
            (expectedPrimary is not null &&
                !string.Equals(verified.PrimaryModel, expectedPrimary, StringComparison.Ordinal)))
        {
            return Failed("The managed Local AI gateway route remained active after it was disabled.");
        }
        return LocalAiEndpointLifecycleResult.Ok();
    }

    public async Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        try
        {
            _ = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }

        GatewayCapture current = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        LocalAiEndpointLifecycleResult admission = CheckPublication(current, install);
        if (!admission.Success)
            return admission;
        // Admission accepted the current shape or the exact pre-effort shape.
        if (current.ProviderExists && MatchesProvider(current.ProviderJson!, install, ProviderShape.Current))
            return admission;
        // Decide what the installed Gateway accepts before anything is written or withdrawn.
        (LocalAiEndpointLifecycleResult decided, ProviderShape shape) = await DecideShapeAsync(install, cancellationToken)
            .ConfigureAwait(false);
        if (!decided.Success)
            return decided;
        if (current.ProviderExists)
        {
            // A Gateway that rejects effort metadata keeps its pre-effort route as is.
            if (shape == ProviderShape.Legacy)
                return admission;
            // Native endpoint recovery keeps a verified live route, so only the
            // owned model array of a pre-effort provider is upgraded in place.
            if (_atomicConfiguration is not null)
                return await MigrateLegacyProviderAsync(current, install, cancellationToken).ConfigureAwait(false);
            // WSL always quiesces before publishing, so a pre-effort provider is
            // withdrawn like an endpoint cycle and published from scratch.
            LocalAiEndpointLifecycleResult withdrawn = await QuiesceCoreAsync(
                install, LocalAiQuiesceReason.EndpointCycle, cancellationToken).ConfigureAwait(false);
            if (!withdrawn.Success)
                return withdrawn;
            current = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
            admission = CheckPublication(current, install);
            if (!admission.Success)
                return admission;
            if (current.ProviderExists)
                return Failed("The Local AI gateway route changed outside the companion; preserving it instead of publishing the managed endpoint.");
        }

        if (_atomicConfiguration is not null)
        {
            var defaults = new JsonObject
            {
                ["model"] = new JsonObject { ["primary"] = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) },
            };
            // A configured allowlist is additive. Never send the whole redacted config
            // back, and never replace unrelated providers, models, aliases or credentials.
            if (TryGetPath(current.Snapshot!.Config, ["agents", "defaults", "models"], out var allowlist))
            {
                if (allowlist.ValueKind != JsonValueKind.Object)
                    return Failed("The Gateway model allowlist is invalid; no Local AI configuration was changed.");
                var model = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
                if (!allowlist.TryGetProperty(model, out _))
                    defaults["models"] = new JsonObject { [model] = new JsonObject() };
            }
            var patch = new JsonObject
            {
                ["models"] = new JsonObject { ["providers"] = new JsonObject
                    { ["llamacpp"] = JsonNode.Parse(BuildProvider(install, shape)) } },
                ["agents"] = new JsonObject { ["defaults"] = defaults },
            };
            var result = await ApplyAtomicAsync(current, patch, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return result;
            return await VerifyPublishedAsync(install, shape, CancellationToken.None).ConfigureAwait(false);
        }

        string batch = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(
            install, legacy: shape == ProviderShape.Legacy);
        LocalAiGatewayCommandOutcome applied = await _configuration!.ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        if (!applied.Routed)
            return Failed(applied.Detail!);
        if (!applied.Result!.Success)
        {
            LocalAiEndpointLifecycleResult cleanup = await QuiesceAsync(install, LocalAiQuiesceReason.Teardown, cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The verified Local AI route could not be published to the app-owned gateway.",
                cleanup);
        }

        // WSL config writes persist the route before the running Gateway adopts it.
        // Keep the runtime in Starting until the original Gateway has restarted.
        var restarted = await _configuration.RunAsync(["gateway", "restart"], cancellationToken).ConfigureAwait(false);
        if (!restarted.Routed || !restarted.Result!.Success)
        {
            _logger.Warn($"Managed Local AI Gateway restart failed (routed={restarted.Routed}, " +
                $"timedOut={restarted.Result?.TimedOut}, indeterminate={restarted.Result?.OutcomeIndeterminate}, " +
                $"exitCode={restarted.Result?.ExitCode}).");
            return Failed("The Local AI route was saved, but the app-owned Gateway did not restart. Check the WSL Gateway before retrying Local AI.");
        }

        return await VerifyPublishedAsync(install, shape, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalAiEndpointLifecycleResult> VerifyPublishedAsync(
        LocalAiResolvedInstall install, ProviderShape shape, CancellationToken cancellationToken)
    {
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        GatewayCapture verified = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || !verified.ProviderExists ||
            !MatchesProvider(verified.ProviderJson!, install, shape) ||
            !verified.PrimaryExists ||
            !string.Equals(verified.PrimaryModel, managedPrimary, StringComparison.Ordinal))
        {
            if (_atomicConfiguration is not null)
                return Failed("Local AI publication could not be verified. Reconnect the original Gateway to reconcile its route; no unguarded rollback was attempted.");
            LocalAiEndpointLifecycleResult cleanup = await QuiesceAsync(install, LocalAiQuiesceReason.Teardown, cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The app-owned gateway did not retain the verified Local AI route.",
                cleanup);
        }
        return LocalAiEndpointLifecycleResult.Ok();
    }

    public async Task<LocalAiEndpointLifecycleResult> ValidatePublicationAsync(
        LocalAiResolvedInstall install, CancellationToken ct = default) =>
        CheckPublication(await CaptureGatewayAsync(ct).ConfigureAwait(false), install);

    private LocalAiEndpointLifecycleResult CheckPublication(GatewayCapture current, LocalAiResolvedInstall install)
    {
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI gateway route could not be inspected.");
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        if (current.ProviderExists)
        {
            return (MatchesProvider(current.ProviderJson!, install, ProviderShape.Current) ||
                    MatchesProvider(current.ProviderJson!, install, ProviderShape.Legacy)) &&
                   current.PrimaryExists &&
                   string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal)
                ? LocalAiEndpointLifecycleResult.Ok()
                : Failed("The Local AI gateway route changed outside the companion; preserving it instead of publishing the managed endpoint.");
        }

        string? fallbackModel = install.Manifest.GatewayFallbackModel;
        bool retainedManagedPrimary = current.PrimaryExists &&
            string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal);
        if (!retainedManagedPrimary &&
            (current.PrimaryExists != (fallbackModel is not null) ||
                (fallbackModel is not null &&
                    !string.Equals(current.PrimaryModel, fallbackModel, StringComparison.Ordinal))))
        {
            return Failed("The gateway primary model changed while Local AI was stopped; preserving it instead of overwriting it.");
        }

        return LocalAiEndpointLifecycleResult.Ok();
    }

    private async Task<LocalAiEndpointLifecycleResult> MigrateLegacyProviderAsync(
        GatewayCapture current, LocalAiResolvedInstall install, CancellationToken ct)
    {
        // Change the owned model array only. The provider readback may redact
        // the API key, so replaying the entire provider would destroy a secret.
        string models = LocalAiGatewayProviderDefinition.BuildProviderModelsJson(install);
        var patch = new JsonObject { ["models"] = new JsonObject
        {
            ["providers"] = new JsonObject { ["llamacpp"] = new JsonObject
            {
                ["models"] = JsonNode.Parse(models),
            } },
        } };
        LocalAiEndpointLifecycleResult applied = await ApplyAtomicAsync(current, patch, ct).ConfigureAwait(false);
        if (!applied.Success) return applied;
        return await VerifyMigratedProviderAsync(install).ConfigureAwait(false);
    }

    private async Task<LocalAiEndpointLifecycleResult> VerifyMigratedProviderAsync(LocalAiResolvedInstall install)
    {
        // The migration write has already landed. Read it back without the
        // caller's token, and never tear down the route that was just upgraded.
        GatewayCapture verified = await CaptureGatewayAsync(CancellationToken.None).ConfigureAwait(false);
        return verified.Success && verified.ProviderExists &&
               MatchesProvider(verified.ProviderJson!, install, ProviderShape.Current) &&
               verified.PrimaryExists &&
               string.Equals(verified.PrimaryModel,
                   LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), StringComparison.Ordinal)
            ? LocalAiEndpointLifecycleResult.Ok()
            : Failed(Localized("LocalAi_GatewaySavedRouteVerification", "The saved Local AI route could not be verified. Preserve the endpoint and reconcile the original Gateway."));
    }

    private LocalAiEndpointLifecycleResult PublicationFailed(
        string detail,
        LocalAiEndpointLifecycleResult cleanup) => cleanup.Success
            ? Failed($"{detail} The just-written route was removed.")
            : Failed($"{detail} Cleanup also failed: {cleanup.Detail}");

    private async Task<GatewayCapture> CaptureGatewayAsync(CancellationToken cancellationToken)
    {
        if (_atomicConfiguration is not null)
            return await CaptureAtomicAsync(cancellationToken).ConfigureAwait(false);
        SettingCapture provider = await CaptureSettingAsync(
            LocalAiGatewayProviderDefinition.ProviderPath,
            cancellationToken).ConfigureAwait(false);
        if (!provider.Success)
            return new(false, false, null, false, null, provider.Detail);

        SettingCapture primary = await CaptureSettingAsync(
            LocalAiGatewayProviderDefinition.PrimaryModelPath,
            cancellationToken).ConfigureAwait(false);
        if (!primary.Success)
            return new(false, false, null, false, null, primary.Detail);

        string? providerJson = null;
        if (provider.Exists)
        {
            try
            {
                using JsonDocument document = ParseBounded(provider.Json!);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new(false, false, null, false, null, "The managed llamacpp provider has an invalid shape.");
                providerJson = document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                return new(false, false, null, false, null, "The managed llamacpp provider is not valid JSON.");
            }
        }

        string? primaryModel = null;
        if (primary.Exists)
        {
            try
            {
                using JsonDocument document = ParseBounded(primary.Json!);
                if (document.RootElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(primaryModel = document.RootElement.GetString()) ||
                    primaryModel.Length > 512 ||
                    primaryModel.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                {
                    return new(false, false, null, false, null, "The gateway primary model has an invalid shape.");
                }
            }
            catch (JsonException)
            {
                return new(false, false, null, false, null, "The gateway primary model is not valid JSON.");
            }
        }

        return new(true, provider.Exists, providerJson, primary.Exists, primaryModel, null);
    }

    private async Task<SettingCapture> CaptureSettingAsync(
        string path,
        CancellationToken cancellationToken)
    {
        LocalAiGatewayCommandOutcome routed = await _configuration!.RunAsync(
            ["config", "get", path, "--json"], cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return new(false, false, null, routed.Detail);

        LocalAiGatewayCommandResult direct = routed.Result!;
        if (direct.TimedOut || direct.OutcomeIndeterminate)
            return new(false, false, null, $"The app-owned gateway setting '{path}' could not be read reliably.");
        if (direct.Success)
            return new(true, true, direct.StandardOutput, null);
        return IsUnsetSetting(direct, path)
            ? new(true, false, null, null)
            : new(false, false, null, $"The app-owned gateway setting '{path}' could not be read.");
    }

    private static bool IsUnsetSetting(LocalAiGatewayCommandResult result, string path) =>
        result.StandardError.Length <= 64 * 1024 &&
        GatewayConfigCliCompatibility.IsUnsetError(
            result.ExitCode, result.StandardOutput, result.StandardError, path);

    private async Task<LocalAiEndpointLifecycleResult> SetPrimaryAsync(
        string model,
        CancellationToken cancellationToken)
    {
        LocalAiGatewayProviderDefinition.ValidateFallbackModel(model);
        string batch = JsonSerializer.Serialize(new[]
        {
            new { path = LocalAiGatewayProviderDefinition.PrimaryModelPath, value = model },
        });
        LocalAiGatewayCommandOutcome routed = await _configuration!.ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return Failed(routed.Detail!);
        return routed.Result!.Success
            ? LocalAiEndpointLifecycleResult.Ok()
            : Failed("The prior gateway primary model could not be restored before the Local AI endpoint changed.");
    }

    private async Task<LocalAiEndpointLifecycleResult> UnsetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        LocalAiGatewayCommandOutcome routed = await _configuration!.RunAsync(
            ["config", "unset", path], cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return Failed(routed.Detail!);
        return routed.Result!.Success
            ? LocalAiEndpointLifecycleResult.Ok()
            : Failed($"The managed gateway setting '{path}' could not be disabled before the Local AI endpoint changed.");
    }

    private static JsonDocument ParseBounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumConfigBytes)
            throw new JsonException("The gateway configuration is too large.");
        return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private string Localized(string key, string fallback)
    {
        string? value = _getString?.Invoke(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private LocalAiEndpointLifecycleResult Failed(string detail)
    {
        _logger.Warn(detail);
        return LocalAiEndpointLifecycleResult.Failed(detail);
    }

    private string BuildProvider(LocalAiResolvedInstall install, ProviderShape shape)
    {
        string apiKey = _getApiKey?.Invoke() ?? "llama-local";
        return shape == ProviderShape.Legacy
            ? LocalAiGatewayProviderDefinition.BuildLegacyProviderJson(install, apiKey)
            : LocalAiGatewayProviderDefinition.BuildProviderJson(install, apiKey);
    }

    private bool MatchesProvider(string json, LocalAiResolvedInstall install, ProviderShape shape) =>
        shape == ProviderShape.Legacy
            ? LocalAiGatewayProviderDefinition.MatchesLegacyProviderJson(json, install, _getApiKey?.Invoke())
            : LocalAiGatewayProviderDefinition.MatchesProviderJson(json, install, _getApiKey?.Invoke());

    // Decides, once per publication and before anything is written or withdrawn,
    // which provider shape the installed Gateway accepts. Nothing is cached: the
    // Gateway may be upgraded or replaced between publications.
    private async Task<(LocalAiEndpointLifecycleResult Result, ProviderShape Shape)> DecideShapeAsync(
        LocalAiResolvedInstall install, CancellationToken ct)
    {
        bool effortMetadata;
        if (_atomicConfiguration is not null)
        {
            try
            {
                effortMetadata = await _atomicConfiguration.SupportsEffortMetadataAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // RPC errors can include authored config. Never log their payload.
                return (Failed(ShapeCheckFailed()), default);
            }
        }
        else
        {
            // A dry-run validates against the installed schema without writing.
            // A rejection counts only when it repeats and the pre-effort entry
            // validates, so one failed CLI run cannot downgrade a working route.
            string fullBatch = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install);
            var full = await ValidateAsync(fullBatch, ct).ConfigureAwait(false);
            if (full.Failure is not null)
                return (full.Failure, default);
            if (!full.Accepted)
            {
                full = await ValidateAsync(fullBatch, ct).ConfigureAwait(false);
                if (full.Failure is not null)
                    return (full.Failure, default);
            }
            effortMetadata = full.Accepted;
            if (!effortMetadata)
            {
                var legacy = await ValidateAsync(
                    LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install, legacy: true), ct)
                    .ConfigureAwait(false);
                if (legacy.Failure is not null)
                    return (legacy.Failure, default);
                if (!legacy.Accepted)
                    return (Failed(Localized("LocalAi_GatewayProviderRejected",
                        "The Gateway rejected the Local AI provider configuration, so nothing was written. Update the OpenClaw Gateway and retry.")), default);
            }
        }
        if (!effortMetadata)
        {
            _logger.Warn("The Gateway does not accept Local AI effort metadata (it needs OpenClaw Gateway 2026.6.5 or later). " +
                "Publishing the earlier provider shape, so Effort Off cannot turn reasoning off on this Gateway.");
        }
        return (LocalAiEndpointLifecycleResult.Ok(), effortMetadata ? ProviderShape.Current : ProviderShape.Legacy);
    }

    private async Task<(bool Accepted, LocalAiEndpointLifecycleResult? Failure)> ValidateAsync(
        string batch, CancellationToken ct)
    {
        LocalAiGatewayCommandOutcome outcome = await _configuration!.ValidateBatchAsync(batch, ct).ConfigureAwait(false);
        if (!outcome.Routed)
            return (false, Failed(outcome.Detail!));
        if (outcome.Result!.TimedOut || outcome.Result.OutcomeIndeterminate)
            return (false, Failed(ShapeCheckFailed()));
        return (outcome.Result.Success, null);
    }

    private string ShapeCheckFailed() => Localized("LocalAi_GatewayProviderCheckFailed",
        "The Gateway could not confirm which Local AI configuration it accepts, so nothing was written. Reconnect the Gateway and retry.");

    private async Task<GatewayCapture> CaptureAtomicAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await _atomicConfiguration!.CaptureAsync(ct).ConfigureAwait(false);
            bool providerExists = TryGetPath(snapshot.Config, ["models", "providers", "llamacpp"], out var provider);
            bool primaryExists = TryGetPath(snapshot.Config, ["agents", "defaults", "model", "primary"], out var primary);
            if (providerExists && provider.ValueKind != JsonValueKind.Object ||
                primaryExists && (primary.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(primary.GetString()) ||
                    primary.GetString()!.Length > 512 ||
                    primary.GetString()!.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
                return new(false, false, null, false, null, "The bound Gateway configuration has an invalid Local AI route.");
            return new(true, providerExists, providerExists ? provider.GetRawText() : null,
                primaryExists, primaryExists ? primary.GetString() : null, null, snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // RPC errors can include authored config. Never log their payload or credentials.
            return new(false, false, null, false, null,
                "The bound Local AI Gateway could not be inspected. Reconnect it before changing the managed endpoint.");
        }
    }

    private async Task<LocalAiEndpointLifecycleResult> ApplyAtomicAsync(
        GatewayCapture current, JsonObject patch, CancellationToken ct)
    {
        try
        {
            // config.patch merges model arrays by id. Removing the provider also
            // removes that array, so authorize only this owned destructive path.
            string[] replacePaths = current.ProviderExists &&
                patch["models"]?["providers"] is JsonObject providers &&
                providers.ContainsKey("llamacpp")
                ? [LocalAiGatewayProviderDefinition.ProviderModelsPath] : [];
            await _atomicConfiguration!.ApplyAsync(current.Snapshot!,
                JsonSerializer.SerializeToElement(patch), ct, replacePaths).ConfigureAwait(false);
            return LocalAiEndpointLifecycleResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Failed("The guarded Local AI configuration change was not confirmed. Preserve the endpoint and reconnect the original Gateway to reconcile; do not retry an offline write.");
        }
    }

    private static bool TryGetPath(JsonElement root, string[] path, out JsonElement value)
    {
        value = root;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Gateway configuration path has an invalid shape.");
            if (!value.TryGetProperty(name, out value)) return false;
        }
        return true;
    }

    /// <summary>Current carries effort metadata; Legacy is the exact pre-effort provider.</summary>
    private enum ProviderShape { Current, Legacy }

    private sealed record SettingCapture(bool Success, bool Exists, string? Json, string? Detail);
    private sealed record GatewayCapture(
        bool Success,
        bool ProviderExists,
        string? ProviderJson,
        bool PrimaryExists,
        string? PrimaryModel,
        string? Detail,
        LocalAiGatewayConfigurationSnapshot? Snapshot = null);
}
