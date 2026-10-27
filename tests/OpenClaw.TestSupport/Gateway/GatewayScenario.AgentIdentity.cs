using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    public static GatewayScenario CreateAgentIdentityError(string code) =>
        CreateAgentIdentity(_ => throw new FixtureRequestException(code, "Identity unavailable."));

    public static GatewayScenario CreateAgentIdentity(
        Func<JsonElement, object>? respond = null, bool advertised = true, string[]? scopes = null)
    {
        var browse = CreateBrowse();
        var reads = browse._reads.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (advertised)
            reads["agent.identity.get"] = JsonSerializer.SerializeToElement(new { });
        return new(browse._sessions, reads, agentIdentity: respond ?? (parameters => new
        {
            agentId = parameters.GetProperty("agentId").GetString(),
            name = parameters.GetProperty("agentId").GetString() == "research"
                ? "Resolved Research" : "Resolved Main",
            nameSource = "workspace",
            emoji = "\u2728",
            avatarStatus = "none",
            avatarReason = "not-configured"
        }), operatorScopes: scopes);
    }
}
