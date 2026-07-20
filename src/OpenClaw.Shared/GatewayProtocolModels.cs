using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared;

// ─────────────────────────────────────────────────────────────────────────────
// Gateway protocol DTOs.
//
// These mirror the canonical upstream gateway protocol
// (openclaw/openclaw, packages/gateway-protocol/src/schema) for:
//   • commands.list           - the command catalog
//   • sessions.patch          - the extended per-session preference field set
//   • sessions.files.list/get - session workspace file rail + browser
//   • sessions.compaction.*   - compaction checkpoint list/get/branch/restore
//
// Wire field names and value sets match the upstream TypeBox schemas exactly,
// which use `additionalProperties: false` (strict) - so request payloads must
// only carry known fields with the documented value sets.
//
// List/get/mutation result DTOs carry an `IsSupported` flag. Older gateways
// that do not implement a method report "unknown method", which the client
// surfaces as a typed result with `IsSupported = false` rather than throwing -
// see OpenClawGatewayClient.Protocol.cs.
// ─────────────────────────────────────────────────────────────────────────────

// ── Command catalog (commands.list) ──

/// <summary>A static argument choice: machine value plus a user-facing label.</summary>
public sealed class GatewayCommandArgChoice
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>A single declared argument for a gateway command.</summary>
public sealed class GatewayCommandArg
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Declared argument type: "string", "number", or "boolean".</summary>
    public string? Type { get; set; }

    public bool Required { get; set; }

    /// <summary>True when the choice set is resolved dynamically by the gateway at invocation time.</summary>
    public bool IsDynamic { get; set; }

    /// <summary>Static enumerated choices, when the gateway declares them.</summary>
    public IReadOnlyList<GatewayCommandArgChoice> Choices { get; set; } = Array.Empty<GatewayCommandArgChoice>();
}

/// <summary>A single command entry from the gateway command catalog (commands.list).</summary>
public sealed class GatewayCommand
{
    /// <summary>Catalog command name (the value the catalog advertises for this surface).</summary>
    public string Name { get; set; } = "";

    /// <summary>Native/platform command name, when distinct from <see cref="Name"/>.</summary>
    public string? NativeName { get; set; }

    /// <summary>Alternate text triggers (slash-prefixed) that resolve to this command.</summary>
    public IReadOnlyList<string> TextAliases { get; set; } = Array.Empty<string>();

    public string? Description { get; set; }

    /// <summary>UI grouping: session, options, status, management, media, tools, docks.</summary>
    public string? Category { get; set; }

    /// <summary>Contributing source system: "native", "skill", or "plugin".</summary>
    public string? Source { get; set; }

    /// <summary>Invocation surface: "text", "native", or "both".</summary>
    public string? Scope { get; set; }

    public bool AcceptsArgs { get; set; }

    public IReadOnlyList<GatewayCommandArg> Args { get; set; } = Array.Empty<GatewayCommandArg>();
}

/// <summary>
/// Typed result of <c>commands.list</c> (<c>{ commands: [...] }</c>). The gateway
/// does not return facet lists, so <see cref="Categories"/>, <see cref="Scopes"/>
/// and <see cref="Sources"/> are derived from the returned commands to give the
/// UI a stable vocabulary.
/// </summary>
public sealed class CommandCatalog
{
    public IReadOnlyList<GatewayCommand> Commands { get; set; } = Array.Empty<GatewayCommand>();

    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Sources { get; set; } = Array.Empty<string>();

    /// <summary>False when the connected gateway does not implement <c>commands.list</c> (older gateway).</summary>
    public bool IsSupported { get; set; } = true;

    public int Count => Commands.Count;
}

