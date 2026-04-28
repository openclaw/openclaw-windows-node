using System;
using System.IO;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public static class DiagnosticsJsonlService
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxArchives = 5;
    private static readonly object s_lock = new();
    private static string? s_filePath;

    public static string? FilePath => s_filePath;

    public static void Configure(string dataPath)
    {
        try
        {
            var logDirectory = Path.Combine(dataPath, "Logs");
            Directory.CreateDirectory(logDirectory);
            s_filePath = Path.Combine(logDirectory, "diagnostics.jsonl");
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

        var record = new
        {
            ts = DateTimeOffset.Now,
            @event = eventName,
            metadata
        };

        lock (s_lock)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(s_filePath!, TokenSanitizer.Sanitize(JsonSerializer.Serialize(record)) + Environment.NewLine);
            }
            catch (IOException ex)
            {
                Logger.Warn($"Diagnostics JSONL write failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warn($"Diagnostics JSONL write denied: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                Logger.Warn($"Diagnostics JSONL record was not serializable: {ex.Message}");
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(s_filePath))
            return;

        var current = new FileInfo(s_filePath);
        if (!current.Exists || current.Length <= MaxBytes)
            return;

        for (var i = MaxArchives; i >= 1; i--)
        {
            var source = i == 1 ? s_filePath : $"{s_filePath}.{i - 1}";
            var destination = $"{s_filePath}.{i}";
            if (!File.Exists(source))
                continue;

            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(source, destination);
        }
    }
}
