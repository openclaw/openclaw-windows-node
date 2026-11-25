// Classifies typed Gateway transcript notices into native timeline presentation, matching the web
// Control UI's projectChatSystemNotice: run failures, learned-skill changes, model fallbacks and
// internal system context. Only typed Gateway facts select a notice; display text never does.
//
// Usage:
//   var kind = ChatNoticePresentation.Classify(message.Presentation, text);
//   if (kind == ChatNoticeKind.Hidden) return;                       // drop the row
//   if (kind != ChatNoticeKind.None)
//       apply(new ChatStatusEvent(ChatNoticePresentation.EntryText(kind, message.Presentation, text),
//           ChatTone.Dim, StartsTurn: ChatNoticePresentation.StartsTurn(message.Presentation)));
//   // Rendering a status entry whose metadata carries the presentation:
//   var view = ChatNoticePresentation.ResolveSystem(presentation, entry.Text, LocalizedOrDefault);

using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public enum ChatNoticeKind
{
    /// <summary>An ordinary transcript row.</summary>
    None,
    /// <summary>A typed notice with nothing to show, such as source tool "restart".</summary>
    Hidden,
    RunFailure,
    SkillLearned,
    LegacySkill,
    ModelFallback,
    System,
}

public enum ChatSystemNoticeIcon
{
    Cpu,
    Compass,
}

/// <summary>Resolved system notice copy. A collapsed notice with text renders as an inline disclosure.</summary>
public sealed record ChatSystemNoticeView(
    string Label,
    string Text,
    ChatSystemNoticeIcon Icon,
    bool Collapsed,
    bool ShowTimestamp);

/// <summary>Outcome of one Skill Workshop Undo attempt. Unknown outcomes report <see cref="Failed"/>.</summary>
public enum ChatSkillUndoOutcome
{
    Undone,
    Failed,
    Unavailable,
}

/// <summary>Latest-row repeat counts for consecutive identical run failures, and the rows they absorb.</summary>
public sealed record ChatRunFailureStreaks(
    IReadOnlySet<string> HiddenEntryIds,
    IReadOnlyDictionary<string, int> RepeatCounts)
{
    public static ChatRunFailureStreaks Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal));

    public int RepeatCount(string entryId) => RepeatCounts.GetValueOrDefault(entryId, 1);
}

public static class ChatNoticePresentation
{
    /// <summary>Upper bound for a status-only system notice summary, matching the web.</summary>
    public const int StatusSummaryMaxLength = 64;
    /// <summary>A run-failure first line longer than this is not repeated above the detail log.</summary>
    public const int RunFailureLeadMaxLength = 240;

    private const string InternalSystemProvenance = "internal_system";
    private const string InterSessionProvenance = "inter_session";
    private const string PluginSourcePrefix = "plugin:";
    private const string SystemTextPrefix = "[System] ";

    private sealed record SystemKind(
        string? LabelKey = null,
        string? LabelFallback = null,
        string? SummaryKey = null,
        string? SummaryFallback = null,
        bool CollapsedBody = false,
        bool StartsTurn = true,
        ChatSystemNoticeIcon Icon = ChatSystemNoticeIcon.Cpu,
        bool ShowTimestamp = false);

    private static readonly IReadOnlyDictionary<string, SystemKind> s_systemKinds =
        new Dictionary<string, SystemKind>(StringComparer.Ordinal)
        {
            // Older scheduled conversation turns lack the run metadata for an automation row.
            ["cron"] = new("Chat_Notice_Automation", "Automation", CollapsedBody: true),
            ["cron_failure_repair"] = new(
                "Chat_Notice_AutomationRecovery", "System · automation recovery",
                "Chat_Notice_AutomationFailed", "Automation failed."),
            ["restart-sentinel"] = new("Chat_Notice_GatewayRestarted", "System · Gateway restarted"),
            ["cli_harness_context"] = new(
                "Chat_Notice_InjectedContext", "System · injected context", CollapsedBody: true, StartsTurn: false),
            ["claude_cli_task_notification"] = new(
                "Chat_Notice_BackgroundTask", "System · background task", CollapsedBody: true, StartsTurn: false),
        };

    // Plugin steering omits a label key: the plugin's own name is the label.
    private static readonly SystemKind s_pluginSteering = new(
        Icon: ChatSystemNoticeIcon.Compass, CollapsedBody: true, ShowTimestamp: true, StartsTurn: false);