/// <summary>
/// Client-side filter applied over a <see cref="CommandCatalog"/>. The gateway's
/// <c>commands.list</c> only accepts <c>scope</c>/<c>includeArgs</c>/<c>provider</c>/<c>agentId</c>
/// server-side filters (no category/source/text search), so filtering locally
/// covers all facets and keeps the full catalog cached for cheap re-filtering.
/// </summary>
public sealed class CommandCatalogQuery
{
    /// <summary>Match command category (session, options, status, management, media, tools, docks).</summary>
    public string? Category { get; set; }

    /// <summary>Match command source (native, skill, plugin).</summary>
    public string? Source { get; set; }

    /// <summary>Match command scope (text, native, both).</summary>
    public string? Scope { get; set; }

    /// <summary>Case-insensitive substring matched against name, native name, aliases, and description.</summary>
    public string? Search { get; set; }

    /// <summary>When set, only commands whose <see cref="GatewayCommand.AcceptsArgs"/> matches are kept.</summary>
    public bool? AcceptsArgs { get; set; }

    public bool HasFilter =>
        !string.IsNullOrWhiteSpace(Category) ||
        !string.IsNullOrWhiteSpace(Source) ||
        !string.IsNullOrWhiteSpace(Scope) ||
        !string.IsNullOrWhiteSpace(Search) ||
        AcceptsArgs.HasValue;

