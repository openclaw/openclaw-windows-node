using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Manages downloads and on-disk lifecycle for Piper TTS voices.
///
/// Each "voice" is a sherpa-onnx pre-packaged tarball that contains
/// everything needed for offline synthesis — the .onnx model, the
/// tokens.txt phoneme map, and the language-specific espeak-ng-data.
/// We use the sherpa-onnx repackaged distribution rather than the raw
/// HuggingFace Piper voices because the latter requires the user (or
/// us) to ship espeak-ng-data separately (~80 MB shared across voices).
///
/// Storage layout under the tray's data directory:
///   models/piper/&lt;voice-id&gt;/
///       &lt;voice-id&gt;.onnx
///       tokens.txt
///       espeak-ng-data/...
///
/// Each voice is ~50 MB compressed, ~80 MB extracted (with espeak data).
///
/// **TODO (pre-GA):** SHA-256 verification of downloaded tarballs before
/// extraction (Audio_FollowUps.md §2). The current implementation trusts
/// HTTPS + the system trust chain only.
/// </summary>
public sealed class PiperVoiceManager
{
    private readonly string _voicesDirectory;
    private readonly IOpenClawLogger _logger;
    // Per-voice single-flight gate: prevents racing the same voice download
    // from two callers (e.g. UI and a programmatic caller). Static so two
    // PiperVoiceManager instances over the same data directory still
    // coalesce against the same in-flight task.
    private static readonly ConcurrentDictionary<string, Task> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Curated catalog of Piper voices we offer in the UI. Each entry is
    /// a sherpa-onnx pre-packaged tarball from the project's GitHub
    /// releases. To add a voice: pick its key from
    /// https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models and
    /// drop it in here. Sizes are post-extraction (compressed is roughly
    /// half).
    /// </summary>
    public static readonly PiperVoiceInfo[] AvailableVoices =
    [
        new("en_US-amy-low",     "English (US) — Amy (low quality, fast)",   "en-US",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-amy-low.tar.bz2"),
        new("en_US-libritts-high","English (US) — LibriTTS (high quality)",  "en-US",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-libritts-high.tar.bz2"),
        new("en_GB-alan-low",    "English (GB) — Alan (low quality, fast)",  "en-GB",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_GB-alan-low.tar.bz2"),
        new("fr_FR-siwis-low",   "Français (FR) — Siwis (low quality, fast)","fr-FR",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-fr_FR-siwis-low.tar.bz2"),
        new("de_DE-thorsten-low","Deutsch (DE) — Thorsten (low quality)",    "de-DE",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-thorsten-low.tar.bz2"),
        new("zh_CN-huayan-medium","中文 (CN) — Huayan (medium quality)",      "zh-CN",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-zh_CN-huayan-medium.tar.bz2"),
    ];

    public PiperVoiceManager(string dataDirectory, IOpenClawLogger logger)
    {
        _voicesDirectory = Path.Combine(dataDirectory, "models", "piper");
        _logger = logger;
        Directory.CreateDirectory(_voicesDirectory);
    }

    /// <summary>Root directory where this voice's files live (created lazily).</summary>
    public string GetVoiceDirectory(string voiceId)
    {
        var info = FindVoice(voiceId);
        return Path.Combine(_voicesDirectory, info.VoiceId);
    }

    /// <summary>Path to the .onnx model file for a downloaded voice.</summary>
    public string GetModelPath(string voiceId)
    {
        var dir = GetVoiceDirectory(voiceId);
        // sherpa-onnx tarballs put files at the root of the voice dir; the
        // model file is named after the voice id.
        return Path.Combine(dir, $"{voiceId}.onnx");
    }

    /// <summary>Path to tokens.txt (phoneme map).</summary>
    public string GetTokensPath(string voiceId) => Path.Combine(GetVoiceDirectory(voiceId), "tokens.txt");

    /// <summary>Path to the espeak-ng-data directory bundled with this voice.</summary>
    public string GetEspeakDataDir(string voiceId) => Path.Combine(GetVoiceDirectory(voiceId), "espeak-ng-data");

    /// <summary>True when all three files are present on disk.</summary>
    public bool IsVoiceDownloaded(string voiceId)
    {
        try
        {
            return File.Exists(GetModelPath(voiceId))
                && File.Exists(GetTokensPath(voiceId))
                && Directory.Exists(GetEspeakDataDir(voiceId));
        }
        catch
        {
            // FindVoice throws on unknown voiceId — treat as not-downloaded.
            return false;
        }
    }

    /// <summary>
    /// Download and extract a Piper voice from the sherpa-onnx release.
    /// Reports progress as bytes downloaded / total bytes (extraction
    /// progress is not reported separately).
    /// Per-voice single-flight: concurrent calls for the same voice await
    /// the in-flight download instead of racing on the same temp tarball.
    /// </summary>
    public Task DownloadVoiceAsync(
        string voiceId,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = FindVoice(voiceId);
        if (IsVoiceDownloaded(info.VoiceId))
        {
            _logger.Info($"Piper voice '{info.VoiceId}' already downloaded");
            return Task.CompletedTask;
        }

        // Preflight: bail out before downloading 50-150 MB if the OS isn't
        // capable of extracting the .tar.bz2 we'd produce. tar.exe ships with
        // Windows 10 1803+; older systems would fail at the extract step
        // after a long, wasted download.
        EnsureExtractorAvailable();

        var key = info.VoiceId;
        var task = InFlightDownloads.GetOrAdd(key, _ => DownloadVoiceCoreAsync(info, progress, cancellationToken));
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
            InFlightDownloads.TryRemove(new KeyValuePair<string, Task>(key, task));
        }
    }

    private async Task DownloadVoiceCoreAsync(
        PiperVoiceInfo info,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        var voiceDir = Path.Combine(_voicesDirectory, info.VoiceId);
        Directory.CreateDirectory(voiceDir);
        var tarballPath = Path.Combine(voiceDir, $"{info.VoiceId}.tar.bz2.tmp");
        _logger.Info($"Downloading Piper voice '{info.VoiceId}' from {info.DownloadUrl}");

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            using var response = await httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var fileStream = new FileStream(tarballPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    downloaded += bytesRead;
                    progress?.Report((downloaded, totalBytes));
                }
            }

            _logger.Info($"Extracting Piper voice '{info.VoiceId}'");
            ExtractTarBz2(tarballPath, voiceDir, cancellationToken);

            // Verify the extraction produced the files we expect; if not,
            // tear the half-extracted dir down so a retry starts clean.
            if (!IsVoiceDownloaded(info.VoiceId))
            {
                throw new InvalidOperationException(
                    $"Extraction of Piper voice '{info.VoiceId}' did not produce the expected layout.");
            }

            _logger.Info($"Piper voice '{info.VoiceId}' ready at {voiceDir}");
        }
        catch
        {
            // Best-effort cleanup — leaves the user able to retry without
            // leftover partial files.
            try { if (File.Exists(tarballPath)) File.Delete(tarballPath); } catch { /* swallow */ }
            try { if (Directory.Exists(voiceDir) && !IsVoiceDownloaded(info.VoiceId)) Directory.Delete(voiceDir, recursive: true); } catch { /* swallow */ }
            throw;
        }
        finally
        {
            try { if (File.Exists(tarballPath)) File.Delete(tarballPath); } catch { /* swallow */ }
        }
    }

    /// <summary>Delete a downloaded voice directory.</summary>
    public bool DeleteVoice(string voiceId)
    {
        var info = FindVoice(voiceId);
        var dir = Path.Combine(_voicesDirectory, info.VoiceId);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        _logger.Info($"Deleted Piper voice '{info.VoiceId}'");
        return true;
    }

    /// <summary>Total disk usage of a downloaded voice, or 0 if not downloaded.</summary>
    public long GetVoiceSize(string voiceId)
    {
        var info = FindVoice(voiceId);
        var dir = Path.Combine(_voicesDirectory, info.VoiceId);
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { /* skip */ }
        }
        return total;
    }

    /// <summary>
    /// Probe the bundled OS tar.exe used by <see cref="ExtractTarBz2"/>.
    /// Throws a clear error before any network I/O happens so users on
    /// downlevel Windows aren't left with a half-downloaded tarball.
    /// </summary>
    private static void EnsureExtractorAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("tar.exe not found on PATH.");
            }
            proc.WaitForExit(2000);
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* swallow */ }
                throw new InvalidOperationException("tar.exe didn't respond to --version.");
            }
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"tar.exe --version returned exit code {proc.ExitCode}.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Piper voices need bundled tar (Windows 10 1803+). " +
                "Your system doesn't have tar on PATH; please update Windows or install a tar utility.", ex);
        }
    }

    /// <summary>
    /// Extract a .tar.bz2 archive in-place. We use SharpCompress (already a
    /// transitive dependency via PiperSharp's ecosystem, but explicit here)
    /// so we don't need to shell out to tar.exe.
    /// </summary>
    private static void ExtractTarBz2(string archivePath, string destinationDir, CancellationToken cancellationToken)
    {
        // SharpCompress isn't a direct dep of OpenClaw.Shared today; we
        // intentionally use the BCL .tar reader on top of a bzip2 stream
        // from a small inline implementation. Keeping the dep surface small
        // matters in this assembly because everything here is also referenced
        // from OpenClaw.Cli.
        //
        // .NET 7+ ships System.Formats.Tar; bzip2 is not in the BCL, so we
        // bring it in via a thin wrapper. For now the simplest-correct path
        // is to call out to the OS-bundled `tar` (Win10 1803+ ships it),
        // which transparently handles bz2.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            ArgumentList = { "-xjf", archivePath, "-C", destinationDir, "--strip-components=1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start tar to extract Piper voice");

        // Cancellation: kill the tar process if requested.
        using var reg = cancellationToken.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { /* swallow */ } });

        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"tar extraction failed (exit {proc.ExitCode}): {err}");
        }
    }

    private static PiperVoiceInfo FindVoice(string voiceId)
    {
        foreach (var v in AvailableVoices)
        {
            if (string.Equals(v.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        var available = string.Join(", ", AvailableVoicesIds());
        throw new ArgumentException($"Unknown Piper voice: '{voiceId}'. Available: {available}");
    }

    private static IEnumerable<string> AvailableVoicesIds()
    {
        foreach (var v in AvailableVoices) yield return v.VoiceId;
    }
}

/// <summary>Metadata about a Piper voice variant.</summary>
public sealed record PiperVoiceInfo(
    string VoiceId,
    string DisplayName,
    string LanguageTag,
    string DownloadUrl);
