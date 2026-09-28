using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Owns a staged native profile and runtime while the shared WinUI wizard owns RPC
/// and rendering. Successful wizard completion or a validated optional-setup handoff
/// permits final config/health verification before publication.
/// </summary>
public sealed class NativeGatewaySetupSession(
    GatewayRegistry registry,
    NativeGatewaySetupDraft draft,
    GatewayRecord record,
    NativeGatewayPackage package,
    IReadOnlyDictionary<string, string> environment,
    INativeGatewaySetupHost host,
    INativeGatewayRuntime runtime) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _wizardCompleted;
    private bool _optionalSetupDeferred;
    private bool _disposed;
    private bool _published;
    private IDisposable? _terminal;
    public GatewayRecord Record { get; private set; } = record;
    public string IdentityDirectory => registry.GetIdentityDirectory(draft.GatewayId);
    public string ConsoleLogPath => Path.Combine(
        NativeGatewayPaths.GetStateDirectory(registry, draft.GatewayId), "wizard-console.log");
    public CancellationToken LifetimeToken => _lifetime.Token;
    public static string GetPairingGuidance(string? requestId)
    {
        var instruction = ApprovalRequestHelper.IsSafeRequestId(requestId)
            ? $"Run openclaw devices approve {requestId}."
            : "Run openclaw devices list, then approve the request for this Companion.";
        return $"This Companion needs pairing approval. Open the Gateway terminal below, which uses this setup profile. {instruction} Then choose Retry.";
    }

    private string ConfigPath => NativeGatewayPaths.GetConfigPath(registry, draft.GatewayId);
    private string ReloadBackupPath => ConfigPath + ".setup-reload.json";

    public void OpenRecoveryTerminal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _terminal?.Dispose();
        _terminal = host.OpenRecoveryTerminal(package, environment);
    }

    public async Task PrepareWizardAsync(CancellationToken cancellationToken)
    {
        // A failed final health check restored reload already. Suspend again before
        // starting a new wizard, not when retrying finalization itself.
        if (!File.Exists(ReloadBackupPath))
            await RestartAsync(cancellationToken);
        await AuthorizeAsync(cancellationToken);
    }

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SuspendReload();
            await host.ValidateConfigurationAsync(package, environment, cancellationToken);
            await AuthorizeCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await AuthorizeCoreAsync(linked.Token);
        }
        finally { _gate.Release(); }
    }

    public async Task ApproveWizardPairingAsync(string? requestId, CancellationToken cancellationToken)
    {
        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
            throw new InvalidOperationException("The Gateway did not supply a safe pairing request for this Companion.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await AuthorizeCoreAsync(linked.Token);
            RequirePairingConfiguration();
            var identity = new DeviceIdentity(IdentityDirectory);
            identity.Initialize();
            var approvalEnvironment = new Dictionary<string, string>(environment)
            {
                // The CLI's local bootstrap requires a profile target, not --url.
                // Prevent inherited target overrides from selecting another Gateway.
                ["OPENCLAW_GATEWAY_URL"] = "",
                ["OPENCLAW_GATEWAY_PORT"] = draft.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["OPENCLAW_GATEWAY_TOKEN"] = Record.SharedGatewayToken ??
                    throw new InvalidOperationException("The setup Gateway credential is unavailable for pairing."),
            };
            var normalizedId = requestId!.Trim();
            var listing = await host.ListDevicePairingRequestsAsync(
                package, approvalEnvironment, linked.Token);
            ApprovalRequestHelper.RequireMatchingDeviceRequest(
                listing, normalizedId, identity.DeviceId, identity.PublicKeyBase64Url);
            // Recheck after the CLI read and before issuing the exact approval.
            await AuthorizeCoreAsync(linked.Token);
            RequirePairingConfiguration();
            var result = await host.ApproveDevicePairingAsync(
                package, normalizedId, approvalEnvironment, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var approved = ApprovalRequestHelper.TryReadApprovedRequestId(result);
            if (!approved.Success || approved.RequestId != normalizedId)
                throw new InvalidOperationException("The Gateway did not confirm this Companion's pairing approval.");
        }
        finally { _gate.Release(); }
    }

    private void RequirePairingConfiguration()
    {
        var configured = NativeGatewaySetupService.ReadConfiguredRecord(draft, File.ReadAllText(ConfigPath));
        if (configured.SharedGatewayToken != Record.SharedGatewayToken)
            throw new InvalidOperationException("The setup Gateway credential changed. Restart setup before pairing.");
    }

    private async Task AuthorizeCoreAsync(CancellationToken cancellationToken)
    {
        // Never let loopback or an existing device token bypass package workload inspection.
        host.ReportProgress(NativeGatewaySetupStage.StartingGateway);
        await runtime.EnsureRunningAsync(Record, cancellationToken);
        host.ReportProgress(NativeGatewaySetupStage.VerifyingEndpoint);
        var provenance = await runtime.InspectAsync(Record, cancellationToken);
        if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            throw new InvalidOperationException("Native gateway ownership could not be verified. No credential was sent.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            BeginWizard();
            _terminal?.Dispose();
            _terminal = null;
            await runtime.StopAsync(linked.Token);
            // Upstream may have changed configuration before an error. Validate before restart.
            Record = NativeGatewaySetupService.ReadConfiguredRecord(draft, File.ReadAllText(ConfigPath));
            SuspendReload();
            await host.ValidateConfigurationAsync(package, environment, linked.Token);
            await AuthorizeCoreAsync(linked.Token);
        }
        finally { _gate.Release(); }
    }

    public void MarkWizardCompleted() => _wizardCompleted = true;
    public void MarkOptionalSetupDeferred() => _optionalSetupDeferred = true;
    public void BeginWizard()
    {
        if (_published)
            throw new InvalidOperationException(
                "This Gateway setup has already been published. Retry completion instead of restarting onboarding.");
        _wizardCompleted = false;
        _optionalSetupDeferred = false;
    }

    public async Task<GatewayRecord> CompleteAsync(
        CancellationToken cancellationToken, CapabilitiesConfig? capabilities = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (!_wizardCompleted && !_optionalSetupDeferred)
                throw new InvalidOperationException("Finish the Gateway wizard before completing native setup.");
            if (_published)
                return Record;

            // Stop before touching configuration: the hosted wizard must no longer be writing.
            await runtime.StopAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            RestoreReload();
            if (capabilities is not null)
                ApplyCapabilities(capabilities);
            Record = NativeGatewaySetupService.ReadConfiguredRecord(draft, File.ReadAllText(ConfigPath));
            await host.ValidateConfigurationAsync(package, environment, linked.Token);
            await AuthorizeCoreAsync(linked.Token);
            await host.VerifyHealthAsync(package, environment, linked.Token);
            await runtime.StopAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            registry.Load();
            registry.AddOrUpdate(Record);
            registry.SetActive(Record.Id);
            registry.Save();
            _published = true;
            // Keep the credential-free draft descriptor. CreateDraftAsync recognizes
            // the published ID and replaces it instead of taking over an active gateway.
            return Record;
        }
        finally
        {
            // A failed health/config check is retryable, but must not leak a running gateway.
            try { await runtime.StopAsync(CancellationToken.None); }
            finally { _gate.Release(); }
        }
    }

    private void ApplyCapabilities(CapabilitiesConfig capabilities)
    {
        var config = ReadConfigurationObject();
        // The native package uses the modern node-command policy schema.
        var keys = ConfigureGatewayStep.NodeCommandsAllowKey.Split('.');
        var section = config;
        foreach (var key in keys[..^1])
        {
            if (!section.TryGetPropertyValue(key, out var child))
                section[key] = child = new JsonObject();
            section = child as JsonObject
                ?? throw new InvalidDataException(
                    $"Native Gateway configuration section '{key}' must be a JSON object.");
        }
        section[keys[^1]] = new JsonArray(
            capabilities.GetEnabledCommandIds().Select(command => (JsonNode?)JsonValue.Create(command)).ToArray());
        AtomicFile.WriteAllText(ConfigPath, config.ToJsonString());
    }

    private JsonObject ReadConfigurationObject()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject
                ?? throw new InvalidDataException("Native Gateway configuration must be a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Native Gateway configuration contains invalid JSON.", ex);
        }
    }

    private void SuspendReload()
    {
        var config = ReadConfigurationObject();
        var gateway = config["gateway"] as JsonObject
            ?? throw new InvalidDataException("Native Gateway configuration section 'gateway' must be a JSON object.");
        var reload = gateway["reload"] as JsonObject;
        // A durable, credential-free backup also survives a Companion crash.
        if (!File.Exists(ReloadBackupPath))
            AtomicFile.WriteAllText(ReloadBackupPath, new JsonObject
            {
                ["present"] = reload?.ContainsKey("mode") == true,
                ["mode"] = reload?["mode"]?.DeepClone(),
            }.ToJsonString());
        if (reload is null)
            gateway["reload"] = reload = new JsonObject();
        reload["mode"] = "off";
        var logging = config["logging"] as JsonObject;
        if (logging is null)
            config["logging"] = logging = new JsonObject();
        logging["file"] = Path.Combine(environment["OPENCLAW_STATE_DIR"], "wizard-console.log");
        AtomicFile.WriteAllText(ConfigPath, config.ToJsonString());
    }

    private void RestoreReload()
    {
        if (!File.Exists(ReloadBackupPath))
            return;
        var backup = JsonNode.Parse(File.ReadAllText(ReloadBackupPath))!;
        var config = ReadConfigurationObject();
        var gateway = config["gateway"] as JsonObject
            ?? throw new InvalidDataException("Native Gateway configuration section 'gateway' must be a JSON object.");
        var reload = gateway["reload"] as JsonObject;
        if (reload is null)
            gateway["reload"] = reload = new JsonObject();
        if (backup["present"]!.GetValue<bool>())
            reload["mode"] = backup["mode"]?.DeepClone();
        else
            reload.Remove("mode");
        AtomicFile.WriteAllText(ConfigPath, config.ToJsonString());
        File.Delete(ReloadBackupPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _terminal?.Dispose();
            _terminal = null;
            await runtime.DisposeAsync();
            RestoreReload();
        }
        finally { _gate.Release(); }
    }
}
