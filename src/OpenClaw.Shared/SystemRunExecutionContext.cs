using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Routing hints only. Never session, turn, or approval authority.</summary>
public sealed record SystemRunExecutionContext(string? SenderId = null, string? ChatId = null, bool Subagent = false)
{
    public const string Capability = "system.run.execution-context.v1";
    internal const string ChannelMarker = "OPENCLAW_CHANNEL_CONTEXT";
    internal const string SubagentMarker = "OPENCLAW_SUBAGENT_EXEC";

    internal static bool TryRead(JsonElement args, out SystemRunExecutionContext? context)
    {
        context = null;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("executionContext", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        string? sender = null, chat = null;
        var subagent = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            switch (property.Name)
            {
                case "senderId" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 0:
                    sender = property.Value.GetString();
                    break;
                case "chatId" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 0:
                    chat = property.Value.GetString();
                    break;
                case "subagent" when property.Value.ValueKind == JsonValueKind.True:
                    subagent = true;
                    break;
                default:
                    return false;
            }
        }
        context = new(sender, chat, subagent);
        return true;
    }

    internal void ApplyTo(IDictionary<string, string?> environment)
    {
        // Presence is a complete projection, including {}. Windows names are
        // case-insensitive even when the caller's dictionary is not.
        foreach (var name in environment.Keys.Where(IsRoutingMarker).ToArray())
            environment.Remove(name);
        if (SenderId is not null || ChatId is not null)
        {
            var channel = new Dictionary<string, object>();
            if (SenderId is not null) channel["sender"] = new { id = SenderId };
            if (ChatId is not null) channel["chat"] = new { id = ChatId };
            environment[ChannelMarker] = JsonSerializer.Serialize(channel);
        }
        if (Subagent) environment[SubagentMarker] = "1";
    }

    private static bool IsRoutingMarker(string name) =>
        name.Equals(ChannelMarker, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(SubagentMarker, StringComparison.OrdinalIgnoreCase);
}
