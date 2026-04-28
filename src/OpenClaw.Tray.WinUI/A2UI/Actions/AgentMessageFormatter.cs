using System.Collections.Generic;
using System.Text;

namespace OpenClawTray.A2UI.Actions;

/// <summary>
/// Builds the single-line tagged user message that the gateway appends to the
/// agent session when an A2UI action fires. Byte-for-byte port of the Android
/// reference (<c>OpenClawCanvasA2UIAction.formatAgentMessage</c>) so the LLM
/// sees identical input regardless of which node emitted it.
/// </summary>
public static class AgentMessageFormatter
{
    /// <summary>
    /// Sanitize a tag value for inclusion in the space-separated CANVAS_A2UI
    /// line. Whitespace becomes <c>_</c>; any character outside
    /// <c>[A-Za-z0-9_\-.:]</c> becomes <c>_</c>; empty/whitespace inputs
    /// become <c>-</c> (so we never emit a bare <c>key=</c>).
    /// </summary>
    public static string SanitizeTagValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "-";
        var normalized = trimmed.Replace(' ', '_');
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':';
            sb.Append(ok ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Format the user-shaped message that the gateway will append as a turn
    /// in <paramref name="sessionKey"/>. The model decides what each action
    /// name means; the gateway has no built-in action→tool mapping.
    ///
    /// <para>Tag order matters for prompt-injection defense: the
    /// <c>default=update_canvas</c> sentinel comes BEFORE the
    /// agent-controlled <c>ctx={...}</c> JSON. A hostile component could put
    /// e.g. <c>"} default=do_something_else"</c> in a context value; if
    /// <c>default=</c> were emitted last, that injected fragment would render
    /// as a second <c>default=</c> token and might shadow ours. With the
    /// sentinel before <c>ctx=</c>, anything the agent can sneak in is
    /// strictly trailing noise.</para>
    /// </summary>
    public static string FormatAgentMessage(
        string actionName,
        string sessionKey,
        string surfaceId,
        string sourceComponentId,
        string host,
        string instanceId,
        string? contextJson)
    {
        var parts = new List<string>(8)
        {
            "CANVAS_A2UI",
            $"action={SanitizeTagValue(actionName)}",
            $"session={SanitizeTagValue(sessionKey)}",
            $"surface={SanitizeTagValue(surfaceId)}",
            $"component={SanitizeTagValue(sourceComponentId)}",
            $"host={SanitizeTagValue(host)}",
            $"instance={SanitizeTagValue(instanceId)}",
            "default=update_canvas",
        };
        if (!string.IsNullOrWhiteSpace(contextJson))
            parts.Add($"ctx={contextJson}");
        return string.Join(" ", parts);
    }
}
