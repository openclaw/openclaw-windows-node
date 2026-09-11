using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Recognizes only known unset-path CLI errors, never unknown paths or operational failures.
/// Callers retain transport timeout checks and snapshot-specific decoding and size limits.
/// </summary>
public static class GatewayConfigCliCompatibility
{
    public static bool IsUnsetError(int exitCode, string stdout, string stderr, string path)
    {
        if (exitCode != 1 || stdout.Length > 64 * 1024)
            return false;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            string legacy = $"Config path not found: {path}";
            string error = stderr.Trim();
            return error == legacy ||
                error == legacy + ". Run openclaw config validate to inspect config shape.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout, new JsonDocumentOptions { MaxDepth = 16 });
            return IsUnsetError(document.RootElement, path);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Gateway 2026.9 reports a valid, unset path as a structured stdout error.
    // Snapshot callers already check the command exit code and parse bounded JSON.
    public static bool IsUnsetError(JsonElement root, string path) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False &&
        root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object &&
        error.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
        type.GetString() == "cli_error" &&
        error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String &&
        message.GetString() == $"Config path is valid but unset: {path}. The runtime default applies until you set an authored value with openclaw config set {path} <value>.";
}
