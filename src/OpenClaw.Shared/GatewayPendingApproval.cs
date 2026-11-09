// Typed observation of Gateway approvals that wait for an operator decision
// (exec, plugin and OpenClaw system-agent approvals). Parses the top-level
// "<prefix>.approval.requested|resolved" events and the "<prefix>.approval.list"
// replies into one record shape, mirroring the web Control UI's exec-approval
// parser. Command, scope and warning text are untrusted display data: callers
// must never log or export them.
//
// Usage:
//   if (GatewayPendingApprovalParser.TryGetEventKind(eventName, out var kind, out var requested))
//       if (requested && GatewayPendingApprovalParser.TryParse(kind, payload, out var approval)) Show(approval);
//   var pending = GatewayPendingApprovalParser.ParseList(GatewayApprovalKind.Exec, listReply);

using System.Collections.Immutable;
using System.Text.Json;

namespace OpenClaw.Shared;

public enum GatewayApprovalKind
{
    Exec,
    Plugin,
    SystemAgent,
}

public enum GatewayApprovalScopeKind
{
    StandingGrant,
    MessageSend,
    Payment,
    ExternalPost,
    Unreviewable,
}

/// <summary>What an approval would allow, when the Gateway states it (for example a payment target).</summary>
public sealed record GatewayApprovalScope(GatewayApprovalScopeKind Kind)
{
    public string? Automation { get; init; }
    /// <summary>Exact command bound by a standing grant, distinct from the displayed command.</summary>
    public string? Command { get; init; }
    /// <summary>Publication audience stated by an external-post scope.</summary>
    public string? Visibility { get; init; }
    public int? ExpiresInDays { get; init; }
    public string? Target { get; init; }
    public int RecipientCount { get; init; }
    internal const int MaxListedRecipients = 5;
    internal const int MaxRecipientLength = 128;
    private readonly IReadOnlyList<string>? _recipients;
    /// <summary>Exact optional recipient sample. Null means absent, not an explicitly empty list.</summary>
    public IReadOnlyList<string>? Recipients
    {
        get => _recipients;
        init => _recipients = value?.ToImmutableList();
    }
    /// <summary>Exact internal/external message audience, when supplied by the Gateway.</summary>
    public string? Audience { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    // Unsupported wire scope still changes observation identity, but is never displayed or logged.
    internal string? UnsupportedBinding { get; init; }

    internal bool HasValidMessageSendDetails =>
        (Audience is null or "internal" or "external") &&
        (Recipients is null || (Recipients.Count <= MaxListedRecipients && Recipients.All(IsValidRecipient)));

    // JSON Schema string lengths count Unicode code points, not UTF-16 code units.
    internal static bool IsValidRecipient(string? value) => !string.IsNullOrEmpty(value) &&
        value.EnumerateRunes().Take(MaxRecipientLength + 1).Count() <= MaxRecipientLength;

    /// <summary>Scope equality is ordinal and structural, including recipient order and absent versus empty.</summary>
    public bool Equals(GatewayApprovalScope? other) =>
        other is not null && Kind == other.Kind && Automation == other.Automation &&
        Command == other.Command && Visibility == other.Visibility && ExpiresInDays == other.ExpiresInDays &&
        Target == other.Target && RecipientCount == other.RecipientCount && Audience == other.Audience &&
        Amount == other.Amount && Currency == other.Currency && UnsupportedBinding == other.UnsupportedBinding &&
        (Recipients is null ? other.Recipients is null :
            other.Recipients is not null && Recipients.SequenceEqual(other.Recipients, StringComparer.Ordinal));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Automation, StringComparer.Ordinal);
        hash.Add(Command, StringComparer.Ordinal);
        hash.Add(Visibility, StringComparer.Ordinal);
        hash.Add(ExpiresInDays);
        hash.Add(Target, StringComparer.Ordinal);
        hash.Add(RecipientCount);
        hash.Add(Recipients is not null);
        if (Recipients is not null)
            foreach (var recipient in Recipients)
                hash.Add(recipient, StringComparer.Ordinal);
        hash.Add(Audience, StringComparer.Ordinal);
        hash.Add(Amount, StringComparer.Ordinal);
        hash.Add(Currency, StringComparer.Ordinal);
        hash.Add(UnsupportedBinding, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>One approval waiting for a decision. Times are Gateway epoch milliseconds.</summary>
public sealed record GatewayPendingApproval(
    string Id,
    GatewayApprovalKind Kind,
    string Command,
    long CreatedAtMs,
    long ExpiresAtMs)
{
    /// <summary>Plugin or system-agent request title; null for exec approvals.</summary>
    public string? Title { get; init; }
    public string? Description { get; init; }
    /// <summary>Full reviewer-only detail supplied by a plugin, such as the SQL being authorized.</summary>
    public string? Detail { get; init; }
    /// <summary>Opaque system-agent proposal binding. Retained for observation identity, not display.</summary>
    public string? ProposalHash { get; init; }
    public string? WarningText { get; init; }
    public GatewayApprovalScope? Scope { get; init; }
    public string? Cwd { get; init; }
    public string? Host { get; init; }
    public string? SessionKey { get; init; }
    public string? AgentId { get; init; }
    /// <summary>Exec ask mode ("always" limits the default decisions to Allow once and Deny).</summary>
    public string? Ask { get; init; }
    private readonly IReadOnlyList<string>? _allowedDecisions;
    /// <summary>Known decisions stated by the request; see <see cref="GatewayApprovalDecisions.Effective"/>.</summary>
    public IReadOnlyList<string> AllowedDecisions { get => _allowedDecisions ?? []; init => _allowedDecisions = value; }
    /// <summary>
    /// True when the request stated its decisions, even if none are known to this client; then
    /// only those decisions are offered instead of the defaults.
    /// </summary>
    public bool AllowedDecisionsSpecified => _allowedDecisions is not null;

    /// <summary>Compares every observed request field, including the explicit decision sequence.</summary>
    public bool HasSameContentAs(GatewayPendingApproval? other) =>
        other is not null &&
        Id == other.Id && Kind == other.Kind && Command == other.Command &&
        CreatedAtMs == other.CreatedAtMs && ExpiresAtMs == other.ExpiresAtMs &&
        Title == other.Title && Description == other.Description && Detail == other.Detail &&
        ProposalHash == other.ProposalHash &&
        WarningText == other.WarningText && Scope == other.Scope &&
        Cwd == other.Cwd && Host == other.Host && SessionKey == other.SessionKey &&
        AgentId == other.AgentId && Ask == other.Ask &&
        AllowedDecisionsSpecified == other.AllowedDecisionsSpecified &&
        AllowedDecisions.SequenceEqual(other.AllowedDecisions, StringComparer.Ordinal);
}

/// <summary>
/// Gateway epoch-millisecond timestamps are untrusted. Values outside the range that
/// <see cref="DateTimeOffset"/> can represent make the BCL throw, so every conversion
/// goes through these checks instead of <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/>.
/// </summary>
public static class GatewayUnixTime
{
    public static readonly long MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    public static readonly long MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static bool IsValid(long ms) => ms >= MinMs && ms <= MaxMs;

    /// <summary>Converts epoch milliseconds, clamping out-of-range values to the representable range.</summary>
    public static DateTimeOffset ToDateTimeOffset(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, MinMs, MaxMs));

    /// <summary>Adds milliseconds without overflowing the representable range.</summary>
    public static DateTimeOffset Add(DateTimeOffset value, long ms)
    {
        var target = Math.Clamp((decimal)value.ToUnixTimeMilliseconds() + ms, MinMs, MaxMs);
        return DateTimeOffset.FromUnixTimeMilliseconds((long)target).ToOffset(value.Offset);
    }
}

public static class GatewayPendingApprovalParser
{
    private static readonly IReadOnlyList<string> KnownDecisions = GatewayApprovalDecisions.All;

