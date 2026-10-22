using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Welcome copy supplied by the connected Gateway, not bundled by the client.</summary>
public sealed record ChatWelcomeCatalog(
    string Locale,
    string Hint,
    string RecentChatsLabel,
    IReadOnlyList<ChatWelcomeSuggestion> Suggestions)
{
    internal static ChatWelcomeCatalog Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("Unsupported welcome catalog version.");
        var locale = RequiredText(payload, "resolvedLocale");
        _ = RequiredText(payload, "requestedLocale");
        var hint = RequiredText(payload, "hint");
        var recentChatsLabel = RequiredText(payload, "recentChatsLabel");
        if (!payload.TryGetProperty("suggestions", out var suggestions) ||
            suggestions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Welcome catalog suggestions are missing.");

        var result = new List<ChatWelcomeSuggestion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suggestion in suggestions.EnumerateArray())
        {
            var id = RequiredText(suggestion, "id");
            if (!ids.Add(id))
                throw new InvalidDataException("Welcome catalog suggestion IDs must be unique.");
            result.Add(new(id, RequiredText(suggestion, "displayText"), RequiredText(suggestion, "sendText")));
        }
        return new(locale, hint, recentChatsLabel, result.AsReadOnly());
    }

    private static string RequiredText(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Welcome catalog field '{name}' is missing or invalid.");
        return value.GetString()!;
    }
}

public sealed record ChatWelcomeSuggestion(string Id, string Label, string Prompt);
