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
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Known Whisper model definitions.
    ///
    /// SECURITY — pinned SHA-256 hashes (lowercase hex) verified against
    /// HuggingFace on 2026-05-05. Downloads with a different hash are
    /// rejected and the partial file is deleted. Before any public release:
    /// re-verify each hash from an independent source and document the
    /// provenance in Audio_FollowUps.md §2 (also consider replacing this
    /// inline table with a signed manifest).
    /// </summary>
    public static readonly WhisperModelInfo[] AvailableModels =
    [
        new("ggml-tiny.bin",    "tiny",    77_691_713,  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new("ggml-base.bin",    "base",    147_951_465, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("ggml-small.bin",   "small",   487_601_967, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
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
        return SingleFlightDownload.RunAsync(
            InFlightDownloads,
            key,
            token => DownloadModelCoreAsync(info, destPath, progress, token),
            cancellationToken);
    }

    private async Task DownloadModelCoreAsync(
        WhisperModelInfo info,
        string destPath,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        // SECURITY: a missing pinned hash is treated as a hard failure so we
        // never install an unverified asset. The catalog above pins all
        // shipped models; if you add a new one without a hash, this is the
        // place that refuses to download it. See Audio_FollowUps.md §2.
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            throw new InvalidOperationException(
                $"Whisper model '{info.Name}' has no pinned SHA-256; refusing to download. " +
                "Add a verified hash to AvailableModels before enabling this model.");
        }

        _logger.Info($"Downloading model '{info.Name}' from {info.DownloadUrl}");
        var tempPath = destPath + ".tmp";

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            using var response = await httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? info.ApproximateSizeBytes;
            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
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
            }

            // SECURITY: verify SHA-256 BEFORE the atomic rename, so a
            // tampered file never lands at the canonical path. On mismatch
            // we delete the temp file (no partial install) and surface a
            // sanitized error — we deliberately do NOT echo the actual
            // hash because that gives an attacker a confirmation oracle.
            await VerifyHashAsync(tempPath, info.Sha256, info.Name, cancellationToken);

            File.Move(tempPath, destPath, overwrite: true);
            _logger.Info($"Model '{info.Name}' downloaded and verified");
        }
        catch
        {
            // Clean up partial download
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Compute SHA-256 of <paramref name="filePath"/> and compare to
    /// <paramref name="expectedHex"/>. Throws on mismatch (and the caller
    /// is expected to delete the file). Does not echo the actual hash to
    /// avoid handing attackers a confirmation oracle.
    /// </summary>
    private static async Task VerifyHashAsync(string filePath, string expectedHex, string assetName, CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = await sha.ComputeHashAsync(stream, cancellationToken);
        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException(
                $"Whisper model '{assetName}' failed integrity check. The downloaded file does not match the pinned SHA-256.");
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
/// <param name="FileName">On-disk filename (e.g. "ggml-base.bin").</param>
/// <param name="Name">Short identifier used by callers ("tiny" / "base" / "small").</param>
/// <param name="ApproximateSizeBytes">Approximate size hint for UI; the
/// actual size is asserted against <paramref name="Sha256"/> after download.</param>
/// <param name="DownloadUrl">HTTPS URL of the model file.</param>
/// <param name="Sha256">Pinned lowercase hex SHA-256 of the downloaded file.
/// MUST be set; downloads are refused when null. See the catalog for the
/// "verified on" date — these need re-verification before any public
/// release (see Audio_FollowUps.md §2).</param>
public sealed record WhisperModelInfo(
    string FileName,
    string Name,
    long ApproximateSizeBytes,
    string DownloadUrl,
    string? Sha256);
