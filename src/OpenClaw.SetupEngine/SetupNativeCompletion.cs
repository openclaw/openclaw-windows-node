namespace OpenClaw.SetupEngine;

// Values are persisted in restart receipts. Keep legacy channel destinations stable.
public enum SetupNativeDestination { Chat = 0, WhatsApp = 1, Telegram = 2, Channels = 3, Skills = 4 }

/// <summary>A UI destination, not a model role or an authorization claim.</summary>
public sealed record SetupNativeTarget(SetupNativeDestination Destination, string SessionKey)
{
    public string? ChannelId => Destination switch
    {
        SetupNativeDestination.WhatsApp => "whatsapp",
        SetupNativeDestination.Telegram => "telegram",
        _ => null,
    };

    public bool Matches(GatewayAiSetupCompletion proof)
    {
        var parts = SessionKey?.Split(':', 3);
        return Enum.IsDefined(Destination) && proof.ModelTarget is null &&
            SetupCompletionAuthority.IsValid(proof.IdentityBinding, proof.SessionKey, proof.AgentId) &&
            SessionKey == proof.SessionKey &&
            !string.IsNullOrWhiteSpace(proof.AgentId) &&
            parts is ["agent", { Length: > 0 } agent, { Length: > 0 }] && agent == proof.AgentId;
    }
}

public sealed record SetupNativeCompletion(GatewayAiSetupCompletion Verification, SetupNativeTarget Target);
public sealed record SetupVerifiedNativeRoute(GatewayAiSetupCompletion Verification, string SessionKey);

public sealed class SetupNativeOwnershipException : InvalidOperationException
{
    public SetupNativeOwnershipException() : base("The verified Gateway, agent, or primary model changed. Return to AI setup and verify again.") { }
}

public static class SetupNativeVerification
{
    public static void RequireRoute(GatewayAiSetupCompletion expected, GatewayAiSetupRoute current)
    {
        if (current.GatewayId != expected.GatewayId || current.EndpointBinding != expected.EndpointBinding ||
            current.AgentId != expected.AgentId || current.IdentityBinding != expected.IdentityBinding ||
            current.SessionKey != expected.SessionKey ||
            !SetupCompletionAuthority.IsValid(current.IdentityBinding, current.SessionKey, current.AgentId))
            throw new SetupNativeOwnershipException();
    }

    public static void RequireSame(GatewayAiSetupCompletion expected, SetupVerifiedNativeRoute current)
    {
        var proof = current.Verification;
        if (expected.VerifiedGeneration <= 0 || proof.VerifiedGeneration <= 0 ||
            expected.ModelTarget is not null || proof.ModelTarget is not null ||
            proof.GatewayId != expected.GatewayId || proof.EndpointBinding != expected.EndpointBinding ||
            proof.AgentId != expected.AgentId || proof.ModelRef != expected.ModelRef ||
            !SetupCompletionAuthority.IsValid(expected.IdentityBinding, expected.SessionKey, expected.AgentId) ||
            proof.IdentityBinding != expected.IdentityBinding || proof.SessionKey != expected.SessionKey ||
            !new SetupNativeTarget(SetupNativeDestination.Chat, current.SessionKey).Matches(proof))
            throw new SetupNativeOwnershipException();
    }

}
