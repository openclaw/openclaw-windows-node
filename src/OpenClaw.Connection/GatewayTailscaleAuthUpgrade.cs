using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum GatewayTailscaleAuthUpgradeOutcome
{
    Succeeded,
    AlreadyEnabled,
    Ineligible,
    NotActive,
    NotConnected,
    MissingConfigScope,
    ConfigUnavailable,
    PatchRejected,
    PersistenceFailed,
}

public sealed record GatewayTailscaleAuthUpgradeResult(
    GatewayTailscaleAuthUpgradeOutcome Outcome,
    string? Error = null)
{
    public bool IsSuccess => Outcome is
        GatewayTailscaleAuthUpgradeOutcome.Succeeded or
        GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled;
}

public static class GatewayTailscaleAuthUpgradePolicy
{
    public static bool CanOffer(GatewayRecord? record)
    {
        if (record is null ||
            record.TrustTailscaleAuth ||
            !record.IsLocal ||
            record.SshTunnel is not null ||
            GatewayRecordEditing.ResolveManagedDistroName(record) is null)
        {
            return false;
        }

        // Topology only limits where the opt-in is offered; confirmation and a successful Core patch grant trust.
        return GatewayTopologyClassifier.Classify(record.Url, useSshTunnel: false).DetectedKind ==
            GatewayKind.Tailscale;
    }
}

internal interface IGatewayTailscaleAuthConfigClient
{
    event EventHandler<JsonElement>? ConfigUpdated;
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }
    Task RequestConfigAsync();
    Task<ConfigPatchResult> PatchConfigDetailedAsync(
        JsonElement fullConfig,
        string? baseHash,
        int timeoutMs = 15000);
}

internal sealed class GatewayTailscaleAuthConfigClientAdapter(IOperatorGatewayClient client)
    : IGatewayTailscaleAuthConfigClient
{
    public event EventHandler<JsonElement>? ConfigUpdated
    {
        add => client.ConfigUpdated += value;
        remove => client.ConfigUpdated -= value;
    }

    public IReadOnlyList<string> GrantedOperatorScopes => client.GrantedOperatorScopes;
    public bool IsConnectedToGateway => client.IsConnectedToGateway;
    public Task RequestConfigAsync() => client.RequestConfigAsync();
    public Task<ConfigPatchResult> PatchConfigDetailedAsync(
        JsonElement fullConfig,
        string? baseHash,
        int timeoutMs = 15000) =>
        client.PatchConfigDetailedAsync(fullConfig, baseHash, timeoutMs);
}

internal sealed class GatewayTailscaleAuthUpgradeService(GatewayRegistry registry)
{
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(15);

    public async Task<GatewayTailscaleAuthUpgradeResult> EnableAsync(
        string gatewayId,
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var record = registry.GetById(gatewayId);
        if (record is null || !GatewayTailscaleAuthUpgradePolicy.CanOffer(record))
        {
            return record?.TrustTailscaleAuth == true
                ? new(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled)
                : new(GatewayTailscaleAuthUpgradeOutcome.Ineligible);
        }

        if (!string.Equals(registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal))
            return new(GatewayTailscaleAuthUpgradeOutcome.NotActive);

        if (!client.IsConnectedToGateway)
            return new(GatewayTailscaleAuthUpgradeOutcome.NotConnected);

        if (!OperatorScopeHelper.CanReadConfig(client.GrantedOperatorScopes) ||
            !OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.MissingConfigScope);
        }

        ConfigSnapshot snapshot;
        try
        {
            snapshot = await ReadConfigAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(snapshot.BaseHash))
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, "Config base hash is unavailable.");
        }

        var configPatch = CreateAllowTailscalePatch();
        ConfigPatchResult patch;
        try
        {
            patch = await client.PatchConfigDetailedAsync(configPatch, snapshot.BaseHash)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, ex.Message);
        }

        if (!patch.Ok)
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patch.Error);

        var previousValue = false;
        var changed = false;
        registry.Update(gatewayId, current =>
        {
            if (!GatewayTailscaleAuthUpgradePolicy.CanOffer(current))
                return current;

            previousValue = current.TrustTailscaleAuth;
            changed = true;
            return current with { TrustTailscaleAuth = true };
        });

        if (!changed)
            return new(GatewayTailscaleAuthUpgradeOutcome.NotActive);

        try
        {
            registry.Save();
            return new(GatewayTailscaleAuthUpgradeOutcome.Succeeded);
        }
        catch (Exception ex)
        {
            registry.Update(gatewayId, current => current with { TrustTailscaleAuth = previousValue });
            return new(GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed, ex.Message);
        }
    }

    private static async Task<ConfigSnapshot> ReadConfigAsync(
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConfigUpdated(object? _, JsonElement config) => completion.TrySetResult(config.Clone());

        client.ConfigUpdated += OnConfigUpdated;
        try
        {
            await client.RequestConfigAsync().ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(ConfigTimeout, cancellationToken).ConfigureAwait(false);
            return CaptureSnapshot(response);
        }
        finally
        {
            client.ConfigUpdated -= OnConfigUpdated;
        }
    }

    private static ConfigSnapshot CaptureSnapshot(JsonElement response)
    {
        var root = response.TryGetProperty("parsed", out var parsed)
            ? parsed
            : response.TryGetProperty("config", out var config)
                ? config
                : response;
        var baseHash = response.TryGetProperty("baseHash", out var baseHashElement) &&
            baseHashElement.ValueKind == JsonValueKind.String
                ? baseHashElement.GetString()
                : response.TryGetProperty("hash", out var hashElement) &&
                    hashElement.ValueKind == JsonValueKind.String
                    ? hashElement.GetString()
                    : null;
        if (baseHash is null &&
            response.TryGetProperty("raw", out var rawElement) &&
            rawElement.ValueKind == JsonValueKind.String &&
            rawElement.GetString() is { } raw)
        {
            baseHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }

        return new(root.Clone(), baseHash);
    }

    private static JsonElement CreateAllowTailscalePatch()
    {
        using var document = JsonDocument.Parse("""
            { "gateway": { "auth": { "allowTailscale": true } } }
            """);
        return document.RootElement.Clone();
    }

    private sealed record ConfigSnapshot(JsonElement Root, string? BaseHash);
}
