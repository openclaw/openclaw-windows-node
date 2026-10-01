using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// One endpoint, one durable Gateway owner. Native RPCs borrow an existing authorized
/// connection; WSL keeps its pinned command transport.
/// </summary>
internal sealed class LocalAiGatewayLifecycle(
    LocalAiPaths paths, string dataDirectory, Func<GatewayRegistry?> getRegistry,
    Func<GatewayConnectionManager?> getManager, ILocalAiEndpointLifecycle wsl,
    IOpenClawLogger logger) : ILocalAiEndpointLifecycle
{
    private readonly LocalAiNativeBindingStore _store = new(paths);
    private readonly LocalAiApiCredentialStore _credentials = new(paths);
    private NativeLocalAiGatewayTarget? _registeredTarget;
    private IGatewayAiSetupTransport? _registeredTransport;
    private int _resuming;
    private Task? _resumeTask;
    private sealed record EndpointRecovery(string GatewayId, string ModelRef, Uri Endpoint, string ConfigHash);
    private EndpointRecovery? _endpointRecovery;

    public bool IsNativeMode => _store.Exists ||
        getRegistry()?.GetActive()?.NativePackageFamilyName is not null;
    public bool HasNativeBinding => _store.Exists;
    public bool OwnsGateway(string id) => _store.Load()?.GatewayId == id;
    public string? GetApiKey() => _store.Exists ? _credentials.GetOrCreate() : null;
    public int? GetRecoveryPort(LocalAiResolvedInstall install) =>
        _endpointRecovery is { } recovery && recovery.Endpoint == install.Endpoint &&
        recovery.ModelRef == LocalAiGatewayProviderDefinition.BuildPrimaryModel(install)
            ? recovery.Endpoint.Port : null;
    public void EndEndpointRecovery() => _endpointRecovery = null;

    public async Task SetAutomaticRecoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var lease = await _store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (_store.Load() is { } binding)
            _store.Save(binding with { AutomaticRecoveryEnabled = enabled });
    }

    public async Task ForgetWithdrawnAsync(string gatewayId, CancellationToken ct)
    {
        await using var lease = await _store.AcquireAsync(ct).ConfigureAwait(false);
        var binding = _store.Load();
        if (binding?.GatewayId != gatewayId || binding.Pending)
            throw new InvalidOperationException("The native Local AI ownership receipt is not safe to release.");
        _store.Delete();
    }

    public async Task ReconcileVerifiedAsync(GatewayRecord record, IGatewayAiSetupTransport transport,
        LocalAiResolvedInstall install, CancellationToken ct)
    {
        await using var lease = await _store.AcquireAsync(ct).ConfigureAwait(false);
        var binding = _store.Load() ?? throw new InvalidOperationException("The Local AI ownership receipt is unavailable.");
        var target = NativeLocalAiGatewayTarget.Capture(record, transport.Route);
        RequireBinding(binding, target);
        var rpc = new LocalAiGatewayRpcConfigurationTransport(target, transport);
        var before = await rpc.CaptureAsync(ct).ConfigureAwait(false);
        var config = JsonNode.Parse(before.Config.GetRawText())!;
        var provider = config["models"]?["providers"]?["llamacpp"];
        if (binding.ModelRef != LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) ||
            config["agents"]?["defaults"]?["model"]?["primary"]?.GetValue<string>() != binding.ModelRef ||
            provider is null || !LocalAiGatewayProviderDefinition.MatchesProviderJson(
                provider.ToJsonString(), install, _credentials.GetOrCreate()))
            throw new InvalidOperationException("The saved Local AI provider or primary model was changed. No ownership was adopted.");
        // A redacted credential cannot establish ownership. Explicit verification
        // must perform real inference through the exact primary, without fallback.
        var verified = await new GatewayAiSetupClient(transport).VerifyConfiguredAsync(binding.ModelRef, ct)
            .ConfigureAwait(false);
        if (!verified.Ok)
            throw new InvalidOperationException("The native Gateway could not authenticate and infer with the owned Local AI model.");
        var after = await rpc.CaptureAsync(ct).ConfigureAwait(false);
        if (before.Hash != after.Hash)
            throw new InvalidOperationException("The Gateway configuration changed during Local AI verification.");
        _store.Save(binding with { ConfigHash = after.Hash, Pending = false });
    }

    public void Register(GatewayRecord record, IGatewayAiSetupTransport transport)
    {
        _registeredTarget = NativeLocalAiGatewayTarget.Capture(record, transport.Route);
        _registeredTransport = transport;
    }

    public void Release(IGatewayAiSetupTransport transport)
    {
        if (!ReferenceEquals(_registeredTransport, transport)) return;
        _endpointRecovery = null;
        _registeredTransport = null;
        _registeredTarget = null;
    }

    public void Attach(GatewayConnectionManager manager, ILocalAiRuntime runtime)
    {
        manager.StateChanged += (_, snapshot) =>
        {
            if (snapshot.OperatorState == RoleConnectionState.Connected && _store.Exists &&
                Interlocked.CompareExchange(ref _resuming, 1, 0) == 0)
                Volatile.Write(ref _resumeTask, ResumeAsync(runtime));
        };
    }

    public Task WaitForRuntimeAsync(GatewayAiSetupCompletion expected, ILocalAiRuntime runtime, CancellationToken ct)
    {
        var binding = _store.Load();
        if (binding is null || binding.GatewayId != expected.GatewayId || binding.ModelRef != expected.ModelRef)
            return Task.CompletedTask;
        return WaitForRecoveryAsync(expected.ModelRef, runtime, () => Volatile.Read(ref _resumeTask), ct);
    }

    internal static async Task WaitForRecoveryAsync(string expectedModelRef, ILocalAiRuntime runtime,
        Func<Task?> getRecoveryTask, CancellationToken ct)
    {
        // Only join recovery already admitted by the connection owner. Handoff
        // cannot replay a pending write or start a different Gateway's runtime.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (getRecoveryTask() is { IsCompleted: false } recovery)
            {
                await recovery.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (runtime.Snapshot.State == LocalAiRuntimeState.Healthy) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (runtime.Snapshot is not { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged } ready ||
            "llamacpp/" + ready.ModelId != expectedModelRef)
            throw new InvalidOperationException("The selected Local AI runtime is not ready after Gateway reconnection.");
    }

    internal async Task ResumeAsync(ILocalAiRuntime runtime)
    {
        try
        {
            var binding = _store.Load();
            if (binding is { AutomaticRecoveryEnabled: false })
                return;
            if (binding is null || binding.Pending || getRegistry()?.GetActive()?.Id != binding.GatewayId)
            {
                logger.Warn("Local AI automatic recovery is waiting for its original Gateway and a confirmed configuration revision.");
                return;
            }
            var snapshot = await runtime.ResumeAsync().ConfigureAwait(false);
            if (snapshot.State != LocalAiRuntimeState.Healthy)
                logger.Warn("The bound Local AI runtime needs attention after Gateway reconnection.");
        }
        catch (Exception ex) { logger.Warn($"Local AI recovery failed ({ex.GetType().Name})."); }
        finally { Interlocked.Exchange(ref _resuming, 0); }
    }

    public async Task PrepareAsync(LocalAiResolvedInstall install, CancellationToken ct)
    {
        _endpointRecovery = null;
        await using var lease = await _store.AcquireAsync(ct).ConfigureAwait(false);
        var target = _registeredTarget ??
            throw new LocalAiSelectionRejectedException("Reconnect the selected native Gateway before using Local AI.");
        var rpc = new LocalAiGatewayRpcConfigurationTransport(target, _registeredTransport!);
        var current = await rpc.CaptureAsync(ct).ConfigureAwait(false);
        var binding = _store.Load();
        if (binding is not null)
        {
            RequireBinding(binding, target);
            if (binding.Pending)
            {
                var pendingConfig = JsonNode.Parse(current.Config.GetRawText())!;
                var provider = pendingConfig["models"]?["providers"]?["llamacpp"];
                var primary = pendingConfig["agents"]?["defaults"]?["model"]?["primary"]?.GetValue<string>();
                if (current.Hash == binding.ConfigHash ||
                    provider is null && (primary == binding.PreviousPrimary || primary == binding.ModelRef))
                {
                    // The write did not land, or the observed endpoint is already withdrawn.
                    binding = binding with { Pending = false, ConfigHash = current.Hash };
                    _store.Save(binding);
                }
                else if (provider is not null && primary == binding.ModelRef && install.Endpoint is { } endpoint &&
                    binding.ModelRef == LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) &&
                    LocalAiGatewayProviderDefinition.MatchesProviderJson(
                        provider.ToJsonString(), install, _credentials.GetOrCreate()))
                {
                    // Explicit Use may restore only the same authenticated listener.
                    // No Gateway write is replayed; publication waits for real inference.
                    _endpointRecovery = new(binding.GatewayId, binding.ModelRef, endpoint, current.Hash);
                    return;
                }
            }
            if (binding.Pending || binding.ConfigHash != current.Hash)
                throw new LocalAiSelectionRejectedException(binding.Pending
                    ? PendingRecoveryDetail(binding, install)
                    : "The saved Local AI route needs reconciliation. Its Gateway configuration was changed.");
            if (binding.ModelRef != LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
                throw new LocalAiSelectionRejectedException("Withdraw the previous Local AI model before replacing its installation.");
            return;
        }
        var config = JsonNode.Parse(current.Config.GetRawText())!;
        if (config["models"]?["providers"]?["llamacpp"] is not null)
            throw new LocalAiSelectionRejectedException("An existing llamacpp provider is not owned by this installation. It has not been changed.");
        var previous = config["agents"]?["defaults"]?["model"]?["primary"]?.GetValue<string>();
        LocalAiGatewayProviderDefinition.ValidateFallbackModel(previous);
        var model = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        if (previous?.StartsWith("llamacpp/", StringComparison.OrdinalIgnoreCase) == true)
            throw new LocalAiSelectionRejectedException("The existing Local AI primary model has no ownership receipt.");
        var allowlist = config["agents"]?["defaults"]?["models"];
        if (allowlist is not null && allowlist is not JsonObject)
            throw new LocalAiSelectionRejectedException("The Gateway model allowlist is invalid.");
        _store.Save(new(target.GatewayId, target.Route.EndpointBinding!,
            target.Route.IdentityBinding!, model, previous, current.Hash,
            allowlist is JsonObject entries && !entries.ContainsKey(model)));
    }

    public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown, CancellationToken cancellationToken = default) =>
        _store.Exists ? RunNativeAsync(install, reason, cancellationToken) :
            wsl.QuiesceAsync(install, reason, cancellationToken);

    public Task<LocalAiEndpointLifecycleResult> PublishAsync(LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default) =>
        _store.Exists ? RunNativeAsync(install, null, cancellationToken) :
            wsl.PublishAsync(install, cancellationToken);

    private async Task<LocalAiEndpointLifecycleResult> RunNativeAsync(
        LocalAiResolvedInstall install, LocalAiQuiesceReason? reason, CancellationToken ct)
    {
        try
        {
            await using var lease = await _store.AcquireAsync(ct).ConfigureAwait(false);
            var binding = _store.Load();
            if (binding is null)
                return reason is not null ? LocalAiEndpointLifecycleResult.Ok() :
                    LocalAiEndpointLifecycleResult.Failed("Choose Use Local AI for the selected native Gateway before publishing its endpoint.");
            IGatewayAiSetupTransport transport;
            NativeLocalAiGatewayTarget target;
            if (_registeredTarget?.GatewayId == binding.GatewayId && _registeredTransport is { IsConnected: true } registered)
            {
                transport = registered;
                target = _registeredTarget;
            }
            else
            {
                var record = getRegistry()?.GetActive();
                if (record?.Id != binding.GatewayId || getManager() is not { } manager)
                    throw new InvalidOperationException("The original Local AI Gateway is not connected. Cleanup remains pending.");
                transport = await GatewayAiSetupTransport.BorrowNativeAsync(dataDirectory, manager,
                    binding.GatewayId, ct, binding.EndpointBinding).ConfigureAwait(false);
                target = NativeLocalAiGatewayTarget.Capture(record, transport.Route);
            }
            RequireBinding(binding, target);
            if (binding.ModelRef != LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
                throw new InvalidOperationException("The Local AI installation differs from its saved Gateway owner.");
            if (binding.Pending)
            {
                var rpc = new LocalAiGatewayRpcConfigurationTransport(target, transport);
                var current = await rpc.CaptureAsync(ct).ConfigureAwait(false);
                var config = JsonNode.Parse(current.Config.GetRawText())!;
                var provider = config["models"]?["providers"]?["llamacpp"];
                var primary = config["agents"]?["defaults"]?["model"]?["primary"]?.GetValue<string>();
                if (current.Hash == binding.ConfigHash ||
                    provider is null && (primary == binding.PreviousPrimary || primary == binding.ModelRef))
                {
                    binding = binding with { Pending = false, ConfigHash = current.Hash };
                    _store.Save(binding);
                }
                else if (_endpointRecovery is { } recovery && recovery.GatewayId == binding.GatewayId &&
                    recovery.Endpoint == install.Endpoint && recovery.ModelRef == binding.ModelRef &&
                    recovery.ConfigHash == current.Hash && reason == LocalAiQuiesceReason.EndpointCycle)
                    return LocalAiEndpointLifecycleResult.Ok();
                else if (_endpointRecovery is { } publication && publication.GatewayId == binding.GatewayId &&
                    publication.Endpoint == install.Endpoint && publication.ModelRef == binding.ModelRef &&
                    publication.ConfigHash == current.Hash && reason is null)
                {
                    var verification = await new GatewayAiSetupClient(transport)
                        .VerifyConfiguredAsync(binding.ModelRef, ct).ConfigureAwait(false);
                    var after = await rpc.CaptureAsync(ct).ConfigureAwait(false);
                    if (!verification.Ok || after.Hash != current.Hash)
                        throw new InvalidOperationException("The recovered endpoint could not be verified through the original Gateway.");
                    _store.Save(binding with { Pending = false, ConfigHash = after.Hash });
                    _endpointRecovery = null;
                    return LocalAiEndpointLifecycleResult.Ok();
                }
                else
                {
                    logger.Warn("An unconfirmed native Local AI write requires explicit endpoint recovery or original Gateway configuration inspection.");
                    return LocalAiEndpointLifecycleResult.Failed(PendingRecoveryDetail(binding, install));
                }
            }
            var guarded = new JournaledTransport(new(target, transport), _store, binding,
                reason == LocalAiQuiesceReason.Teardown);
            var coordinator = new LocalAiGatewayProviderCoordinator(guarded, _credentials.GetOrCreate, logger);
            var ownedInstall = install with { Manifest = install.Manifest with { GatewayFallbackModel = binding.PreviousPrimary } };
            return reason is { } withdrawal
                ? await coordinator.QuiesceAsync(ownedInstall, withdrawal, ct).ConfigureAwait(false)
                : await coordinator.PublishAsync(ownedInstall, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.Warn($"Native Local AI route requires reconciliation ({ex.GetType().Name}).");
            return LocalAiEndpointLifecycleResult.Failed(
                "The original native Gateway route could not be safely updated. Its saved ownership or configuration changed, or it is offline. Reconnect it and review Local AI before retrying.");
        }
    }

    private static string PendingRecoveryDetail(LocalAiNativeBinding binding, LocalAiResolvedInstall install) =>
        $"Gateway '{binding.GatewayId}' has an unconfirmed Local AI write. Restore its authenticated listener at " +
        $"'{install.Endpoint}' and retry Use Local AI. Otherwise, inspect the original Gateway configuration, " +
        $"remove models.providers.llamacpp, and restore agents.defaults.model.primary to " +
        $"{(binding.PreviousPrimary is null ? "unset" : $"'{binding.PreviousPrimary}'")} " +
        $"(or retain '{binding.ModelRef}' for cleanup). Keep the local ownership receipt.";

    private static void RequireBinding(LocalAiNativeBinding binding, NativeLocalAiGatewayTarget target)
    {
        if (binding.GatewayId != target.GatewayId || binding.EndpointBinding != target.Route.EndpointBinding ||
            binding.IdentityBinding != target.Route.IdentityBinding)
            throw new LocalAiSelectionRejectedException("The Local AI installation belongs to a different Gateway or device identity.");
    }

    private sealed class JournaledTransport(
        LocalAiGatewayRpcConfigurationTransport rpc, LocalAiNativeBindingStore store,
        LocalAiNativeBinding binding, bool teardown) : ILocalAiGatewayAtomicConfigurationTransport
    {
        private LocalAiNativeBinding _binding = binding;

        public async Task<LocalAiGatewayConfigurationSnapshot> CaptureAsync(CancellationToken ct)
        {
            var snapshot = await rpc.CaptureAsync(ct).ConfigureAwait(false);
            if (snapshot.Hash != _binding.ConfigHash)
                throw new InvalidOperationException("The bound Gateway configuration changed outside this Local AI operation.");
            return snapshot;
        }

        public async Task ApplyAsync(LocalAiGatewayConfigurationSnapshot expected, JsonElement patch,
            CancellationToken ct, IReadOnlyList<string>? replacePaths = null)
        {
            var outgoing = JsonNode.Parse(patch.GetRawText())!.AsObject();
            if (teardown && _binding.AddedAllowlistEntry)
            {
                var agents = outgoing["agents"] as JsonObject ?? new JsonObject();
                if (outgoing["agents"] is null) outgoing["agents"] = agents;
                var defaults = agents["defaults"] as JsonObject ?? new JsonObject();
                if (agents["defaults"] is null) agents["defaults"] = defaults;
                defaults["models"] = new JsonObject { [_binding.ModelRef] = null };
            }
            ct.ThrowIfCancellationRequested();
            rpc.BeforeMutationDispatch = () => store.Save(_binding with { Pending = true });
            try
            {
                await rpc.ApplyAsync(expected, JsonSerializer.SerializeToElement(outgoing),
                    ct, replacePaths).ConfigureAwait(false);
            }
            finally
            {
                // Acknowledged persistence is retained even if the connection then restarts.
                if (rpc.LastConfirmedHash is { } hash)
                {
                    _binding = _binding with { ConfigHash = hash, Pending = false };
                    store.Save(_binding);
                }
            }
        }
    }
}