    public static ChatNoticeKind Classify(ChatMessagePresentation? presentation, string? text)
    {
        if (presentation is null)
            return ChatNoticeKind.None;
        if (presentation.RunFailure is not null)
            return ChatNoticeKind.RunFailure;
        if (presentation.SkillChanges is not null)
            return ChatNoticeKind.SkillLearned;
        if (presentation.LegacySkillNotice)
            return string.IsNullOrWhiteSpace(text) ? ChatNoticeKind.Hidden : ChatNoticeKind.LegacySkill;
        if (presentation.ModelFallback is not null)
            return ChatNoticeKind.ModelFallback;
        if (!IsInternalSystemUser(presentation))
            return ChatNoticeKind.None;
        if (presentation.SourceTool == "restart")
            return ChatNoticeKind.Hidden;
        var kind = ResolveKind(presentation.SourceTool);
        return kind?.SummaryKey is null && string.IsNullOrWhiteSpace(StripSystemPrefix(text))
            ? ChatNoticeKind.Hidden
            : ChatNoticeKind.System;
    }

    /// <summary>Plain status-entry text: what copy, search and accessibility read for the notice.</summary>
    public static string EntryText(ChatNoticeKind kind, ChatMessagePresentation? presentation, string? text) =>
        kind switch
        {
            ChatNoticeKind.RunFailure => presentation?.RunFailure?.Summary ?? string.Empty,
            ChatNoticeKind.SkillLearned => string.Join(
                ", ", presentation?.SkillChanges?.Skills.Select(skill => skill.Name) ?? []),
            ChatNoticeKind.ModelFallback when presentation?.ModelFallback is { } fallback =>
                $"{fallback.FromModel} → {fallback.ToModel}",
            ChatNoticeKind.System => StripSystemPrefix(text),
            _ => text ?? string.Empty,
        };

    /// <summary>True when a notice opens a new operator turn, like a user message.</summary>
    public static bool StartsTurn(ChatMessagePresentation? presentation) =>
        presentation is not null && !presentation.HasStructuredNotice && IsInternalSystemUser(presentation) &&
        presentation.SourceTool != "restart" && ResolveKind(presentation.SourceTool)?.StartsTurn != false;

    /// <summary>Kind of an already-routed status entry, or <see cref="ChatNoticeKind.None"/>.</summary>
    public static ChatNoticeKind KindOf(ChatTimelineItem entry, IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        if (entry.Kind != ChatTimelineItemKind.Status)
            return ChatNoticeKind.None;
        var presentation = entry.Presentation ?? metadata?.GetValueOrDefault(entry.Id)?.Presentation;
        var kind = Classify(presentation, entry.Text);
        return kind == ChatNoticeKind.Hidden ? ChatNoticeKind.None : kind;
    }

