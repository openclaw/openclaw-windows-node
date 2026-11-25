// Pure current-run headline selection and dwell policy, following web qf/Cg.
// Example: state.Update(scope, tools, runId, nowMs) returns a title and a bounded refresh delay.
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

internal sealed record ChatToolActivityHeadline(ChatTimelineItem Tool, string Title, string? Status)
{
    public bool Running => Status == "running";
}

/// <summary>Retains a readable current-run step across fast calls without delaying failure or scope changes.</summary>
internal sealed class ChatToolActivityHeadlineState
{
    internal const int DwellMilliseconds = 3000;
    private string? _scope;
    private ChatToolActivityHeadline? _shown;
    private long _changedAt;

    internal (ChatToolActivityHeadline? Headline, int RefreshAfterMs) Update(
        string scope, IReadOnlyList<ChatTimelineItem> tools, string? runId, long nowMs,
        Func<ChatTimelineItem, string?>? rowText = null)
    {
        var current = CurrentTools(tools, runId);
        var last = current.LastOrDefault();
        var urgent = last is not null && Status(last) is "failed" or "blocked";
        var candidate = urgent ? last : current.LastOrDefault(tool => Status(tool) == "running") ?? last;
        if (candidate is not null)
        {
            // A visible wrapper owns its nested steps' headline, just as on the web.
            foreach (var root in ChatToolActivityPresentation.GroupNested(current))
                if (Contains(root.Children, candidate.Id))
                {
                    candidate = root.Tool;
                    break;
                }
        }
        var title = candidate is null ? null : Title(candidate, rowText);
        var next = candidate is null || title is null ? null
            : new ChatToolActivityHeadline(candidate, title, urgent ? Status(last!) : Status(candidate));
        var changedScope = _scope != scope;
        _scope = scope;
        if (next is null)
        {
            _shown = null;
            return (null, 0);
        }
        var remaining = Math.Max(0, DwellMilliseconds - (nowMs - _changedAt));
        if (changedScope || _shown is null || urgent || _shown.Tool.Id == next.Tool.Id || remaining == 0 ||
            !current.Any(tool => tool.Id == _shown.Tool.Id))
        {
            if (changedScope || _shown?.Tool.Id != next.Tool.Id || _shown.Title != next.Title)
                _changedAt = nowMs;
            _shown = next;
            return (next, 0);
        }
        var retained = current.LastOrDefault(tool => tool.Id == _shown.Tool.Id);
        // Refresh status immediately while keeping the previous title during its dwell.
        _shown = _shown with { Status = retained is null ? null : Status(retained) };
        return (_shown, (int)remaining);
    }

    /// <summary>Shared by headline selection and the latest headline-owning timeline row.</summary>
    internal static IReadOnlyList<ChatTimelineItem> CurrentTools(IEnumerable<ChatTimelineItem> tools, string? runId)
    {
        var inRun = tools.Where(tool => tool.Kind == ChatTimelineItemKind.ToolCall && (runId is not null
            ? tool.ToolRunId == runId : tool.ToolResult is null or ChatToolCallStatus.InProgress)).ToArray();
        var authoritative = inRun.Any(tool => tool.ToolActivityFacts?.IsCanonical == true);
        var visible = new List<ChatTimelineItem>();
        foreach (var tool in inRun)
        {
            var facts = tool.ToolActivityFacts;
            if (authoritative && facts?.IsCanonical != true) continue;
            if (facts is not null)
            {
                var headlineFacts = facts.ForHeadline();
                if (headlineFacts is null) continue;
                visible.Add(tool with { ToolActivityFacts = headlineFacts });
            }
            else visible.Add(tool);
        }
        // Canonical records share a call-key namespace for this display projection only.
        if (!authoritative) return visible;
        return visible.GroupBy(tool => tool.ToolCallId ?? tool.Id, StringComparer.Ordinal)
            .Select(group => group.Last()).ToArray();
    }

    private static bool Contains(IReadOnlyList<ChatToolCallNode> nodes, string id) =>
        nodes.Any(node => node.Tool.Id == id || Contains(node.Children, id));

    private static string? Title(ChatTimelineItem tool, Func<ChatTimelineItem, string?>? rowText)
    {
        var facts = tool.ToolActivityFacts;
        if (facts?.IsCanonical == true && string.IsNullOrWhiteSpace(facts.Title)) return null;
        if (facts?.IsCanonical == true && facts.Status is null && facts.Summary is { Length: > 0 } summary)
            return summary;
        var row = ChatToolRowPresentation.Resolve(tool);
        if (facts?.IsCanonical == true)
        {
            // Canonical activity controls title eligibility and precedence. The card projection
            // contributes only an explicitly labeled subagent, not a synthesized file/command row.
            if (string.IsNullOrWhiteSpace(facts.Title)) return null;
            if (row.Subagent is not null) return row.Subagent.Label;
            return facts.Meta ?? (facts.Status is not null && (facts.Title == tool.ToolName ||
                facts.Title == ChatToolRowPresentation.DisplayName(tool.ToolName)) ? "" : facts.Title);
        }
        return rowText?.Invoke(tool) ?? row.Title ?? tool.ToolInputDetails?.Preview;
    }

    private static string? Status(ChatTimelineItem tool) => ChatToolActivityPresentation.Outcome(tool);
}
