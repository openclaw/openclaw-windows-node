using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Identity for an agent-event row. A normal event is run id plus sequence.
/// Translated approvals use an empty run id and sequence 0, so they are
/// distinguished by approval id and phase.
/// </summary>
public static class AgentEventIdentity
{
    public static bool IsSame(AgentEventInfo existing, AgentEventInfo incoming)
    {
        if (!string.IsNullOrEmpty(existing.RunId) || !string.IsNullOrEmpty(incoming.RunId))
            return existing.RunId == incoming.RunId && existing.Seq == incoming.Seq;

        var existingKey = ApprovalKey(existing);
        var incomingKey = ApprovalKey(incoming);
        if (existingKey == null || incomingKey == null)
            return false;

        return string.Equals(existingKey, incomingKey, StringComparison.Ordinal);
    }

    private static string? ApprovalKey(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;

        if (!evt.Data.TryGetProperty("approvalId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrEmpty(id))
            return null;

        var phase = "";
        if (evt.Data.TryGetProperty("phase", out var phaseElement) &&
            phaseElement.ValueKind == JsonValueKind.String)
        {
            phase = phaseElement.GetString() ?? "";
        }

        return id + "\n" + phase;
    }
}
