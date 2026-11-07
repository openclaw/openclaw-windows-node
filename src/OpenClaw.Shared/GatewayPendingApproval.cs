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
}

/// <summary>What an approval would allow, when the Gateway states it (for example a payment target).</summary>
public sealed record GatewayApprovalScope(GatewayApprovalScopeKind Kind)
{
    public string? Automation { get; init; }
    public int? ExpiresInDays { get; init; }
    public string? Target { get; init; }
    public int RecipientCount { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
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
    public string? WarningText { get; init; }
    public GatewayApprovalScope? Scope { get; init; }
    public string? Cwd { get; init; }
    public string? Host { get; init; }
    public string? SessionKey { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<string> AllowedDecisions { get; init; } = [];
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
    private static readonly string[] KnownDecisions = ["allow-once", "allow-always", "deny"];

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

        var title = ReadString(request, "title");
        string? command;
        IReadOnlyList<string> decisions = ReadDecisions(request);
        switch (kind)
        {
            case GatewayApprovalKind.Exec:
                command = ReadString(request, "command");
                title = null;
                break;
            case GatewayApprovalKind.Plugin:
                command = title;
                break;
            default:
                command = ReadString(request, "command");
                if (title is null || ReadString(request, "description") is null || ReadString(request, "proposalHash") is null)
                    return false;
                decisions = ["allow-once", "deny"];
                break;
        }
        if (command is null)
            return false;

        approval = new GatewayPendingApproval(id, kind, command, createdAtMs, expiresAtMs)
        {
            Title = title,
            Description = kind == GatewayApprovalKind.Exec ? null : ReadString(request, "description"),
            WarningText = ReadString(request, "warningText"),
            Scope = ReadScope(request),
            Cwd = ReadString(request, "cwd"),
            Host = ReadString(request, "host"),
            SessionKey = ReadString(request, "sessionKey"),
            AgentId = ReadString(request, "agentId"),
            AllowedDecisions = decisions,
        };
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

    private static GatewayApprovalScope? ReadScope(JsonElement request)
    {
        if (!request.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            return null;

        switch (ReadString(scope, "kind"))
        {
            case "standing-grant":
                return ReadString(scope, "automation") is { } automation && ReadString(scope, "command") is not null
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant)
                    {
                        Automation = automation,
                        ExpiresInDays = scope.TryGetProperty("expiresInDays", out var days) && days.ValueKind == JsonValueKind.Number &&
                            days.TryGetInt32(out var value) ? value : null,
                    }
                    : null;
            case "message-send":
                return ReadString(scope, "target") is { } messageTarget &&
                    scope.TryGetProperty("recipientCount", out var count) && count.ValueKind == JsonValueKind.Number &&
                    count.TryGetInt32(out var recipients)
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend) { Target = messageTarget, RecipientCount = recipients }
                    : null;
            case "payment":
                return ReadString(scope, "amount") is { } amount && ReadString(scope, "currency") is { } currency &&
                    ReadString(scope, "target") is { } payee
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.Payment) { Amount = amount, Currency = currency, Target = payee }
                    : null;
            case "external-post":
                return ReadString(scope, "target") is { } postTarget &&
                    ReadString(scope, "visibility") is "public" or "restricted"
                    ? new GatewayApprovalScope(GatewayApprovalScopeKind.ExternalPost) { Target = postTarget }
                    : null;
            default:
                return null;
        }
    }

    private static IReadOnlyList<string> ReadDecisions(JsonElement request)
    {
        if (!request.TryGetProperty("allowedDecisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
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
