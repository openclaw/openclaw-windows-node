using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.ExecApprovals;

// ── Config enums ──────────────────────────────────────────────────────────────

public enum ExecSecurity
{
    Deny,
    Allowlist,
    Full,
}

public enum ExecAsk
{
    Off,
    OnMiss,
    Always,
    Deny,
}

public enum ExecApprovalDecision
{
    Allow,
    Deny,
    AllowOnce,
    AllowAlways,
}

// ── Allowlist contracts ───────────────────────────────────────────────────────

public sealed class ExecAllowlistEntry
{
    public Guid? Id { get; set; }
    public string? Pattern { get; set; }
    public double? LastUsedAt { get; set; }
    public string? LastResolvedPath { get; set; }
}

// ── Persisted config contracts ────────────────────────────────────────────────

public sealed class ExecApprovalsSocketConfig
{
    public string? Path { get; set; }
    public string? Token { get; set; }
}

public sealed class ExecApprovalsDefaults
{
    public ExecSecurity? Security { get; set; }
    public ExecAsk? Ask { get; set; }
    [JsonConverter(typeof(ExecSecurityFallbackConverter))]
    public ExecSecurity? AskFallback { get; set; }
    public bool? AutoAllowSkills { get; set; }
}

public sealed class ExecApprovalsAgent
{
    public ExecSecurity? Security { get; set; }
    public ExecAsk? Ask { get; set; }
    [JsonConverter(typeof(ExecSecurityFallbackConverter))]
    public ExecSecurity? AskFallback { get; set; }
    public bool? AutoAllowSkills { get; set; }
    public List<ExecAllowlistEntry>? Allowlist { get; set; }
}

public sealed class ExecApprovalsFile
{
    public int? Version { get; set; }
    public ExecApprovalsSocketConfig? Socket { get; set; }
    public ExecApprovalsDefaults? Defaults { get; set; }
    public Dictionary<string, ExecApprovalsAgent>? Agents { get; set; }
}

internal sealed class ExecSecurityFallbackConverter : JsonConverter<ExecSecurity?>
{
    public override ExecSecurity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("askFallback must be a string");
        return reader.GetString()?.ToLowerInvariant() switch
        {
            "deny" or "always" => ExecSecurity.Deny,
            "allowlist" or "on-miss" => ExecSecurity.Allowlist,
            "full" or "off" => ExecSecurity.Full,
            var value => throw new JsonException($"Unsupported askFallback value: {value}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ExecSecurity? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStringValue(value.Value switch
        {
            ExecSecurity.Deny => "deny",
            ExecSecurity.Allowlist => "allowlist",
            ExecSecurity.Full => "full",
            _ => throw new JsonException($"Unsupported askFallback value: {value}"),
        });
    }
}

// ── Resolved/runtime contracts (not serialized) ───────────────────────────────

public sealed class ExecApprovalsResolvedDefaults
{
    public ExecSecurity Security { get; init; }
    public ExecAsk Ask { get; init; }
    public ExecSecurity AskFallback { get; init; }
    public bool AutoAllowSkills { get; init; }
}

public sealed class ExecApprovalsResolved
{
    public string AgentId { get; init; } = string.Empty;
    public ExecApprovalsResolvedDefaults Defaults { get; init; } = null!;
    public IReadOnlyList<ExecAllowlistEntry> Allowlist { get; init; } = [];
    public string? SocketToken { get; init; }
}
