using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

/// <summary>Native recovery resources for personal identity failures, without raw Gateway diagnostics.</summary>
internal static class GatewayPersonalErrorPresentation
{
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
