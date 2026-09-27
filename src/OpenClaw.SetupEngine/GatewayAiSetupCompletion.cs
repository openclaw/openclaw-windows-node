using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum SetupCompletionIntent { Dashboard, CustodianOnboarding }

public sealed record GatewayAiSetupRoute(
    string GatewayId, string? AgentId, string AuthorityId, string? EndpointBinding = null,
    string? IdentityBinding = null, string? SessionKey = null);

/// <summary>A main-model verification receipt, not permission to switch gateways or replay activation.</summary>
public sealed record GatewayAiSetupCompletion(
    SetupCompletionIntent Intent, string GatewayId, string EndpointBinding,
    string ModelRef, string? AgentId, long VerifiedGeneration, string? ModelTarget = null,
    string? IdentityBinding = null, string? SessionKey = null);

public static class SetupCompletionAuthority
{
    public static string CaptureIdentity(string identityPath, string signingDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingDeviceId);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identityPath));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Purpose = "setup-signing-identity-v1", IdentityPath = path, DeviceId = signingDeviceId }))));
    }

    public static void RequirePersistedIdentity(string identityPath, string? expectedBinding)
    {
        if (string.IsNullOrWhiteSpace(expectedBinding))
            throw new SetupNativeOwnershipException();
        var identity = new DeviceIdentity(identityPath);
        try { identity.LoadExisting(); }
        catch (DeviceIdentityLoadException) { throw new SetupNativeOwnershipException(); }
        if (CaptureIdentity(identityPath, identity.DeviceId) != expectedBinding)
            throw new SetupNativeOwnershipException();
    }

    public static bool IsValid(string? identityBinding, string? sessionKey, string? agentId) =>
        !string.IsNullOrWhiteSpace(agentId) &&
        identityBinding is { Length: 64 } && identityBinding.All(Uri.IsHexDigit) &&
        sessionKey?.Split(':', 3) is ["agent", { Length: > 0 } agent, { Length: > 0 }] &&
        agent == agentId;
}
