using System;
using System.IO;
using System.Text.Json;
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
            var record = new
            {
                ts = DateTimeOffset.Now,
                @event = eventName,
                metadata
            };
            var line = TokenSanitizer.Sanitize(JsonSerializer.Serialize(record));
            channel.Writer.TryWrite(line);
        }
        catch (NotSupportedException ex)
        {
            // Serialization failure is the only thing we want to surface from
            // the calling thread — record IS untrusted (caller-supplied).
            Logger.Warn($"Diagnostics JSONL record was not serializable: {ex.Message}");
        }
    }

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
