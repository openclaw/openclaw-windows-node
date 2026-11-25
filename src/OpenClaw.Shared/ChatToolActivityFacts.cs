// Gateway-owned display facts used by native tool activity summaries.
// Example: ChatToolActivityFacts.Read(item) preserves commandBearing without guessing from a tool name.
using System.Text.Json;
using System.Collections.Immutable;

namespace OpenClaw.Shared;

/// <summary>Optional presentation hints, never execution permission or tool completion authority.</summary>
public sealed record ChatToolActivityFacts(bool? CommandBearing = null, bool? HideFromChannelProgress = null,
    bool? SuppressChannelProgress = null, string? Title = null, string? Meta = null,
    string? Summary = null, string? Status = null, string? ItemId = null, bool AuthoritativeAbsence = false,
    bool? UnpairedCall = null)
{
    /// <summary>Sparse legacy hints do not assert a complete canonical visibility list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCanonical => ItemId is not null || AuthoritativeAbsence;

    /// <summary>Active headlines filter hidden records before invocation dedup, unlike aggregate counts.</summary>
    public ChatToolActivityFacts? ForHeadline() => Records.IsDefaultOrEmpty
        ? HideFromChannelProgress != true && SuppressChannelProgress != true ? this : null
        : Records.LastOrDefault(item => item.HideFromChannelProgress != true && item.SuppressChannelProgress != true);

    /// <summary>Retained complete item records. Selection filters suppression before call dedup.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Collections.Immutable.ImmutableArray<ChatToolActivityFacts> Records { get; init; }

    /// <summary>Canonical records replace the same item, while distinct items retain their own visibility.</summary>
    public static ChatToolActivityFacts? Merge(ChatToolActivityFacts? prior, ChatToolActivityFacts? next)
    {
        if (next is null) return prior;
        if (prior is null || next.AuthoritativeAbsence) return next;
        if (next.ItemId is not null)
        {
            var records = prior.Records.IsDefault ? prior.ItemId is null
                ? System.Collections.Immutable.ImmutableArray<ChatToolActivityFacts>.Empty
                : System.Collections.Immutable.ImmutableArray.Create(prior with { Records = default }) : prior.Records;
            records = records.Where(item => item.ItemId != next.ItemId).ToImmutableArray();
            var incoming = next.Records.IsDefault ? System.Collections.Immutable.ImmutableArray.Create(next) : next.Records;
            foreach (var item in incoming)
            {
                records = records.Where(existing => existing.ItemId != item.ItemId).ToImmutableArray();
                // Bound retained producer records per invocation without changing reducer correlation limits.
                if (records.Length >= 128) records = records.RemoveAt(0);
                records = records.Add(item with { Records = default });
            }
            var selected = records.LastOrDefault(item => item.SuppressChannelProgress != true) ?? records[^1];
            return selected with { Records = records };
        }
        if (prior.ItemId is not null || prior.AuthoritativeAbsence) return prior;
        // Older tool streams carry sparse hints, not complete canonical activity records.
        return new(next.CommandBearing ?? prior.CommandBearing,
            next.HideFromChannelProgress ?? prior.HideFromChannelProgress,
            next.SuppressChannelProgress ?? prior.SuppressChannelProgress,
            next.Title ?? prior.Title, next.Meta ?? prior.Meta, next.Summary ?? prior.Summary, next.Status ?? prior.Status,
            UnpairedCall: next.UnpairedCall ?? prior.UnpairedCall);
    }

    /// <summary>Reads only typed protocol hints. Older or malformed payloads retain the legacy presentation.</summary>
    public static ChatToolActivityFacts? Read(JsonElement item, bool requireCanonical = false)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        bool? Flag(string name) => item.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        var command = Flag("commandBearing");
        var hidden = Flag("hideFromChannelProgress");
        var suppressed = Flag("suppressChannelProgress");
        var unpaired = Flag("unpairedCall");
        string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            // Redact before collapsing layout so split secret names keep the sanitizer's protection.
            ? string.Join(' ', ExecApprovals.ExecApprovalCommandDisplaySanitizer.SanitizeDocumentText(value.GetString()!, 240)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;
        var title = Text("title");
        var meta = Text("meta");
        var summary = Text("summary");
        var itemId = item.TryGetProperty("itemId", out var identity) && identity.ValueKind == JsonValueKind.String &&
            identity.GetString() is { Length: > 0 and <= 256 } exactIdentity ? exactIdentity : null;
        var canonical = itemId is not null && Text("kind") is not null &&
            Text("phase") is "start" or "update" or "end" && title is not null;
        if (requireCanonical)
        {
            if (!canonical || string.IsNullOrEmpty(itemId)) return null;
            foreach (var name in new[] { "commandBearing", "hideFromChannelProgress", "suppressChannelProgress", "unpairedCall" })
                if (item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            foreach (var name in new[] { "name", "meta", "summary", "status", "toolCallId" })
                if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.String) return null;
        }
        var status = Text("status");
        if (status is not (null or "running" or "completed" or "failed" or "blocked" or "skipped"))
        {
            if (requireCanonical) return null;
            status = null;
        }
        return command.HasValue || hidden.HasValue || suppressed.HasValue || unpaired.HasValue || title is not null || meta is not null ||
            summary is not null || status is not null ? new(command, hidden, suppressed, title, meta, summary, status,
                canonical ? itemId : null, UnpairedCall: unpaired) : null;
    }
}
