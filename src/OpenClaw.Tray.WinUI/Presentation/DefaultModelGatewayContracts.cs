using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Chat;

namespace OpenClawTray.Presentation;

/// <summary>What the Default model page knows about the active Gateway's model configuration.</summary>
internal sealed record DefaultModelGatewayState(
    bool IsConnected,
    bool CanWriteConfig,
    IReadOnlyList<ChatModelChoice> Models,
    string? PrimaryModel,
    bool LocalAiRouteAvailable)
{
    public static DefaultModelGatewayState Disconnected { get; } = new(false, false, [], null, false);
}

/// <summary>Reads and changes the Gateway's default (primary) agent model.</summary>
internal interface IDefaultModelGatewayService
{
    event EventHandler? Changed;
    DefaultModelGatewayState Current { get; }
    Task RefreshAsync();
    Task SetPrimaryModelAsync(string modelRef);
}

/// <summary>Pure helpers for the <c>config.get</c> envelope and the primary-model patch.</summary>
internal static class GatewayConfigEnvelope
{
    /// <summary>
    /// Unwraps <c>{ path, exists, raw, parsed }</c> (or <c>{ config, hash }</c>) into the config
    /// root and the optimistic-concurrency hash: <c>baseHash</c>, then <c>hash</c>, then SHA-256(raw).
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

    public static string? ReadPrimaryModel(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Object ||
            !agents.TryGetProperty("defaults", out JsonElement defaults) || defaults.ValueKind != JsonValueKind.Object ||
            !defaults.TryGetProperty("model", out JsonElement model))
            return null;
        if (model.ValueKind == JsonValueKind.String)
            return model.GetString();
        return model.ValueKind == JsonValueKind.Object &&
               model.TryGetProperty("primary", out JsonElement primary) && primary.ValueKind == JsonValueKind.String
            ? primary.GetString()
            : null;
    }

    /// <summary>
    /// Partial patch setting <c>agents.defaults.model.primary</c>. An existing model allowlist
    /// is extended additively, never replaced, so unrelated entries are untouched.
    /// </summary>
    public static JsonObject BuildPrimaryPatch(JsonElement config, string modelRef)
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
        return new JsonObject { ["agents"] = new JsonObject { ["defaults"] = defaults } };
    }
}