    public bool Matches(GatewayCommand command)
    {
        if (command is null) return false;

        if (!string.IsNullOrWhiteSpace(Category) &&
            !string.Equals(command.Category, Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(Source) &&
            !string.Equals(command.Source, Source, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(Scope))
        {
            // Mirror gateway commands.list scope semantics: a "both" filter
            // returns everything, and a "text" or "native" filter also includes
            // commands available on "both" surfaces.
            var wanted = Scope.Trim();
            if (!string.Equals(wanted, "both", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command.Scope, "both", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command.Scope, wanted, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (AcceptsArgs.HasValue && command.AcceptsArgs != AcceptsArgs.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var needle = Search.Trim();
            var inName = command.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var inNative = command.NativeName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
            var inDesc = command.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
            var inAlias = command.TextAliases.Any(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (!inName && !inNative && !inDesc && !inAlias)
                return false;
        }

        return true;
    }
}

// ── Session patch (sessions.patch) ──

/// <summary>Fast-mode setting: off, on, or "auto" (gateway picks per turn).</summary>
public enum SessionFastMode
{
    Off,
    On,
    Auto
}

/// <summary>Response-usage footer detail level (gateway <c>responseUsage</c>).</summary>
public enum ResponseUsageMode
{
    Off,
    Tokens,
    Full,

    /// <summary>Legacy alias the gateway still accepts for backward compatibility.</summary>
    On
}

/// <summary>Per-session message send policy.</summary>
public enum SessionSendPolicy
{
    Allow,
    Deny
}

/// <summary>Group activation mode for multi-party sessions.</summary>
public enum SessionGroupActivation
{
    Mention,
    Always
}

/// <summary>
/// Non-generic sentinel that clears any <see cref="PatchField{T}"/> - i.e. emits
/// an explicit JSON <c>null</c> for that field, which the gateway treats as
/// "remove this session override". Obtain it via <see cref="SessionPatch.Clear"/>.
/// </summary>
public readonly struct PatchFieldClear
{
}

/// <summary>
/// A tri-state patch field: <b>unset</b> (omitted from the request), <b>set</b>
/// to a concrete value, or <b>cleared</b> (sent as explicit JSON <c>null</c> to
/// remove the override). Implicitly converts from a value (<c>field = value</c>)
/// and from <see cref="SessionPatch.Clear"/> (<c>field = SessionPatch.Clear</c>),
/// so the common cases stay terse. The default value is <b>unset</b>.
/// </summary>
public readonly struct PatchField<T>
{
    private readonly T _value;

    private PatchField(bool specified, bool clear, T value)
    {
        IsSpecified = specified;
        IsClear = clear;
        _value = value;
    }

    /// <summary>True when the field participates in the payload (either a value or an explicit null).</summary>
    public bool IsSpecified { get; }

    /// <summary>True when the field should be emitted as explicit JSON <c>null</c> (clear the override).</summary>
    public bool IsClear { get; }

    /// <summary>True when the field carries a concrete value to send.</summary>
    public bool HasValue => IsSpecified && !IsClear;

    /// <summary>The concrete value; only meaningful when <see cref="HasValue"/> is true.</summary>
    public T Value => _value;

    /// <summary>Sets the field to a concrete value. A null reference maps to unset.</summary>
    public static PatchField<T> Set(T? value) => value is null ? default : new PatchField<T>(true, false, value);

    /// <summary>A field cleared to explicit JSON <c>null</c>. Callers use <see cref="SessionPatch.Clear"/>.</summary>
    private static PatchField<T> ClearField { get; } = new PatchField<T>(true, true, default!);

    public static implicit operator PatchField<T>(T? value) => Set(value);

    public static implicit operator PatchField<T>(PatchFieldClear _) => ClearField;
}

/// <summary>
/// Extended <c>sessions.patch</c> field set. Each field is tri-state via
/// <see cref="PatchField{T}"/>: leave it unset to omit it, assign a value to set
/// it, or assign <see cref="Clear"/> to remove the session override (explicit
/// JSON null). Field names and value sets match the upstream
/// <c>SessionsPatchParamsSchema</c> exactly (see <see cref="ToPayload"/>), whose
/// fields are <c>Union([&lt;value&gt;, Null])</c> - so null is a valid clear signal.
/// </summary>
public sealed class SessionPatch
{
    /// <summary>
    /// Assign to any patch field to clear that session override (emits explicit
    /// JSON <c>null</c>), e.g. <c>patch.Model = SessionPatch.Clear;</c>.
    /// </summary>
    public static PatchFieldClear Clear => default;

    public PatchField<string> Model { get; set; }
    public PatchField<string> ThinkingLevel { get; set; }
    public PatchField<SessionFastMode> FastMode { get; set; }
    public PatchField<string> VerboseLevel { get; set; }
    public PatchField<string> TraceLevel { get; set; }
    public PatchField<string> ReasoningLevel { get; set; }
    public PatchField<ResponseUsageMode> ResponseUsage { get; set; }
    public PatchField<string> ElevatedLevel { get; set; }
    public PatchField<string> ExecHost { get; set; }
    public PatchField<string> ExecSecurity { get; set; }
    public PatchField<string> ExecAsk { get; set; }
    public PatchField<string> ExecNode { get; set; }
    public PatchField<SessionSendPolicy> SendPolicy { get; set; }
    public PatchField<SessionGroupActivation> GroupActivation { get; set; }

    /// <summary>
    /// True when the patch would change something: any field cleared, or any
    /// field set to a meaningful value (a blank string value produces nothing).
    /// </summary>
    public bool HasChanges =>
        ProducesString(Model) || ProducesString(ThinkingLevel) || FastMode.IsSpecified ||
        ProducesString(VerboseLevel) || ProducesString(TraceLevel) || ProducesString(ReasoningLevel) ||
        ResponseUsage.IsSpecified || ProducesString(ElevatedLevel) || ProducesString(ExecHost) ||
        ProducesString(ExecSecurity) || ProducesString(ExecAsk) || ProducesString(ExecNode) ||
        SendPolicy.IsSpecified || GroupActivation.IsSpecified;

    /// <summary>
    /// Builds the <c>sessions.patch</c> request parameters: always includes
    /// <c>key</c>, plus every field that participates. A cleared field is emitted
    /// as explicit <c>null</c>; a set field uses the gateway's exact wire value.
    /// String fields map to the schema's <c>NonEmptyString</c> value type, so a
    /// blank/whitespace <i>value</i> is omitted (an empty string would be
    /// rejected and fail the whole patch) - clearing always uses null instead.
    /// </summary>
    internal Dictionary<string, object?> ToPayload(string key)
    {
        var payload = new Dictionary<string, object?> { ["key"] = key };
        AddString(payload, "model", Model);
        AddString(payload, "thinkingLevel", ThinkingLevel);
        AddEncoded(payload, "fastMode", FastMode, EncodeFastMode);
        AddString(payload, "verboseLevel", VerboseLevel);
        AddString(payload, "traceLevel", TraceLevel);
        AddString(payload, "reasoningLevel", ReasoningLevel);
        AddEncoded(payload, "responseUsage", ResponseUsage, EncodeResponseUsage);
        AddString(payload, "elevatedLevel", ElevatedLevel);
        AddString(payload, "execHost", ExecHost);
        AddString(payload, "execSecurity", ExecSecurity);
        AddString(payload, "execAsk", ExecAsk);
        AddString(payload, "execNode", ExecNode);
        AddEncoded(payload, "sendPolicy", SendPolicy, p => p == SessionSendPolicy.Allow ? "allow" : "deny");
        AddEncoded(payload, "groupActivation", GroupActivation, g => g == SessionGroupActivation.Mention ? "mention" : "always");
        return payload;
    }

    // A blank-value string Set produces nothing (consistent with NonEmptyString);
    // a clear always produces an explicit null.
    private static bool ProducesString(PatchField<string> field)
        => field.IsClear || (field.HasValue && !string.IsNullOrWhiteSpace(field.Value));

    private static void AddString(Dictionary<string, object?> payload, string name, PatchField<string> field)
    {
        if (field.IsClear)
            payload[name] = null;
        else if (field.HasValue && !string.IsNullOrWhiteSpace(field.Value))
            payload[name] = field.Value;
    }

    private static void AddEncoded<TValue>(
        Dictionary<string, object?> payload, string name, PatchField<TValue> field, Func<TValue, object> encode)
    {
        if (field.IsClear)
            payload[name] = null;
        else if (field.HasValue)
            payload[name] = encode(field.Value);
    }

    // fastMode is a union of boolean | "auto" on the wire.
    private static object EncodeFastMode(SessionFastMode mode) => mode switch
    {
        SessionFastMode.On => true,
        SessionFastMode.Off => false,
        _ => "auto"
    };

    private static object EncodeResponseUsage(ResponseUsageMode mode) => mode switch
    {
        ResponseUsageMode.Off => "off",
        ResponseUsageMode.Tokens => "tokens",
        ResponseUsageMode.Full => "full",
        _ => "on"
    };
}

// ── Session files (sessions.files.list / sessions.files.get) ──

/// <summary>A file referenced by a session transcript.</summary>
public sealed class SessionFileEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Relevance: "modified" or "read".</summary>
    public string? Kind { get; set; }

    /// <summary>True when the referenced file no longer exists on disk.</summary>
    public bool Missing { get; set; }

    public long? Size { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>File content (present on <c>sessions.files.get</c>; usually omitted in list results).</summary>
    public string? Content { get; set; }
}

/// <summary>One file or folder in the session-rooted workspace browser.</summary>
public sealed class SessionFileBrowserEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"file" or "directory".</summary>
    public string? Kind { get; set; }

    /// <summary>Session relevance for this entry: "modified", "read", or "mixed".</summary>
    public string? SessionKind { get; set; }

    public long? Size { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public bool IsDirectory => string.Equals(Kind, "directory", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Folder listing or search result rooted at the session workspace.</summary>
public sealed class SessionFileBrowser
{
    public string Path { get; set; } = "";
    public string? ParentPath { get; set; }
    public string? Search { get; set; }
    public IReadOnlyList<SessionFileBrowserEntry> Entries { get; set; } = Array.Empty<SessionFileBrowserEntry>();
    public bool Truncated { get; set; }
}

/// <summary>Typed result of <c>sessions.files.list</c>.</summary>
public sealed class SessionFileList
{
    public string Key { get; set; } = "";

    /// <summary>Absolute workspace root reported by the gateway, when available.</summary>
    public string? Root { get; set; }

    public IReadOnlyList<SessionFileEntry> Files { get; set; } = Array.Empty<SessionFileEntry>();

    /// <summary>Folder browser listing, present when a <c>path</c>/<c>search</c> was requested.</summary>
    public SessionFileBrowser? Browser { get; set; }

    /// <summary>False when the connected gateway does not implement the method (older gateway).</summary>
    public bool IsSupported { get; set; } = true;
}

/// <summary>Typed result of <c>sessions.files.get</c>.</summary>
public sealed class SessionFileContent
{
    public string Key { get; set; } = "";
    public string? Root { get; set; }
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? Content { get; set; }
    public long? Size { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>True when the gateway reported the file as missing.</summary>
    public bool Missing { get; set; }

    /// <summary>True when readable content was returned.</summary>
    public bool Found => !Missing && Content is not null;

    /// <summary>False when the connected gateway does not implement the method (older gateway).</summary>
    public bool IsSupported { get; set; } = true;
}

// ── Compaction checkpoints (sessions.compaction.*) ──

/// <summary>Stored compaction checkpoint metadata for branching or restoring a session.</summary>
public sealed class SessionCompactionCheckpoint
{
    /// <summary>Checkpoint id (gateway <c>checkpointId</c>).</summary>
    public string Id { get; set; } = "";

    public string? SessionKey { get; set; }
    public string? SessionId { get; set; }
    public DateTime? CreatedAt { get; set; }

    /// <summary>Why the checkpoint was created: manual, auto-threshold, overflow-retry, timeout-retry.</summary>
    public string? Reason { get; set; }

    public long? TokensBefore { get; set; }
    public long? TokensAfter { get; set; }
    public string? Summary { get; set; }
    public string? FirstKeptEntryId { get; set; }
}

/// <summary>Typed result of <c>sessions.compaction.list</c>.</summary>
public sealed class SessionCompactionCheckpointList
{
    public string Key { get; set; } = "";
    public IReadOnlyList<SessionCompactionCheckpoint> Checkpoints { get; set; } = Array.Empty<SessionCompactionCheckpoint>();

    /// <summary>False when the connected gateway does not implement the method (older gateway).</summary>
    public bool IsSupported { get; set; } = true;
}

/// <summary>Typed result of <c>sessions.compaction.get</c>.</summary>
public sealed class SessionCompactionCheckpointResult
{
    public string Key { get; set; } = "";
    public SessionCompactionCheckpoint? Checkpoint { get; set; }

    /// <summary>True when a checkpoint was returned.</summary>
    public bool Found => Checkpoint is not null;

    /// <summary>False when the connected gateway does not implement the method (older gateway).</summary>
    public bool IsSupported { get; set; } = true;
}

/// <summary>
/// Typed result of a compaction mutation (<c>sessions.compaction.branch</c> or
/// <c>sessions.compaction.restore</c>).
/// </summary>
public sealed class SessionCompactionMutationResult
{
    public bool Ok { get; set; }

    /// <summary>The session key the caller acted on.</summary>
    public string Key { get; set; } = "";

    public string? CheckpointId { get; set; }

    /// <summary>For branch: the original session key (gateway <c>sourceKey</c>).</summary>
    public string? SourceKey { get; set; }

    /// <summary>
    /// The resulting session key: the newly created branch key for branch, or
    /// the restored session key for restore (gateway <c>key</c>).
    /// </summary>
    public string? ResultSessionKey { get; set; }

    public string? SessionId { get; set; }
    public SessionCompactionCheckpoint? Checkpoint { get; set; }

    public string? Error { get; set; }

    /// <summary>False when the connected gateway does not implement the method (older gateway).</summary>
    public bool IsSupported { get; set; } = true;
}