    /// <summary>Gateway RPC that lists pending approvals of <paramref name="kind"/>.</summary>
    public static string ListMethod(GatewayApprovalKind kind) => kind switch
    {
        GatewayApprovalKind.Plugin => "plugin.approval.list",
        GatewayApprovalKind.SystemAgent => "openclaw.approval.list",
        _ => "exec.approval.list",
    };

    /// <summary>Maps a top-level approval event name to its kind and phase.</summary>
    public static bool TryGetEventKind(string? eventName, out GatewayApprovalKind kind, out bool requested)
    {
        kind = GatewayApprovalKind.Exec;
        requested = false;
        if (string.IsNullOrEmpty(eventName))
            return false;

        var separator = eventName.IndexOf(".approval.", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        switch (eventName[..separator])
        {
            case "exec": kind = GatewayApprovalKind.Exec; break;
            case "plugin": kind = GatewayApprovalKind.Plugin; break;
            case "openclaw": kind = GatewayApprovalKind.SystemAgent; break;
            default: return false;
        }

        switch (eventName[(separator + ".approval.".Length)..])
        {
            case "requested": requested = true; return true;
            case "resolved": requested = false; return true;
            default: return false;
        }
    }

    /// <summary>Reads the approval id from a resolved event payload.</summary>
    public static bool TryParseResolvedId(JsonElement payload, out string id)
    {
        id = ReadString(payload, "id") ?? "";
        return id.Length > 0;
    }

    /// <summary>Parses a requested-event payload or one list entry; rejects incomplete records.</summary>
    public static bool TryParse(GatewayApprovalKind kind, JsonElement payload, out GatewayPendingApproval approval)
    {
        approval = null!;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("request", out var request) ||
            request.ValueKind != JsonValueKind.Object)
            return false;

        var id = ReadString(payload, "id");
        var createdAtMs = ReadInt64(payload, "createdAtMs");
        var expiresAtMs = ReadInt64(payload, "expiresAtMs");
        // Out-of-range times cannot be shown or scheduled; treat them like missing ones.
        if (id is null || createdAtMs <= 0 || expiresAtMs <= 0 ||
            !GatewayUnixTime.IsValid(createdAtMs) || !GatewayUnixTime.IsValid(expiresAtMs))
            return false;

        var detail = default(JsonElement);
        if (kind == GatewayApprovalKind.Plugin && request.TryGetProperty("detail", out detail) &&
            detail.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return false;

        var title = ReadString(request, "title");
        string? command;
        IReadOnlyList<string>? decisions = ReadDecisions(request);
        switch (kind)
        {
            case GatewayApprovalKind.Exec:
                // Like the native chat banner, an exec request without a command stays
                // visible so it can be denied; GatewayApprovalDecisions offers only Deny.
                command = request.TryGetProperty("command", out var commandValue) && commandValue.ValueKind == JsonValueKind.String
                    ? ReadString(request, "command") ?? ""
                    : null;
                title = null;
                break;
            case GatewayApprovalKind.Plugin:
                command = title;
                break;
            default:
                command = ReadString(request, "command");
                if (title is null || ReadString(request, "description") is null || ReadString(request, "proposalHash") is null)
                    return false;
                decisions = [GatewayApprovalDecisions.AllowOnce, GatewayApprovalDecisions.Deny];
                break;
        }
        if (command is null)
            return false;

        approval = new GatewayPendingApproval(id, kind, command, createdAtMs, expiresAtMs)
        {
            Title = title,
            Description = kind == GatewayApprovalKind.Exec ? null : ReadString(request, "description"),
            Detail = detail.ValueKind == JsonValueKind.String ? detail.GetString() : null,
            ProposalHash = kind == GatewayApprovalKind.SystemAgent ? request.GetProperty("proposalHash").GetString() : null,
            WarningText = ReadString(request, "warningText"),
            Scope = ReadScope(request),
            Cwd = ReadString(request, "cwd"),
            Host = ReadString(request, "host"),
            SessionKey = ReadString(request, "sessionKey"),
            AgentId = ReadString(request, "agentId"),
            Ask = kind == GatewayApprovalKind.Exec ? ReadString(request, "ask") : null,
        };
        if (decisions is not null)
            approval = approval with { AllowedDecisions = decisions };
        return true;
    }

    /// <summary>Parses an <c>*.approval.list</c> reply (an array of request records); skips invalid entries.</summary>
    public static IReadOnlyList<GatewayPendingApproval> ParseList(GatewayApprovalKind kind, JsonElement reply)
    {
        var items = reply.ValueKind switch
        {
            JsonValueKind.Array => reply,
            JsonValueKind.Object when reply.TryGetProperty("approvals", out var nested) && nested.ValueKind == JsonValueKind.Array => nested,
            _ => default,
        };
        if (items.ValueKind != JsonValueKind.Array)
            return [];

        var approvals = new List<GatewayPendingApproval>();
        foreach (var item in items.EnumerateArray())
        {
            if (TryParse(kind, item, out var approval))
                approvals.Add(approval);
        }
        return approvals;
    }

    /// <summary>
    /// Reads a complete pending inventory for decision reconciliation. Unlike tolerant ParseList,
    /// malformed rows, duplicate IDs and unknown envelope metadata cannot prove absence.
    /// </summary>
    public static bool TryParseCompleteList(GatewayApprovalKind kind, JsonElement reply,
        out IReadOnlyList<GatewayPendingApproval> approvals)
    {
        approvals = [];
        var rows = reply;
        if (reply.ValueKind == JsonValueKind.Object)
        {
            if (reply.EnumerateObject().Count() != 1 || !reply.TryGetProperty("approvals", out rows))
                return false;
        }
        if (rows.ValueKind != JsonValueKind.Array)
            return false;
        var parsed = new List<GatewayPendingApproval>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (!TryParse(kind, row, out var approval) || !ids.Add(approval.Id))
                return false;
            parsed.Add(approval);
        }
        approvals = parsed;
        return true;
    }

