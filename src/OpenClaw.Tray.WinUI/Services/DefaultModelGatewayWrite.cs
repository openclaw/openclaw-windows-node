// <summary>
// Performs default-model configuration writes only while the admitted Gateway connection remains current.
// Each patch reads fresh configuration, and it must never write to a replacement Gateway or connection epoch.
// Usage:
//   var write = new DefaultModelGatewayWrite(client, gatewayId, currentClient, activeGatewayId, connectedGatewayId, agentsList);
//   await write.SetPrimaryModelAsync("openai/gpt-6");
//   await write.ClearDefaultAgentModelAsync(); // restores the configured default-agent choice.
// </summary>
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// Default-model config writes bound to the Gateway and authenticated connection the change was
/// admitted for. Local AI may queue a write behind its runtime gate; every request rechecks the
/// binding first and is sent only on the captured connection epoch, so a replacement Gateway or a
/// reconnected session never receives it. The Gateway prefers the default agent's own primary for
/// new conversations, so a primary write also drops that primary.
/// </summary>
internal sealed class DefaultModelGatewayWrite : IDefaultModelGatewayWrite
{
    private const int ConfigGetTimeoutMs = 15_000;

    private readonly IOperatorGatewayClient _client;
    private readonly long _epoch;
    private readonly string _gatewayId;
    private readonly Func<IOperatorGatewayClient?> _currentClient;
    private readonly Func<string?> _activeGatewayId;
    private readonly Func<string?> _connectedGatewayId;
    private readonly Func<JsonElement?> _agentsList;

    public DefaultModelGatewayWrite(
        IOperatorGatewayClient client,
        string gatewayId,
        Func<IOperatorGatewayClient?> currentClient,
        Func<string?> activeGatewayId,
        Func<string?> connectedGatewayId,
        Func<JsonElement?> agentsList)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gatewayId = gatewayId ?? throw new ArgumentNullException(nameof(gatewayId));
        _currentClient = currentClient ?? throw new ArgumentNullException(nameof(currentClient));
        _activeGatewayId = activeGatewayId ?? throw new ArgumentNullException(nameof(activeGatewayId));
        _connectedGatewayId = connectedGatewayId ?? throw new ArgumentNullException(nameof(connectedGatewayId));
        _agentsList = agentsList ?? throw new ArgumentNullException(nameof(agentsList));
        _epoch = client.SessionMutationConnectionEpoch ?? throw new DefaultModelGatewayChangedException();
        RequireCurrent();
    }

    private bool IsCurrent =>
        ReferenceEquals(_client, _currentClient()) && _client.IsConnectedToGateway &&
        _client.SessionMutationConnectionEpoch == _epoch &&
        string.Equals(_activeGatewayId(), _gatewayId, StringComparison.Ordinal) &&
        string.Equals(_connectedGatewayId(), _gatewayId, StringComparison.Ordinal);

    private void RequireCurrent()
    {
        if (!IsCurrent)
            throw new DefaultModelGatewayChangedException();
    }

    /// <summary>Sets the Gateway default primary and clears the default agent's overriding primary.</summary>
    public Task SetPrimaryModelAsync(string modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef) || modelRef.Length > 512 ||
            modelRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException("Choose a valid model.");
        return PatchConfigAsync(config => GatewayConfigEnvelope.BuildPrimaryPatch(
            config, modelRef, GatewayConfigEnvelope.ResolveDefaultAgentId(config, _agentsList())));
    }

    /// <summary>Clears the default agent's model override without changing the Gateway default.</summary>
    public Task ClearDefaultAgentModelAsync() =>
        PatchConfigAsync(config => GatewayConfigEnvelope.BuildClearAgentModelPatch(
            config, GatewayConfigEnvelope.ResolveDefaultAgentId(config, _agentsList())));

    /// <summary>Applies a patch built from fresh config; a null patch means nothing to change.</summary>
    private async Task PatchConfigAsync(Func<JsonElement, JsonObject?> buildPatch)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            RequireCurrent();
            JsonElement response = await _client
                .SendWizardRequestOnConnectionAsync("config.get", new { }, _epoch, ConfigGetTimeoutMs)
                .ConfigureAwait(false);
            // The binding may have changed while config.get was pending; the patch must not follow it.
            RequireCurrent();
            if (!GatewayConfigEnvelope.TryRead(response, out JsonElement config, out string? baseHash))
                throw new InvalidDataException("The Gateway returned an unreadable configuration.");
            if (buildPatch(config) is not { } patch)
                return;
            ConfigPatchResult result = await _client.PatchConfigOnConnectionAsync(
                    JsonSerializer.SerializeToElement(patch), baseHash, _epoch)
                .ConfigureAwait(false);
            if (result.Ok)
            {
                // Accepted by the admitted Gateway: a success even if the connection changed during the reply.
                if (IsCurrent)
                    await _client.RequestConfigAsync().ConfigureAwait(false);
                return;
            }
            // A send refused for a changed epoch surfaces as a failed result; report the change instead.
            RequireCurrent();
            if (!result.LooksLikeStaleBaseHash || attempt == 1)
                throw new InvalidOperationException(result.Error ?? "The Gateway rejected the model change.");
        }
    }
}
