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
    public static bool TryResolve(
        GatewayRegistry? registry,
        string settingsDirectory,
        IDeviceIdentityReader identityReader,
        string? effectiveGatewayUrl,
        string? legacyToken,
        string? legacyBootstrapToken,
        out InteractiveGatewayCredential? credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentNullException.ThrowIfNull(identityReader);

        var active = registry?.GetActive();
        if (active != null && !string.IsNullOrWhiteSpace(active.Url))
        {
            // For HTTP surfaces (chat), prefer SharedGatewayToken over DeviceToken.
            // DeviceToken is for WebSocket auth (auth.deviceToken); SharedGatewayToken
            // is for HTTP ?token= auth which the chat/dashboard endpoints expect.
            if (!string.IsNullOrWhiteSpace(active.SharedGatewayToken))
            {
                credential = new InteractiveGatewayCredential(
                    active.Url,
                    active.SharedGatewayToken!,
                    false,
                    CredentialResolver.SourceSharedGatewayToken);
                return true;
            }

            // Fall back to standard credential resolution (DeviceToken → Bootstrap)
            var resolver = new CredentialResolver(identityReader);
            var resolved = resolver.ResolveOperator(active, registry!.GetIdentityDirectory(active.Id));
            if (resolved != null)
            {
                credential = new InteractiveGatewayCredential(
                    active.Url,
                    resolved.Token,
                    resolved.IsBootstrapToken,
                    resolved.Source);
                return true;
            }

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

        credential = new InteractiveGatewayCredential(
            gatewayUrl,
            legacyCredential.Token,
            legacyCredential.IsBootstrapToken,
            legacyCredential.Source);
        return true;
    }
}

public sealed record InteractiveGatewayCredential(
    string GatewayUrl,
    string Token,
    bool IsBootstrapToken,
    string Source);