    private static GatewayApprovalScope? ReadScope(JsonElement request)
    {
        if (!request.TryGetProperty("scope", out var scope) || scope.ValueKind == JsonValueKind.Null)
            return null;
        if (scope.ValueKind == JsonValueKind.Object && ReadKnownScope(scope) is { } parsed &&
            HasOnlyScopeFields(scope, parsed.Kind))
            return parsed;
        // Dropping an unknown/malformed scope would turn it into an apparently unscoped permit.
        return new(GatewayApprovalScopeKind.Unreviewable)
        {
            UnsupportedBinding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(scope.GetRawText()))),
        };
    }

    private static bool HasOnlyScopeFields(JsonElement scope, GatewayApprovalScopeKind kind)
    {
        string[] fields = kind switch
        {
            GatewayApprovalScopeKind.StandingGrant => ["kind", "automation", "command", "expiresInDays"],
            GatewayApprovalScopeKind.MessageSend => ["kind", "target", "recipientCount", "recipients", "audience"],
            GatewayApprovalScopeKind.Payment => ["kind", "target", "amount", "currency"],
            GatewayApprovalScopeKind.ExternalPost => ["kind", "target", "visibility"],
            _ => [],
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (scope.EnumerateObject().Any(field => !fields.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)))
            return false;
        return !scope.TryGetProperty("expiresInDays", out var days) || days.ValueKind == JsonValueKind.Null ||
            (days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out _));
    }

    private static GatewayApprovalScope? ReadKnownScope(JsonElement scope)
    {
        switch (scope.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null)
        {
            case "standing-grant":
                return ReadString(scope, "automation") is not null && ReadString(scope, "command") is not null
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant)
                    {
                        Automation = scope.GetProperty("automation").GetString(),
                        Command = scope.GetProperty("command").GetString(),
                        ExpiresInDays = scope.TryGetProperty("expiresInDays", out var days) && days.ValueKind == JsonValueKind.Number &&
                            days.TryGetInt32(out var value) ? value : null,
                    }
                    : null;
            case "message-send":
                return ReadMessageSendScope(scope);
            case "payment":
                return ReadString(scope, "amount") is not null && ReadString(scope, "currency") is not null &&
                    ReadString(scope, "target") is not null
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.Payment) { Amount = scope.GetProperty("amount").GetString(), Currency = scope.GetProperty("currency").GetString(), Target = scope.GetProperty("target").GetString() }
                    : null;
            case "external-post":
                return ReadString(scope, "target") is not null &&
                    ReadString(scope, "visibility") is "public" or "restricted"
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.ExternalPost)
                    { Target = scope.GetProperty("target").GetString(), Visibility = scope.GetProperty("visibility").GetString() }
                    : null;
            default:
                return null;
        }
    }

    private static GatewayApprovalScope? ReadMessageSendScope(JsonElement scope)
    {
        if (ReadString(scope, "target") is null ||
            !scope.TryGetProperty("recipientCount", out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var recipientCount))
            return null;
        List<string>? recipients = null;
        if (scope.TryGetProperty("recipients", out var listed))
        {
            if (listed.ValueKind != JsonValueKind.Array || listed.GetArrayLength() > GatewayApprovalScope.MaxListedRecipients)
                return null;
            recipients = [];
            foreach (var item in listed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !GatewayApprovalScope.IsValidRecipient(item.GetString()))
                    return null;
                recipients.Add(item.GetString()!);
            }
        }
        string? audience = null;
        if (scope.TryGetProperty("audience", out var statedAudience))
        {
            if (statedAudience.ValueKind != JsonValueKind.String || statedAudience.GetString() is not ("internal" or "external"))
                return null;
            audience = statedAudience.GetString();
        }
        return new(GatewayApprovalScopeKind.MessageSend)
        {
            Target = scope.GetProperty("target").GetString(), RecipientCount = recipientCount,
            Recipients = recipients, Audience = audience,
        };
    }

    // Null when the request omits allowedDecisions (or sends JSON null); a malformed value states no known decision.
    private static IReadOnlyList<string>? ReadDecisions(JsonElement request)
    {
        if (!request.TryGetProperty("allowedDecisions", out var decisions) || decisions.ValueKind == JsonValueKind.Null)
            return null;
        if (decisions.ValueKind != JsonValueKind.Array)
            return [];
        return decisions.EnumerateArray()
            .Where(decision => decision.ValueKind == JsonValueKind.String)
            .Select(decision => decision.GetString()!)
            .Where(decision => KnownDecisions.Contains(decision, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static long ReadInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt64(out var whole))
            return whole;
        // Fractional or huge values: keep only those a long can hold; range checks follow at the call site.
        var number = value.GetDouble();
        return number is >= long.MinValue and < long.MaxValue ? (long)number : 0;
    }
}
