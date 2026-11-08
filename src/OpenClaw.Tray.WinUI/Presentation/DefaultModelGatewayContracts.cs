// <summary>
// Contracts and pure wire helpers for reading and changing the active Gateway default model.
// Writes are connection-bound so callers cannot apply a decision to a replacement Gateway.
// Usage:
//   IDefaultModelGatewayWrite write = gateway.BeginWrite(state.GatewayId);
//   await write.SetPrimaryModelAsync("openai/gpt-6");
// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Chat;

namespace OpenClawTray.Presentation;

/// <summary>What the Default model page knows about the active Gateway's model configuration.</summary>
/// <param name="PrimaryModel"><c>agents.defaults.model.primary</c>; Local AI publishes and withdraws here.</param>
/// <param name="DefaultAgentModel">
/// The default agent's own primary model, which the Gateway prefers over <paramref name="PrimaryModel"/>
/// for new conversations (Gateway setup writes one for the agent it configures).
/// </param>
/// <param name="GatewayId">Active registry Gateway this state, including <paramref name="LocalAiRouteAvailable"/>, was projected for.</param>
/// <param name="LocalAiReceiptOwnsGateway">A native Local AI ownership receipt names this Gateway. Unlike runtime evidence, it persists when the Local AI install cannot be loaded.</param>
/// <summary>Projects the active Gateway model configuration and Local AI ownership evidence for the page.</summary>
internal sealed record DefaultModelGatewayState(
    bool IsConnected,
    bool CanWriteConfig,
    IReadOnlyList<ChatModelChoice> Models,
    string? PrimaryModel,
    bool LocalAiRouteAvailable,
    string? DefaultAgentModel = null,
    string? GatewayId = null,
    bool LocalAiReceiptOwnsGateway = false)
{
    public static DefaultModelGatewayState Disconnected { get; } = new(false, false, [], null, false);

    /// <summary>The model new conversations on the default agent use.</summary>
    public string? EffectiveModel => DefaultAgentModel ?? PrimaryModel;
}

/// <summary>Reads and changes the Gateway's default (primary) agent model.</summary>
internal interface IDefaultModelGatewayService
{
    event EventHandler? Changed;
    DefaultModelGatewayState Current { get; }
    /// <summary>Refreshes <see cref="Current"/> from the active Gateway connection.</summary>
    Task RefreshAsync();
    /// <summary>
    /// Binds writes to the current operator connection. Throws <see cref="InvalidOperationException"/> when
    /// disconnected or without config write scope, and <see cref="DefaultModelGatewayChangedException"/> when
    /// the active or connected Gateway is not <paramref name="expectedGatewayId"/>.
    /// </summary>
    IDefaultModelGatewayWrite BeginWrite(string? expectedGatewayId);
}

/// <summary>
/// Config writes bound to the Gateway connection a model change was admitted for. Every request
/// rechecks that binding and fails closed with <see cref="DefaultModelGatewayChangedException"/>.
/// </summary>
internal interface IDefaultModelGatewayWrite
{
    /// <summary>Sets <c>agents.defaults.model.primary</c> and drops the default agent's own primary.</summary>
    Task SetPrimaryModelAsync(string modelRef);
    /// <summary>Drops the default agent's own primary so it follows <c>agents.defaults.model.primary</c>.</summary>
    Task ClearDefaultAgentModelAsync();
}

/// <summary>Signals that a connection-bound model operation lost its original Gateway.</summary>
internal sealed class DefaultModelGatewayChangedException : InvalidOperationException
{
    public DefaultModelGatewayChangedException()
        : base("The Gateway connection changed before the model change was sent. Review the default model and try again.") { }
}

