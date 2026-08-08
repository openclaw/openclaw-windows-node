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
    public static bool IsEligible(GatewayRecord? record)
    {
        if (record is null ||
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

    public static bool CanOffer(GatewayRecord? record) =>
        record?.TrustTailscaleAuth != true && IsEligible(record);
}

internal interface IGatewayTailscaleAuthConfigClient
{
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }
    Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000);
    Task<ConfigPatchResult> PatchConfigDetailedAsync(
        JsonElement fullConfig,
        string? baseHash,
        int timeoutMs = 15000);
}

internal sealed class GatewayTailscaleAuthConfigClientAdapter(IOperatorGatewayClient client)
    : IGatewayTailscaleAuthConfigClient
{
    public IReadOnlyList<string> GrantedOperatorScopes => client.GrantedOperatorScopes;
    public bool IsConnectedToGateway => client.IsConnectedToGateway;
    public Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000) =>
        client.RequestConfigDetailedAsync(timeoutMs);
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
        if (!GatewayTailscaleAuthUpgradePolicy.IsEligible(record))
            return new(GatewayTailscaleAuthUpgradeOutcome.Ineligible);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, ex.Message);
        }

        if (record!.TrustTailscaleAuth && AllowsTailscaleAuth(snapshot.Root))
            return new(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled);

        if (string.IsNullOrWhiteSpace(snapshot.BaseHash))
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, "Config base hash is unavailable.");
        }

        var previousValue = false;
        var changed = false;
        if (!record.TrustTailscaleAuth)
        {
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
            }
            catch (Exception ex)
            {
                registry.Update(gatewayId, current => current with { TrustTailscaleAuth = previousValue });
                return new(GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed, ex.Message);
            }
        }

        // Core beta.7 and current main define config.patch as JSON Merge Patch.
        // Send only the intended leaf: replaying the full snapshot would turn
        // unrelated null values into deletion markers.
        var configPatch = CreateAllowTailscalePatch();
        ConfigPatchResult patch;
        try
        {
            patch = await client.PatchConfigDetailedAsync(configPatch, snapshot.BaseHash)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The response can be lost after Core commits the patch. Keep the marker so
            // the next trust-aware launch revalidates the authoritative Core state.
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, ex.Message);
        }

        if (!patch.Ok && patch.IsGatewayRejection)
            return RollBackMarkerAfterRejectedPatch(gatewayId, changed, previousValue, patch.Error);

        if (!patch.Ok)
        {
            // The request may have committed before its response was lost. Preserve
            // the marker so the next trust-aware launch revalidates Core state.
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patch.Error);
        }

        return new(GatewayTailscaleAuthUpgradeOutcome.Succeeded);
    }

    public async Task<bool> RevalidateAsync(
        string gatewayId,
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var record = registry.GetById(gatewayId);
        if (record?.TrustTailscaleAuth != true ||
            !GatewayTailscaleAuthUpgradePolicy.IsEligible(record) ||
            !string.Equals(registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal) ||
            !client.IsConnectedToGateway ||
            !OperatorScopeHelper.CanReadConfig(client.GrantedOperatorScopes))
        {
            return false;
        }

        ConfigSnapshot snapshot;
        try
        {
            snapshot = await ReadConfigAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (AllowsTailscaleAuth(snapshot.Root))
            return true;

        registry.Update(gatewayId, current => current with { TrustTailscaleAuth = false });
        try
        {
            registry.Save();
        }
        catch
        {
            registry.Update(gatewayId, current => current with { TrustTailscaleAuth = true });
        }
        return false;
    }

    private static async Task<ConfigSnapshot> ReadConfigAsync(
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var timeoutMs = checked((int)ConfigTimeout.TotalMilliseconds);
        var response = await client.RequestConfigDetailedAsync(timeoutMs)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return CaptureSnapshot(response);
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

    private GatewayTailscaleAuthUpgradeResult RollBackMarkerAfterRejectedPatch(
        string gatewayId,
        bool changed,
        bool previousValue,
        string? patchError)
    {
        if (!changed)
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patchError);

        registry.Update(gatewayId, current => current with { TrustTailscaleAuth = previousValue });
        try
        {
            registry.Save();
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patchError);
        }
        catch (Exception ex)
        {
            return new(
                GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed,
                $"Core rejected the patch and the local marker rollback failed: {ex.Message}");
        }
    }

    // A true marker is issued only after this service or setup explicitly writes
    // allowTailscale=true. Omission therefore means that grant drifted or was revoked;
    // do not duplicate Core's environment-sensitive implicit auth resolver here.
    private static bool AllowsTailscaleAuth(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object &&
        config.TryGetProperty("gateway", out var gateway) &&
        gateway.ValueKind == JsonValueKind.Object &&
        gateway.TryGetProperty("auth", out var auth) &&
        auth.ValueKind == JsonValueKind.Object &&
        auth.TryGetProperty("allowTailscale", out var allowTailscale) &&
        allowTailscale.ValueKind is JsonValueKind.True;

    private sealed record ConfigSnapshot(JsonElement Root, string? BaseHash);
}
