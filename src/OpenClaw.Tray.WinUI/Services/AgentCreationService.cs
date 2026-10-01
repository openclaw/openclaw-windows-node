using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClawTray.Services;

internal sealed class AgentCreationService(Func<IOperatorGatewayClient?> getClient)
{
    public bool CanCreate => getClient() is { IsConnectedToGateway: true } client
        && client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal);

    public async Task<string> CreateAsync(string name, string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var client = getClient();
        if (client is not { IsConnectedToGateway: true } ||
            !client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
            throw new InvalidOperationException("Connect to a gateway with operator.admin permission to create an agent.");

        // The workspace is a path on the gateway host. Never resolve or create it on Windows.
        // OpenClaw 743c2ceb, packages/gateway-protocol/src/schema/agents-models-skills.ts:
        // AgentsCreateResultSchema requires literal ok:true and a non-empty agentId.
        var result = await client.SendWizardRequestAsync("agents.create",
            new { name = name.Trim(), workspace = workspace.Trim() });
        if (!ReferenceEquals(client, getClient()) || !client.IsConnectedToGateway)
            throw new InvalidOperationException("The gateway connection changed. Refresh agents on the original gateway to check whether creation completed.");
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("agentId", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidOperationException("The gateway did not confirm agent creation. Refresh agents before trying again.");
        return id.GetString()!;
    }
}
