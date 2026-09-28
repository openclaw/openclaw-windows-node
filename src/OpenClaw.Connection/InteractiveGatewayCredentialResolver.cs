using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Resolves operator credentials for user-facing surfaces such as chat.
/// Unlike the connection credential resolver which prefers DeviceToken (WebSocket auth),
/// this resolver prefers SharedGatewayToken because HTTP surfaces (chat URL ?token=)
/// authenticate via the shared token, not the per-device WebSocket token.
/// </summary>
public static class InteractiveGatewayCredentialResolver
{
    internal static GatewayCredential? ResolveForAssistantMediaHttpSurface(
        GatewayRecord record,
        GatewayCredential? fallback) =>
        !string.IsNullOrWhiteSpace(record.SharedGatewayToken)
            ? new GatewayCredential(
                record.SharedGatewayToken!,
                IsBootstrapToken: false,
                CredentialResolver.SourceSharedGatewayToken)
            : fallback is
                {
                    IsBootstrapToken: false,
                    Source: CredentialResolver.SourceDeviceToken,
                }
                ? fallback
                : null;

    public static bool TryResolve(
        GatewayRegistry? registry,
        string settingsDirectory,
        IDeviceIdentityReader identityReader,
        string? effectiveGatewayUrl,
        string? legacyToken,
        string? legacyBootstrapToken,
        out InteractiveGatewayCredential? credential) =>
        TryResolve(
            registry,
            settingsDirectory,
            identityReader,
            effectiveGatewayUrl,
            legacyToken,
            legacyBootstrapToken,
            authorizeCredential: null,
            out credential);

    public static bool TryResolve(
        GatewayRegistry? registry,
        string settingsDirectory,
        IDeviceIdentityReader identityReader,
        string? effectiveGatewayUrl,
        string? legacyToken,
        string? legacyBootstrapToken,
        Func<GatewayRecord, GatewayCredential, bool>? authorizeCredential,
        out InteractiveGatewayCredential? credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentNullException.ThrowIfNull(identityReader);

        var active = registry?.GetActive();
        if (active != null && !string.IsNullOrWhiteSpace(active.Url))
        {
            if (TryResolveRecord(
                    active,
                    registry!.GetIdentityDirectory(active.Id),
                    identityReader,
                    authorizeCredential,
                    out credential,
                    out var rejected))
            {
                return true;
            }

            if (rejected)
                return false;

            if (!string.Equals(active.Url, effectiveGatewayUrl, StringComparison.OrdinalIgnoreCase))
            {
                credential = null;
                return false;
            }
        }

        var gatewayUrl = effectiveGatewayUrl;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            credential = null;
            return false;
        }

        var legacyRecord = new GatewayRecord
        {
            Id = "legacy-settings",
            Url = gatewayUrl,
            IsLocal = GatewayRecordEditing.IsLoopbackEndpoint(gatewayUrl),
            SharedGatewayToken = legacyToken,
            BootstrapToken = legacyBootstrapToken
        };
        var resolver2 = new CredentialResolver(identityReader);
        var legacyCredential = resolver2.ResolveOperator(legacyRecord, settingsDirectory);
        if (legacyCredential == null)
        {
            credential = null;
            return false;
        }
        if (authorizeCredential is not null &&
            !authorizeCredential(legacyRecord, legacyCredential))
        {
            credential = null;
            return false;
        }

        credential = new InteractiveGatewayCredential(
            gatewayUrl,
            legacyCredential.Token,
            legacyCredential.IsBootstrapToken,
            legacyCredential.Source);
        return true;
    }

    /// <summary>
    /// Resolves HTTP credentials for one gateway record.
    /// Shared token wins for dashboard/chat URLs when it is present and allowed.
    /// Otherwise the operator resolver order applies: device token, then bootstrap.
    /// </summary>
    public static bool TryResolveRecord(
        GatewayRecord record,
        string identityDirectory,
        IDeviceIdentityReader identityReader,
        Func<GatewayRecord, GatewayCredential, bool>? authorizeCredential,
        out InteractiveGatewayCredential? credential,
        out bool rejected)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDirectory);
        ArgumentNullException.ThrowIfNull(identityReader);

        rejected = false;
        if (string.IsNullOrWhiteSpace(record.Url))
        {
            credential = null;
            return false;
        }

        // HTTP surfaces prefer the shared token. DeviceToken is WebSocket auth.
        if (!string.IsNullOrWhiteSpace(record.SharedGatewayToken))
        {
            var sharedCredential = new GatewayCredential(
                record.SharedGatewayToken!,
                IsBootstrapToken: false,
                CredentialResolver.SourceSharedGatewayToken);
            if (authorizeCredential is not null &&
                !authorizeCredential(record, sharedCredential))
            {
                credential = null;
                rejected = true;
                return false;
            }

            credential = new InteractiveGatewayCredential(
                record.Url,
                record.SharedGatewayToken!,
                false,
                CredentialResolver.SourceSharedGatewayToken);
            return true;
        }

        var resolved = new CredentialResolver(identityReader).ResolveOperator(record, identityDirectory);
        if (resolved == null)
        {
            credential = null;
            return false;
        }

        if (authorizeCredential is not null &&
            !authorizeCredential(record, resolved))
        {
            credential = null;
            rejected = true;
            return false;
        }

        credential = new InteractiveGatewayCredential(
            record.Url,
            resolved.Token,
            resolved.IsBootstrapToken,
            resolved.Source);
        return true;
    }
}

public sealed record InteractiveGatewayCredential(
    string GatewayUrl,
    string Token,
    bool IsBootstrapToken,
    string Source);