/// <summary>Pure helpers for the <c>config.get</c> envelope and the primary-model patch.</summary>
internal static class GatewayConfigEnvelope
{
    /// <summary>
    /// Unwraps a Gateway configuration envelope and returns its config root and concurrency hash.
    /// The hash comes from <c>baseHash</c>, <c>hash</c>, or SHA-256 of the raw configuration.
    /// </summary>
    public static bool TryRead(JsonElement envelope, out JsonElement config, out string? baseHash)
    {
        config = envelope.TryGetProperty("parsed", out JsonElement parsed) ? parsed
            : envelope.TryGetProperty("config", out JsonElement inner) ? inner
            : envelope;
        baseHash = null;
        if (envelope.ValueKind != JsonValueKind.Object)
            return false;
        if (envelope.TryGetProperty("baseHash", out JsonElement bh) && bh.ValueKind == JsonValueKind.String)
            baseHash = bh.GetString();
        else if (envelope.TryGetProperty("hash", out JsonElement hash) && hash.ValueKind == JsonValueKind.String)
            baseHash = hash.GetString();
        else if (envelope.TryGetProperty("raw", out JsonElement raw) && raw.ValueKind == JsonValueKind.String &&
                 raw.GetString() is { } rawContent)
            baseHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent)));
        return config.ValueKind == JsonValueKind.Object;
    }

    public static string? ReadPrimaryModel(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object &&
        config.TryGetProperty("agents", out JsonElement agents) && agents.ValueKind == JsonValueKind.Object &&
        agents.TryGetProperty("defaults", out JsonElement defaults)
            ? ReadModelPrimary(defaults)
            : null;

    /// <summary>
    /// The agent new conversations go to: the <c>agents.list</c> <c>defaultId</c>, else the sole
    /// configured roster agent. Null when the Gateway requires an explicit agent choice.
    /// </summary>
    public static string? ResolveDefaultAgentId(JsonElement config, JsonElement? agentsList)
    {
        if (agentsList is { ValueKind: JsonValueKind.Object } list)
        {
            if (list.TryGetProperty("selectionRequired", out JsonElement required) && required.ValueKind == JsonValueKind.True)
                return null;
            if (list.TryGetProperty("defaultId", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { } defaultId && !string.IsNullOrWhiteSpace(defaultId))
                return defaultId.Trim();
        }
        string[] roster = RosterAgents(config).Take(2).Select(agent => agent.Id).ToArray();
        return roster.Length == 1 ? roster[0] : null;
    }

    /// <summary>The agent's own primary model (<c>agents.entries.&lt;id&gt;.model</c> or legacy <c>agents.list[]</c>).</summary>
    public static string? ReadAgentPrimaryModel(JsonElement config, string? agentId) =>
        FindAgent(config, agentId) is { } agent ? ReadModelPrimary(agent.Entry) : null;

    /// <summary>
    /// Partial patch setting <c>agents.defaults.model.primary</c>. An existing model allowlist
    /// is extended additively, never replaced, so unrelated entries are untouched. The default
    /// agent's own primary is removed in the same patch; otherwise the Gateway keeps using it.
    /// </summary>
    public static JsonObject BuildPrimaryPatch(JsonElement config, string modelRef, string? defaultAgentId)
    {
        var defaults = new JsonObject { ["model"] = new JsonObject { ["primary"] = modelRef } };
        if (config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("agents", out JsonElement agents) && agents.ValueKind == JsonValueKind.Object &&
            agents.TryGetProperty("defaults", out JsonElement existing) && existing.ValueKind == JsonValueKind.Object &&
            existing.TryGetProperty("models", out JsonElement allowlist) && allowlist.ValueKind == JsonValueKind.Object &&
            !allowlist.TryGetProperty(modelRef, out _))
        {
            defaults["models"] = new JsonObject { [modelRef] = new JsonObject() };
        }
        JsonObject agentsPatch = BuildAgentModelClear(config, defaultAgentId) ?? new JsonObject();
        agentsPatch["defaults"] = defaults;
        return new JsonObject { ["agents"] = agentsPatch };
    }

    /// <summary>Partial patch removing the agent's own primary model; null when it has none.</summary>
    public static JsonObject? BuildClearAgentModelPatch(JsonElement config, string? agentId) =>
        BuildAgentModelClear(config, agentId) is { } agentsPatch ? new JsonObject { ["agents"] = agentsPatch } : null;

    // Mirrors the Gateway's model shape: "provider/model" or { primary, fallbacks }.
    private static string? ReadModelPrimary(JsonElement holder)
    {
        if (holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty("model", out JsonElement model))
            return null;
        if (model.ValueKind == JsonValueKind.String)
            return model.GetString();
        return model.ValueKind == JsonValueKind.Object &&
               model.TryGetProperty("primary", out JsonElement primary) && primary.ValueKind == JsonValueKind.String
            ? primary.GetString()
            : null;
    }

    // Only the primary is removed: per-agent fallbacks and other agent settings stay.
    private static JsonObject? BuildAgentModelClear(JsonElement config, string? agentId)
    {
        if (FindAgent(config, agentId) is not { } agent || ReadModelPrimary(agent.Entry) is null)
            return null;
        JsonNode? model = agent.Entry.GetProperty("model").ValueKind == JsonValueKind.Object
            ? new JsonObject { ["primary"] = null }
            : null;
        // The Gateway merges id-keyed arrays by id, so a one-element list patch touches only this agent.
        return agent.Listed
            ? new JsonObject { ["list"] = new JsonArray(new JsonObject { ["id"] = agent.Id, ["model"] = model }) }
            : new JsonObject { ["entries"] = new JsonObject { [agent.Id] = new JsonObject { ["model"] = model } } };
    }

    private static RosterAgent? FindAgent(JsonElement config, string? agentId) =>
        string.IsNullOrWhiteSpace(agentId)
            ? null
            : RosterAgents(config).FirstOrDefault(agent => string.Equals(agent.Id, agentId.Trim(), StringComparison.OrdinalIgnoreCase));

    // The Gateway reads agents.entries (keyed by id) first, then the legacy agents.list array.
    private static IEnumerable<RosterAgent> RosterAgents(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Object)
            yield break;
        if (agents.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in entries.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object)
                    yield return new RosterAgent(entry.Name, entry.Value, Listed: false);
            yield break;
        }
        if (agents.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in list.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                    yield return new RosterAgent(id.GetString()!, entry, Listed: true);
        }
    }

    private sealed record RosterAgent(string Id, JsonElement Entry, bool Listed);
}
