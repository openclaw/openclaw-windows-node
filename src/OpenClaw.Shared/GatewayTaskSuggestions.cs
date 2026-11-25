// Typed, bounded task-suggestion wire data. Example: GatewayTaskSuggestions.ParseSuggestion(payload).
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>A volatile Gateway-owned follow-up proposal, not a local work item.</summary>
public sealed record GatewayTaskSuggestion(string Id, string Title, string Prompt, string Tldr,
    string Cwd, string SessionKey, string? AgentId, long CreatedAt);
public sealed record GatewayTaskSuggestionList(bool IsSupported, IReadOnlyList<GatewayTaskSuggestion> Suggestions);
public sealed record GatewayTaskAcceptance(string TaskId, string Mode, string? CloudProfileId = null, string? Cwd = null);

/// <summary>A read-only preflight rejected the cloud profile before task acceptance was dispatched.</summary>
public sealed class GatewayTaskCloudProfileException() : InvalidOperationException("Choose a configured cloud profile before launching this task.");

/// <summary>Validates data before displaying Gateway-authored text as plain native content.</summary>
public static class GatewayTaskSuggestions
{
    public static GatewayTaskSuggestion ParseSuggestion(JsonElement value)
    {
        var created = value.GetProperty("createdAt");
        if (!created.TryGetInt64(out var timestamp) || timestamp < 0)
            throw new JsonException("Invalid task suggestion timestamp.");
        return new(Read(value, "id", 128), Read(value, "title", 60), Read(value, "prompt", 32768),
            Read(value, "tldr", 1024), Read(value, "cwd", 4096), Read(value, "sessionKey", 512),
            value.TryGetProperty("agentId", out _) ? Read(value, "agentId", 128) : null, timestamp);
    }

    public static GatewayTaskSuggestionList ParseList(JsonElement value)
    {
        var rows = value.GetProperty("suggestions");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100)
            throw new JsonException("Invalid task suggestion list.");
        var suggestions = rows.EnumerateArray().Select(ParseSuggestion).ToArray();
        if (suggestions.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != suggestions.Length)
            throw new JsonException("Duplicate task suggestion identity.");
        return new(true, suggestions);
    }

    public static string ParseAccepted(JsonElement value, string taskId)
    {
        if (Read(value, "taskId", 128) != taskId)
            throw new JsonException("Task acceptance identity changed.");
        return Read(value, "key", 512);
    }

    public static void Validate(GatewayTaskAcceptance request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        if (request.Mode is not ("local" or "worktree" or "session" or "cloud")
            || (request.Mode == "cloud" && string.IsNullOrWhiteSpace(request.CloudProfileId))
            || request.CloudProfileId?.Length > 128
            || (request.Cwd is not null && (request.Mode != "worktree" || string.IsNullOrWhiteSpace(request.Cwd) || request.Cwd.Length > 4096)))
            throw new ArgumentException("Invalid task acceptance options.");
    }

    /// <summary>Syntax only: a remote Gateway may run Windows or POSIX. No local filesystem lookup.</summary>
    public static bool IsAbsoluteGatewayPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && !path.Any(char.IsControl)
        && (path.StartsWith('/')
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
            || (path.StartsWith(@"\\", StringComparison.Ordinal)
                && path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2));

    private static string Read(JsonElement value, string key, int limit)
    {
        var field = value.GetProperty(key);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text) || text.Length > limit)
            throw new JsonException("Invalid task suggestion field.");
        return text;
    }
}
