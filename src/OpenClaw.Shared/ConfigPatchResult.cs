namespace OpenClaw.Shared;

/// <summary>
/// Result of <see cref="OpenClawGatewayClient.PatchConfigDetailedAsync"/>.
///
/// Distinguishes "patch dispatched" (the older fire-and-forget bool) from
/// "patch was accepted by the gateway". Lets config saves surface localized
/// rejection guidance rather than reporting dispatch as success.
/// This includes wire-format validation errors that fail silently with the legacy
/// <c>config.set { path, value }</c> path on newer gateways.
/// </summary>
public sealed class ConfigPatchResult
{
    /// <summary>True when the gateway accepted the patch (responded ok:true).</summary>
    public bool Ok { get; init; }

    /// <summary>True when the request deadline elapsed without a response.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Optional safe error detail. Remote rejection text is withheld; pages use localized fallback guidance.</summary>
    public string? Error { get; init; }

    /// <summary>Raw JSON on success. Null on failure because remote errors can echo credentials.</summary>
    public string? RawResponse { get; init; }

    internal bool HasStaleBaseHashSignal { get; init; }

    /// <summary>
    /// True when the gateway rejected the patch because our baseHash was
    /// stale (someone else changed the config out from under us). Pages
    /// should refresh the cached config and prompt the user to retry.
    ///
    /// The bare word "conflict" is too generic to match on its own (Hanselman
    /// review MEDIUM-3) — a JSON schema error like "property 'conflict_mode'
    /// is invalid" would otherwise trigger a spurious refresh-and-retry loop.
    /// We require "conflict" to co-occur with "hash" or "baseHash" before
    /// treating it as a stale-hash signal.
    /// </summary>
    public bool LooksLikeStaleBaseHash => HasStaleBaseHashSignal || IsStaleBaseHashError(Error);

    internal static bool IsStaleBaseHashError(string? error) =>
        error != null &&
        (error.Contains("baseHash", System.StringComparison.OrdinalIgnoreCase) ||
         error.Contains("stale", System.StringComparison.OrdinalIgnoreCase) ||
         (error.Contains("conflict", System.StringComparison.OrdinalIgnoreCase) &&
          error.Contains("hash", System.StringComparison.OrdinalIgnoreCase)));
}
