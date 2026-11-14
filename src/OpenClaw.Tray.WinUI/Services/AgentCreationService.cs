using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClawTray.Services;

internal sealed class AgentCreationService
{
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly IOperatorGatewayClient? _client;
    private readonly long? _connectionEpoch;

    public AgentCreationService(Func<IOperatorGatewayClient?> getClient)
    {
        _getClient = getClient;
        _client = getClient();
        _connectionEpoch = _client?.SessionMutationConnectionEpoch;
    }

    public bool CanCreate => _connectionEpoch.HasValue
        && ReferenceEquals(_client, _getClient())
        && _client is { IsConnectedToGateway: true } client
        && client.SessionMutationConnectionEpoch == _connectionEpoch
        && client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal);

    public async Task<string> CreateAsync(string name, string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        if (!CanCreate || _client is not { } client || _connectionEpoch is not { } connectionEpoch)
            throw new InvalidOperationException("Connect to a gateway with operator.admin permission to create an agent.");

        // The workspace is a path on the gateway host. Never resolve or create it on Windows.
        // Bind dispatch and acknowledgement to the connection that opened the dialog.
        var result = await client.CreateAgentConfirmedAsync(
            name.Trim(), workspace.Trim(), connectionEpoch);
        if (!CanCreate)
            throw new InvalidOperationException("The gateway connection changed. Refresh agents on the original gateway to check whether creation completed.");
        // Pinned OpenClaw 098e9915, schema/agents-create.ts requires all four fields.
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("agentId", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()) ||
            !result.TryGetProperty("name", out var createdName) || createdName.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(createdName.GetString()) ||
            !result.TryGetProperty("workspace", out var createdWorkspace) || createdWorkspace.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(createdWorkspace.GetString()))
            throw new InvalidOperationException("The gateway did not confirm agent creation. Refresh agents before trying again.");
        return id.GetString()!;
    }
}
