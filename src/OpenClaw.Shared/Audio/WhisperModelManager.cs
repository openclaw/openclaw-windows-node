using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Manages Whisper GGML model downloads, storage, and lifecycle.
/// Models are stored in <c>%APPDATA%\OpenClawTray\models\</c> (or the
/// configured data directory).
/// </summary>
public sealed class WhisperModelManager
{
    private readonly string _modelsDirectory;
    private readonly IOpenClawLogger _logger;
    // Per-model single-flight gate: a manual auto-download (VoiceService
    // EnsureInitializedAsync) and a UI-triggered download for the same
    // model would otherwise both write the same .tmp file. Static so an
    // additional manager instance constructed elsewhere (e.g. the Settings
    // page's status-only check) doesn't bypass the lock.
    private static readonly ConcurrentDictionary<string, Task> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Known Whisper model definitions.</summary>
    public static readonly WhisperModelInfo[] AvailableModels =
    [
        new("ggml-tiny.bin",    "tiny",    75_000_000,  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin"),
        new("ggml-base.bin",    "base",    142_000_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"),
        new("ggml-small.bin",   "small",   466_000_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"),
    ];

    public WhisperModelManager(string dataDirectory, IOpenClawLogger logger)
    {
        _modelsDirectory = Path.Combine(dataDirectory, "models");
        _logger = logger;
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>Full file path for a given model name.</summary>
    public string GetModelPath(string modelName)
    {
        var info = FindModel(modelName);
        return Path.Combine(_modelsDirectory, info.FileName);
    }

    /// <summary>Check whether a model file already exists on disk.</summary>
    public bool IsModelDownloaded(string modelName)
    {
        var path = GetModelPath(modelName);
        return File.Exists(path);
    }

    /// <summary>Get the size of a downloaded model, or 0 if not downloaded.</summary>
    public long GetModelSize(string modelName)
    {
        var path = GetModelPath(modelName);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>
    /// Download a model from HuggingFace if not already present.
    /// Reports progress as bytes downloaded / total bytes.
    /// Per-model single-flight: concurrent calls for the same model await
    /// the in-flight download instead of racing on the same .tmp file.
    /// </summary>
    public Task DownloadModelAsync(
        string modelName,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = FindModel(modelName);
        var destPath = Path.Combine(_modelsDirectory, info.FileName);

        if (File.Exists(destPath))
        {
            _logger.Info($"Model '{modelName}' already exists at {destPath}");
            return Task.CompletedTask;
        }

        // Use the canonical key (FileName) so two callers that pass "base"
        // and "ggml-base.bin" still coalesce.
        var key = info.FileName;
        var task = InFlightDownloads.GetOrAdd(key, _ => DownloadModelCoreAsync(info, destPath, progress, cancellationToken));
        // Whichever caller created the task is also responsible for clearing
        // the slot. Subsequent callers just await the same Task; if it faults
        // they all see the same exception. Cancellation linkage is honored
        // by the Core method via the token captured in the GetOrAdd factory.
        return AwaitAndCleanup(key, task);
    }

    private async Task AwaitAndCleanup(string key, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            // Only remove if still the same task (so a fresh start after
            // failure isn't blocked by the old completed entry).
            InFlightDownloads.TryRemove(new KeyValuePair<string, Task>(key, task));
        }
    }

    private async Task DownloadModelCoreAsync(
        WhisperModelInfo info,
        string destPath,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        _logger.Info($"Downloading model '{info.Name}' from {info.DownloadUrl}");
        var tempPath = destPath + ".tmp";

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            using var response = await httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? info.ApproximateSizeBytes;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long downloadedBytes = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;
                progress?.Report((downloadedBytes, totalBytes));
            }

            await fileStream.FlushAsync(cancellationToken);
            fileStream.Close();

            File.Move(tempPath, destPath, overwrite: true);
            _logger.Info($"Model '{info.Name}' downloaded successfully ({downloadedBytes:N0} bytes)");
        }
        catch
        {
            // Clean up partial download
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Delete a downloaded model file.</summary>
    public bool DeleteModel(string modelName)
    {
        var path = GetModelPath(modelName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.Info($"Deleted model '{modelName}'");
        return true;
    }

    private static WhisperModelInfo FindModel(string modelName)
    {
        foreach (var m in AvailableModels)
        {
            if (string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        throw new ArgumentException($"Unknown model: '{modelName}'. Available: tiny, base, small");
    }
}

/// <summary>Metadata about a Whisper model variant.</summary>
public sealed record WhisperModelInfo(
    string FileName,
    string Name,
    long ApproximateSizeBytes,
    string DownloadUrl);
