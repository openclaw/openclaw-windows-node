// Maps personal identity failures to native recovery resources.
using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

/// <summary>Native recovery resources for personal identity failures, without raw Gateway diagnostics.</summary>
/// <example>
/// Resolve <c>ResourceKey(result.ErrorKind, result.RollbackIncomplete)</c> through native localization;
/// a null result leaves non-personal error presentation with its existing owner.
/// </example>
internal static class GatewayPersonalErrorPresentation
{
    /// <summary>Returns the recovery resource for a personal failure, or null for other error kinds.</summary>
    public static string? ResourceKey(GatewayErrorKind? kind, bool rollbackIncomplete = false)
    {
        if (kind is not (GatewayErrorKind.PersonalIdentityUnverified or GatewayErrorKind.PersonalIdentityReverificationRequired))
            return null;
        if (rollbackIncomplete)
            return "ConnectionPage_PersonalIdentityRecoveryIncomplete";
        return kind == GatewayErrorKind.PersonalIdentityUnverified
            ? "ConnectionPage_PersonalIdentityUnverified"
            : "ConnectionPage_PersonalIdentityReverificationRequired";
    }
}
