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

    // Durable argument binding. Null means the rule authorizes the executable for any
    // arguments; non-null pins the rule to one argument form. Every rule this node
    // generates carries one, so a generated rule always describes a single operation.
    public string? ArgPattern { get; set; }

    // Provenance. "allow-always" marks a rule this node generated from an operator's
    // Allow always decision; a hand-written rule has none. Matching depends on this:
    // a generated rule that carries no argument binding predates argument binding and
    // is not honored, while a hand-written path-only rule is deliberate and is.
    public string? Source { get; set; }

    // Human-readable command this rule was created from. Display and audit only:
    // never an input to matching, so a rewritten commandText cannot widen a rule.
    //
    // This does mean approved argument text lands in the approvals file. That is
    // inherent to the model rather than a property of this field: ArgPattern above
    // encodes the exact argv and is load-bearing for authorization, so redacting
    // command text here would not keep arguments off disk, it would only make the
    // file unreadable to an operator auditing what they approved. The file is also
    // returned verbatim by system.execApprovals.get, whose hash is the concurrency
    // token for a matching set, so the returned bytes cannot be filtered without
    // breaking that read-modify-write contract. Operators who must keep secrets out
    // of the file should not pass them as command arguments.
    public string? CommandText { get; set; }

    public double? LastUsedAt { get; set; }
    public string? LastResolvedPath { get; set; }

    // Last command observed for this rule. Same disclosure note as CommandText.
    public string? LastUsedCommand { get; set; }
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

public sealed record ExecApprovalsSnapshot(
    string Path,
    bool Exists,
    string Hash,
    ExecApprovalsFile File);

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
