using System;

namespace OpenClaw.Shared;

/// <summary>
/// Actionable classification of a gateway connection error. Lets the UI route a
/// raw error string to a specific recovery path instead of a generic failure —
/// distinguishing unauthorized, scope mismatch, token drift, pairing, TLS,
/// tunnel, and server problems.
/// </summary>
public enum GatewayErrorKind
{
    /// <summary>No error text, or nothing recognizable.</summary>
    Unknown,

    /// <summary>Connection refused / unreachable / timed out.</summary>
    Network,

    /// <summary>Generic unauthorized / invalid-token rejection.</summary>
    Auth,

    /// <summary>
    /// The stored device token is no longer recognized by the gateway (rotated,
    /// revoked, or replaced) — the fix is to re-pair, not to retry.
    /// </summary>
    TokenDrift,

    /// <summary>
    /// Authenticated but missing a required operator/node scope (e.g. cannot
    /// approve pairing or read config) — the fix is to re-pair for higher scopes.
    /// </summary>
    ScopeMismatch,

    /// <summary>Device/node pairing approval is pending on the gateway host.</summary>
    PairingRequired,

    /// <summary>Pairing was explicitly rejected on the gateway host.</summary>
    PairingRejected,

    /// <summary>TLS/certificate/cleartext transport problem.</summary>
    Tls,

    /// <summary>SSH tunnel could not be established or dropped.</summary>
    Tunnel,

    /// <summary>Gateway returned a 5xx / internal error.</summary>
    Server,

    /// <summary>Rate limited by the gateway.</summary>
    RateLimited,
}

/// <summary>
/// Pure heuristic classifier for gateway error strings. Order is significant:
/// the more specific kinds (scope, token drift) are matched before the generic
/// auth bucket so a "re-pair" path wins over a plain "retry" path.
/// </summary>
public static class GatewayErrorClassifier
{
    public static GatewayErrorKind Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return GatewayErrorKind.Unknown;

        var e = error.ToLowerInvariant();

        if ((Contains(e, "rate") && Contains(e, "limit")) ||
            Contains(e, "429") || Contains(e, "too many request"))
            return GatewayErrorKind.RateLimited;

        // SSH/tunnel first: SSH failures often read "Permission denied
        // (publickey)" which would otherwise be mistaken for a scope problem.
        if (Contains(e, "ssh") || Contains(e, "tunnel"))
            return GatewayErrorKind.Tunnel;

        // Transport security before pairing/auth: e.g. "certificate not
        // approved by CA" must not be read as a pairing approval.
        if (Contains(e, "tls") || Contains(e, "ssl") || Contains(e, "certificate") ||
            Contains(e, "cert ") || Contains(e, "handshake") ||
            Contains(e, "cleartext") || Contains(e, "insecure"))
            return GatewayErrorKind.Tls;

        // Scope/permission problems — authenticated but under-privileged.
        if (Contains(e, "scope") ||
            Contains(e, "insufficient priv") ||
            Contains(e, "not permitted") ||
            Contains(e, "permission denied") ||
            (Contains(e, "forbidden") && Contains(e, "scope")))
            return GatewayErrorKind.ScopeMismatch;

        // Token drift — the device token specifically is stale/unknown.
        if (Contains(e, "re-pair") || Contains(e, "repair token") ||
            Contains(e, "token rotat") || Contains(e, "token revoked") ||
            Contains(e, "token mismatch") || Contains(e, "token drift") ||
            (Contains(e, "device token") &&
                (Contains(e, "unknown") || Contains(e, "invalid") ||
                 Contains(e, "expired") || Contains(e, "not recognized") ||
                 Contains(e, "no longer"))))
            return GatewayErrorKind.TokenDrift;

        // Pairing lifecycle. Use specific tokens ("pairing"/"approval") so we
        // don't match "repair" (contains "pair") or "approved by CA".
        if (Contains(e, "pairing") || Contains(e, "approval"))
        {
            if (Contains(e, "reject") || Contains(e, "denied") || Contains(e, "declin"))
                return GatewayErrorKind.PairingRejected;
            return GatewayErrorKind.PairingRequired;
        }

        // Server (5xx) before the broad auth bucket: a transient
        // "500 internal error: token validation failed" must not route the
        // user to a re-pair flow.
        if (Contains(e, "500") || Contains(e, "502") || Contains(e, "503") ||
            Contains(e, "internal error") || Contains(e, "server error"))
            return GatewayErrorKind.Server;

        // Generic auth — after the more specific auth-adjacent kinds above.
        if (Contains(e, "401") || Contains(e, "unauthor") || Contains(e, "forbid") ||
            Contains(e, "auth") || Contains(e, "token") || Contains(e, "credential"))
            return GatewayErrorKind.Auth;

        // Network.
        if (Contains(e, "refused") || Contains(e, "unreachable") ||
            Contains(e, "timeout") || Contains(e, "timed out") ||
            Contains(e, "network") || Contains(e, "no route") ||
            Contains(e, "could not connect") || Contains(e, "connection closed"))
            return GatewayErrorKind.Network;

        return GatewayErrorKind.Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);
}
