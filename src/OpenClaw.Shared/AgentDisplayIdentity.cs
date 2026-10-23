using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record AgentDisplayIdentity(string AgentId, string? Name, string? Emoji, string? Avatar);

public interface IAgentIdentityGateway
{
    long? IdentityConnectionEpoch { get; }
    bool CanReadAgentIdentity { get; }
    Task<AgentDisplayIdentity> GetAgentIdentityAsync(
        string agentId, long connectionEpoch, CancellationToken cancellationToken);
}

public partial class OpenClawGatewayClient : IAgentIdentityGateway
{
    private const string AgentIdentityMethod = "agent.identity.get";
    private const int AgentIdentityTimeoutMs = 10_000;

    public long? IdentityConnectionEpoch => SessionMutationConnectionEpoch;
    public bool CanReadAgentIdentity => IdentityConnectionEpoch.HasValue &&
        AdvertisedServerMethods.Contains(AgentIdentityMethod, StringComparer.Ordinal) &&
        (GrantedOperatorScopes.Contains("operator.read", StringComparer.Ordinal) ||
         GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal));

    public async Task<AgentDisplayIdentity> GetAgentIdentityAsync(
        string agentId, long connectionEpoch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        cancellationToken.ThrowIfCancellationRequested();
        if (IdentityConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before identity lookup.");
        if (!CanReadAgentIdentity)
            throw new InvalidOperationException("The Gateway does not advertise an authorized agent identity lookup.");

        var owner = agentId.Trim();
        var payload = await SendResponseRequestAsync(
            AgentIdentityMethod, new { agentId = owner }, AgentIdentityTimeoutMs, connectionEpoch)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (IdentityConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed during identity lookup.");
        return ParseAgentDisplayIdentity(payload, owner);
    }

    internal static AgentDisplayIdentity ParseAgentDisplayIdentity(JsonElement payload, string expectedAgentId)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Gateway returned an invalid agent identity.");
        var agentId = ReadIdentityString(payload, "agentId");
        if (agentId is null || !string.Equals(agentId, expectedAgentId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Gateway returned an identity for a different agent.");
        return new(agentId, ReadIdentityString(payload, "name"),
            ReadIdentityString(payload, "emoji"), ReadIdentityString(payload, "avatar"));
    }

    private static string? ReadIdentityString(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("The Gateway returned an invalid agent identity field.");
        return value.GetString();
    }
}
