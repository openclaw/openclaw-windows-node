using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Append-only structured event log. Same async pattern as <see cref="Logger"/>:
/// callers enqueue, a single background task drains; under load this used to
/// stall whichever thread was hottest because writes happened under a lock on
/// the calling thread.
/// </summary>
public static class DiagnosticsJsonlService
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxArchives = 5;
    private const int ChannelCapacity = 4096;

    private static string? s_filePath;
    private static Channel<string>? s_channel;
    private static Task? s_writerTask;
    private static readonly object s_initLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string? FilePath => s_filePath;

    public static void Configure(string dataPath)
    {
        try
        {
            var logDirectory = Path.Combine(dataPath, "Logs");
            Directory.CreateDirectory(logDirectory);
            lock (s_initLock)
            {
                s_filePath = Path.Combine(logDirectory, "diagnostics.jsonl");
                if (s_channel == null)
                {
                    s_channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                    });
                    s_writerTask = Task.Run(WriterLoopAsync);
                }
            }
        }
        catch (IOException ex)
        {
            Logger.Warn($"Diagnostics JSONL setup failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn($"Diagnostics JSONL setup denied: {ex.Message}");
        }
    }

    public static void Write(string eventName, object metadata)
    {
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(s_filePath))
            return;
        var channel = s_channel;
        if (channel == null) return;

        try
        {
            channel.Writer.TryWrite(FormatRecordLine(eventName, metadata));
        }
        catch (NotSupportedException ex)
        {
            // Serialization failure is the only thing we want to surface from
            // the calling thread — record IS untrusted (caller-supplied).
            Logger.Warn($"Diagnostics JSONL record was not serializable: {ex.Message}");
        }
    }

    private static object? SanitizeMetadata(object? metadata)
    {
        if (metadata is null)
            return null;

        if (metadata is string text)
            return TokenSanitizer.SanitizeLogMessage(text);

        var node = JsonSerializer.SerializeToNode(metadata, JsonOptions);
        return SanitizeJsonNode(node);
    }

    private static string FormatRecordLine(string eventName, object metadata)
    {
        var record = new
        {
            ts = DateTimeOffset.Now,
            @event = TokenSanitizer.SanitizeLogMessage(eventName),
            metadata = SanitizeMetadata(metadata)
        };
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private static JsonNode? SanitizeJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var sanitizedObject = new JsonObject();
                foreach (var property in obj)
                {
                    var propertyName = property.Key;
                    var sanitizedPropertyName = MakeUniquePropertyName(
                        sanitizedObject,
                        SanitizeJsonPropertyName(propertyName));
                    sanitizedObject[sanitizedPropertyName] = IsSensitiveMetadataKey(propertyName)
                        ? JsonValue.Create("[REDACTED]")
                        : SanitizeJsonNode(property.Value);
                }
                return sanitizedObject;
            case JsonArray array:
                var sanitizedArray = new JsonArray();
                foreach (var item in array)
                    sanitizedArray.Add(SanitizeJsonNode(item));
                return sanitizedArray;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return SanitizeStringValue(text);
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? SanitizeStringValue(string text)
    {
        var trimmed = text.TrimStart();
        if ((trimmed.StartsWith('{') || trimmed.StartsWith('[')) &&
            TryParseJsonString(text, out var parsed))
        {
            return SanitizeJsonNode(parsed);
        }

        return JsonValue.Create(NormalizeSingleLine(TokenSanitizer.SanitizeLogMessage(text)));
    }

    private static bool TryParseJsonString(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);
            return node is not null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static string NormalizeSingleLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static bool IsSensitiveMetadataKey(string key) =>
        TokenSanitizer.IsSensitiveMetadataKeyName(key);

    private static string SanitizeJsonPropertyName(string propertyName)
    {
        var sanitized = NormalizeSingleLine(TokenSanitizer.SanitizeLogMessage(propertyName));
        return string.IsNullOrWhiteSpace(sanitized) ? "[redacted-key]" : sanitized;
    }

    private static string MakeUniquePropertyName(JsonObject obj, string propertyName)
    {
        if (!obj.ContainsKey(propertyName))
            return propertyName;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{propertyName}#{index++}";
        } while (obj.ContainsKey(candidate));

        return candidate;
    }

#if OPENCLAW_TRAY_TESTS
    internal static object? SanitizeMetadataForTest(object? metadata) => SanitizeMetadata(metadata);
    internal static string FormatRecordLineForTest(string eventName, object metadata) => FormatRecordLine(eventName, metadata);
#endif

    private static async Task WriterLoopAsync()
    {
        var channel = s_channel;
        if (channel == null) return;
        var reader = channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var path = s_filePath;
            if (path == null)
            {
                // Drain anyway so the channel doesn't fill up.
                while (reader.TryRead(out _)) { }
                continue;
            }

            try
            {
                RotateIfNeeded(path);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs);
                int wrote = 0;
                while (reader.TryRead(out var pending))
                {
                    sw.WriteLine(pending);
                    if (++wrote >= 256) break;
                }
                sw.Flush();
            }
            catch (IOException ex) { Logger.Warn($"Diagnostics JSONL write failed: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Logger.Warn($"Diagnostics JSONL write denied: {ex.Message}"); }
            catch (Exception ex) { Logger.Warn($"Diagnostics JSONL writer error: {ex.Message}"); }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length <= MaxBytes)
            return;

        for (var i = MaxArchives; i >= 1; i--)
        {
            var source = i == 1 ? path : $"{path}.{i - 1}";
            var destination = $"{path}.{i}";
            if (!File.Exists(source))
                continue;

            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(source, destination);
        }
    }
}
