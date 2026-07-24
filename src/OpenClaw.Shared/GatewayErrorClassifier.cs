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
    /// The gateway explicitly rejected this device's stored <em>device</em> token
    /// (structured code <c>AUTH_DEVICE_TOKEN_MISMATCH</c>) — distinct from a wrong
    /// shared/gateway token. This is the one token failure that is safe to
    /// auto-recover: clear only the device token and reconnect, letting a still-valid
    /// shared/bootstrap credential re-derive a fresh device token. Broad
    /// <see cref="TokenDrift"/> stays a manual re-pair signal; this exact kind gates
    /// automatic recovery.
    /// </summary>
    DeviceTokenMismatch,

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

    /// <summary>
    /// A different or unverified local process owns the managed gateway's expected loopback port.
    /// Credentials must not be downgraded or disclosed to that listener.
    /// </summary>
    LocalPortConflict,

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

        // A gateway/shared token mismatch is NOT device-token drift. The gateway emits this
        // wording when gateway.remote.token and gateway.auth.token disagree (including when the
        // wrong local gateway owns the expected port). Re-pairing the device cannot fix it.
        if (Contains(e, "gateway token mismatch") ||
            (Contains(e, "gateway.remote.token") && Contains(e, "gateway.auth.token")))
            return GatewayErrorKind.Auth;

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

    public static bool IsSharedGatewayTokenMismatch(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains(SharedTokenMismatchCode, StringComparison.OrdinalIgnoreCase) ||
         message.Contains("gateway token mismatch", StringComparison.OrdinalIgnoreCase) ||
         (message.Contains("gateway.remote.token", StringComparison.OrdinalIgnoreCase) &&
          message.Contains("gateway.auth.token", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The structured gateway code for a stale <em>device</em> token.</summary>
    public const string DeviceTokenMismatchCode = "AUTH_DEVICE_TOKEN_MISMATCH";

    /// <summary>The structured gateway code for a wrong <em>shared/gateway</em> token.</summary>
    public const string SharedTokenMismatchCode = "AUTH_TOKEN_MISMATCH";

    /// <summary>
    /// Code-aware classification. Structured error codes (top-level <c>error.code</c> and
    /// nested <c>error.details.code</c>) are authoritative and are checked BEFORE the textual
    /// heuristic, because a gateway may send a generic message (e.g. "unauthorized") with the
    /// real reason only in a code. Critically this separates a stale <em>device</em> token
    /// (<see cref="GatewayErrorKind.DeviceTokenMismatch"/>, auto-recoverable) from a wrong
    /// <em>shared</em> token (<see cref="GatewayErrorKind.Auth"/>, NOT device-recoverable) —
    /// the plain <see cref="Classify(string?)"/> heuristic conflates both into
    /// <see cref="GatewayErrorKind.TokenDrift"/>. Falls back to the textual heuristic when no
    /// code is authoritative.
    /// </summary>
    public static GatewayErrorKind ClassifyWithCode(string? message, params string?[] codes)
    {
        if (codes != null)
        {
            foreach (var code in codes)
            {
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                if (string.Equals(code, DeviceTokenMismatchCode, StringComparison.OrdinalIgnoreCase))
                    return GatewayErrorKind.DeviceTokenMismatch;
                // A wrong shared/gateway token is terminal auth but must NOT be treated as a
                // recoverable device-token drift (clearing the device token would just loop).
                if (string.Equals(code, SharedTokenMismatchCode, StringComparison.OrdinalIgnoreCase))
                    return GatewayErrorKind.Auth;
            }
        }

        // Message-level exact device phrasing (the structured code carried as text, or an
        // explicit "device token mismatch"). A bare "token mismatch" is deliberately NOT matched
        // here — it is shared-vs-device ambiguous and falls through to broad TokenDrift below.
        if (!string.IsNullOrWhiteSpace(message) &&
            (message.Contains(DeviceTokenMismatchCode, StringComparison.OrdinalIgnoreCase) ||
             message.Contains("device token mismatch", StringComparison.OrdinalIgnoreCase)))
            return GatewayErrorKind.DeviceTokenMismatch;

        if (!string.IsNullOrWhiteSpace(message) &&
            message.Contains(SharedTokenMismatchCode, StringComparison.OrdinalIgnoreCase))
            return GatewayErrorKind.Auth;

        // Fall back to the textual heuristic. Fold any remaining (non-token) structured codes into
        // the classified text so a code-only signal (e.g. AUTH_RATE_LIMITED carried in the code with
        // a generic "unauthorized" message) keeps the precision main had via
        // Classify(code + " " + message). The two token codes were already handled authoritatively
        // above, so this only affects other codes (rate-limit, server, tls, …).
        if (codes != null)
        {
            var joined = string.Empty;
            foreach (var code in codes)
            {
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                joined = joined.Length == 0 ? code : $"{joined} {code}";
            }

            if (joined.Length > 0)
                return Classify(string.IsNullOrWhiteSpace(message) ? joined : $"{joined} {message}");
        }

        return Classify(message);
    }
}