    public static ChatSystemNoticeView? ResolveSystem(
        ChatMessagePresentation? presentation,
        string? text,
        Func<string, string, string> localize)
    {
        if (presentation is null || Classify(presentation, text) != ChatNoticeKind.System)
            return null;
        var kind = ResolveKind(presentation.SourceTool);
        var body = kind?.SummaryKey is { } summaryKey
            ? localize(summaryKey, kind.SummaryFallback!)
            : StripSystemPrefix(text);
        var label = kind is null
            ? localize("Chat_Notice_System", "System")
            : kind.LabelKey is { } labelKey
                ? localize(labelKey, kind.LabelFallback!)
                : PluginName(presentation);
        var collapsed = kind?.CollapsedBody ?? false;
        if (kind?.SummaryKey is not null)
        {
            var summary = label + ": " + body;
            var isShort = summary.Length <= StatusSummaryMaxLength &&
                summary.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) < 0;
            label = isShort ? summary : localize("Chat_Notice_System", "System");
            body = isShort ? string.Empty : body;
            collapsed = !isShort;
        }
        return new(label, body, kind?.Icon ?? ChatSystemNoticeIcon.Cpu, collapsed, kind?.ShowTimestamp ?? false);
    }

    public static string FallbackLabel(ChatModelFallbackNotice fallback, Func<string, string, string> localize) =>
        Format(
            fallback.Restored
                ? localize("Chat_ModelFallback_Restored", "Returned to selected model: {0} → {1}")
                : localize("Chat_ModelFallback_Event", "Model fallback: {0} → {1}"),
            fallback.FromModel,
            fallback.ToModel);

    public static string? FallbackReason(ChatModelFallbackNotice fallback, Func<string, string, string> localize) =>
        string.IsNullOrWhiteSpace(fallback.Reason)
            ? null
            : Format(localize("Chat_ModelFallback_Reason", "Reason: {0}"), fallback.Reason.Trim().Replace('_', ' '));

    /// <summary>Fix-specific title for rejected credentials or billing, otherwise the generic failure title.</summary>
    public static string RunFailureTitle(ChatRunFailureNotice failure, Func<string, string, string> localize)
    {
        if (!string.IsNullOrWhiteSpace(failure.CauseProvider))
        {
            if (failure.FailoverReason is "auth" or "auth_permanent")
                return Format(
                    localize("Chat_RunFailure_CredentialsRejected", "{0} didn’t accept the saved credentials"),
                    failure.CauseProvider);
            if (failure.FailoverReason == "billing")
                return Format(
                    localize("Chat_RunFailure_BillingProblem", "{0} reports a billing problem"),
                    failure.CauseProvider);
        }
        return localize("Chat_RunFailure_Title", "Couldn’t finish this reply");
    }

    /// <summary>The first line, when it is concise and differs from the full detail shown below it.</summary>
    public static string? RunFailureLead(string summary)
    {
        var detail = summary.Trim();
        var firstLine = detail.Split('\n')[0].TrimEnd('\r');
        return firstLine.Length <= RunFailureLeadMaxLength && firstLine != detail ? firstLine : null;
    }

    public static string SkillVerb(ChatSkillChangeAction action, Func<string, string, string> localize) => action switch
    {
        ChatSkillChangeAction.Created => localize("Chat_SkillLearned_Created", "Created"),
        ChatSkillChangeAction.Updated => localize("Chat_SkillLearned_Updated", "Updated"),
        ChatSkillChangeAction.Archived => localize("Chat_SkillLearned_Archived", "Archived"),
        _ => localize("Chat_SkillLearned_Restored", "Restored"),
    };

    public static string? SkillToolTip(ChatSkillChange skill) =>
        string.IsNullOrWhiteSpace(skill.Summary) ? null : skill.Summary.Trim();

    public static string SkillAccessibleName(ChatSkillChange skill, Func<string, string, string> localize) =>
        $"{SkillVerb(skill.Action, localize)} {skill.Name}" +
        (string.IsNullOrWhiteSpace(skill.Summary) ? string.Empty : $": {skill.Summary.Trim()}");

    /// <summary>
    /// Keeps the latest of consecutive identical run failures and counts the earlier ones into it.
    /// Real operator input ends a streak; injected or forwarded input does not.
    /// </summary>
    public static ChatRunFailureStreaks ProjectRunFailureStreaks(
        IReadOnlyList<ChatTimelineItem> entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        HashSet<string>? hidden = null;
        Dictionary<string, int>? counts = null;
        string? latestId = null;
        string? latestSummary = null;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            var presentation = entry.Presentation ?? metadata?.GetValueOrDefault(entry.Id)?.Presentation;
            if (entry.Kind == ChatTimelineItemKind.User &&
                presentation?.ProvenanceKind is not (InterSessionProvenance or InternalSystemProvenance))
            {
                latestId = null;
                continue;
            }
            if (entry.Kind != ChatTimelineItemKind.Status || presentation?.RunFailure is not { } failure)
                continue;
            if (latestId is not null && latestSummary == failure.Summary)
            {
                counts ??= new(StringComparer.Ordinal);
                counts[latestId] = counts.GetValueOrDefault(latestId, 1) + 1;
                (hidden ??= new(StringComparer.Ordinal)).Add(entry.Id);
                continue;
            }
            latestId = entry.Id;
            latestSummary = failure.Summary;
        }
        return hidden is null
            ? ChatRunFailureStreaks.Empty
            : new(hidden, counts!);
    }

    private static bool IsInternalSystemUser(ChatMessagePresentation presentation) =>
        presentation.ProvenanceKind == InternalSystemProvenance &&
        presentation.SourceRole == "user" &&
        !presentation.IsIndependent;

    private static SystemKind? ResolveKind(string? sourceTool)
    {
        if (sourceTool?.StartsWith(PluginSourcePrefix, StringComparison.Ordinal) == true &&
            sourceTool.Length > PluginSourcePrefix.Length)
            return s_pluginSteering;
        return sourceTool is null ? null : s_systemKinds.GetValueOrDefault(sourceTool);
    }

    private static string PluginName(ChatMessagePresentation presentation) =>
        presentation.PluginSteeringName ??
        presentation.SourceTool?[PluginSourcePrefix.Length..] ??
        string.Empty;

    private static string StripSystemPrefix(string? text) =>
        text is null ? string.Empty :
        text.StartsWith(SystemTextPrefix, StringComparison.Ordinal) ? text[SystemTextPrefix.Length..] : text;

    private static string Format(string format, params object?[] values)
    {
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, values);
        }
        catch (FormatException)
        {
            return string.Join(" → ", values);
        }
    }
}
