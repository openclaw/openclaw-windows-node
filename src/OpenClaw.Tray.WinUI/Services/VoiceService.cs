using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

/// <summary>Voice interaction modes.</summary>
public enum VoiceMode
{
    Inactive,
    PushToTalk,
    VoiceChat
}

/// <summary>
/// Orchestrates voice interactions: push-to-talk and voice chat session modes.
/// Manages the audio pipeline lifecycle and coordinates with the gateway.
/// </summary>
public sealed class VoiceService : IAsyncDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly SettingsManager _settings;
    private readonly SpeechToTextService _stt;
    private readonly VoiceActivityDetector _vad;
    private readonly WhisperModelManager _modelManager;
    private AudioPipeline? _pipeline;
    private VoiceMode _currentMode = VoiceMode.Inactive;
    private CancellationTokenSource? _sessionCts;

    // Path to the bundled Silero VAD model (deployed with the app)
    private string? _vadModelPath;

    /// <summary>Current voice interaction mode.</summary>
    public VoiceMode CurrentMode => _currentMode;

    /// <summary>Whether a Whisper model is loaded and ready to transcribe.</summary>
    public bool IsModelLoaded => _stt.IsModelLoaded;

    /// <summary>Whether the configured model has been downloaded.</summary>
    public bool IsModelDownloaded => _modelManager.IsModelDownloaded(_settings.SttModelName);

    // ============================================================
    // Engine adapter surface (consumed by NodeService's STT selector).
    //
    // Whisper is "ready" only when the model is both downloaded AND loaded
    // into memory. Anything else falls back to the WinRT engine inside the
    // selector — kept transparent at the SttCapability response surface.
    // ============================================================

    /// <summary>True when Whisper can serve a transcribe / listen call right now.</summary>
    public bool IsWhisperReady => _stt.IsModelLoaded;

    /// <summary>True while the Whisper model is actively downloading.</summary>
    public bool IsWhisperDownloadingModel => _modelDownloadInProgress;

    /// <summary>0.0..1.0 download progress when <see cref="IsWhisperDownloadingModel"/>; null otherwise.</summary>
    public double? WhisperModelDownloadProgress => _modelDownloadInProgress ? _modelDownloadProgress : null;

    private volatile bool _modelDownloadInProgress;
    private volatile float _modelDownloadProgress;

    /// <summary>Fired when a single Whisper segment is transcribed (per-fragment;
    /// useful for streaming UI updates). For "the full thing the user said",
    /// listen on <see cref="UtteranceCompleted"/> instead.</summary>
    public event Action<string>? TranscriptionReceived;

    /// <summary>Fired exactly once per silence-bounded utterance. Carries the
    /// concatenated text and an immutable snapshot of every segment.</summary>
    public event Action<UtteranceResult>? UtteranceCompleted;

    /// <summary>Fired when voice mode changes.</summary>
    public event Action<VoiceMode>? ModeChanged;

    /// <summary>Fired when VAD state changes (speaking/silence).</summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>Fired with audio level for waveform visualization (0.0–1.0).</summary>
    public event Action<float>? AudioLevelChanged;

    /// <summary>Fired with diagnostic messages for the UI.</summary>
    public event Action<string>? DiagnosticMessage;

    /// <summary>Fired when pipeline state changes.</summary>
    public event Action<AudioPipelineState>? PipelineStateChanged;

    /// <summary>When true, the pipeline ignores audio input (used during TTS playback to prevent echo).</summary>
    public bool IsMutedForPlayback
    {
        get => _pipeline?.IsMuted ?? false;
        set
        {
            if (_pipeline != null)
                _pipeline.IsMuted = value;
        }
    }

    public VoiceService(IOpenClawLogger logger, SettingsManager settings)
    {
        _logger = logger;
        _settings = settings;
        _stt = new SpeechToTextService(logger);
        _vad = new VoiceActivityDetector(logger);
        _modelManager = new WhisperModelManager(SettingsManager.SettingsDirectoryPath, logger);
    }

    /// <summary>
    /// Ensure the VAD and STT models are loaded and ready.
    /// Downloads the Whisper model if needed.
    /// </summary>
    public async Task InitializeAsync(
        IProgress<(long downloaded, long total)>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        // Load VAD model
        if (!_vad.IsLoaded)
        {
            var vadPath = FindVadModelPath();
            if (vadPath == null)
            {
                // Auto-download Silero VAD model
                DiagnosticMessage?.Invoke("Downloading voice activity model…");
                vadPath = await DownloadVadModelAsync(cancellationToken);
            }
            if (vadPath != null)
            {
                _vad.LoadModel(vadPath);
            }
            else
            {
                _logger.Info("Silero VAD model not found — VAD will be unavailable");
            }
        }

        // Download Whisper model if needed
        var modelName = _settings.SttModelName;
        if (!_modelManager.IsModelDownloaded(modelName))
        {
            _logger.Info($"Downloading Whisper model '{modelName}'...");
            DiagnosticMessage?.Invoke($"Downloading Whisper '{modelName}' model on first use (~one-time, ~140 MB)…");
            await _modelManager.DownloadModelAsync(modelName, downloadProgress, cancellationToken);
            DiagnosticMessage?.Invoke("Whisper model downloaded. Loading…");
        }

        // Load Whisper model
        if (!_stt.IsModelLoaded)
        {
            var modelPath = _modelManager.GetModelPath(modelName);
            _stt.LoadModel(modelPath);
        }
    }

    /// <summary>
    /// Start push-to-talk: begins listening immediately.
    /// Call <see cref="StopPushToTalkAsync"/> when the user releases the key.
    /// </summary>
    public async Task StartPushToTalkAsync()
    {
        if (_currentMode != VoiceMode.Inactive)
        {
            _logger.Info("Voice already active, ignoring PTT start");
            return;
        }

        await EnsureInitializedAsync();
        SetMode(VoiceMode.PushToTalk);

        _sessionCts = new CancellationTokenSource();
        _pipeline = new AudioPipeline(_logger, _stt, _vad);
        WirePipelineEvents(_pipeline);

        var options = new AudioPipelineOptions
        {
            ModelPath = _modelManager.GetModelPath(_settings.SttModelName),
            Language = _settings.SttLanguage,
            SilenceTimeoutSeconds = 30, // For PTT, don't auto-stop on silence
            VadThreshold = 0.5f
        };

        try
        {
            await _pipeline.StartAsync(options, _sessionCts.Token);
            _logger.Info("Push-to-talk started");
        }
        catch
        {
            // Clean up on failure so the service isn't stuck in a broken state
            await CleanupSessionAsync();
            throw;
        }
    }

    /// <summary>Stop push-to-talk.</summary>
    public Task StopPushToTalkAsync() => StopAsync();

    /// <summary>
    /// Start a voice chat session with continuous listening and auto-submit on silence.
    /// </summary>
    public async Task StartVoiceChatAsync()
    {
        if (_currentMode != VoiceMode.Inactive)
        {
            _logger.Info("Voice already active, ignoring voice chat start");
            return;
        }

        await EnsureInitializedAsync();
        SetMode(VoiceMode.VoiceChat);

        _sessionCts = new CancellationTokenSource();
        _pipeline = new AudioPipeline(_logger, _stt, _vad);
        WirePipelineEvents(_pipeline);

        var options = new AudioPipelineOptions
        {
            ModelPath = _modelManager.GetModelPath(_settings.SttModelName),
            Language = _settings.SttLanguage,
            SilenceTimeoutSeconds = _settings.SttSilenceTimeout,
            VadThreshold = 0.5f
        };

        try
        {
            await _pipeline.StartAsync(options, _sessionCts.Token);
            _logger.Info("Voice chat session started");
        }
        catch
        {
            await CleanupSessionAsync();
            throw;
        }
    }

    /// <summary>Stop the current voice chat session.</summary>
    public Task StopVoiceChatAsync() => StopAsync();

    /// <summary>Stop any active voice mode.</summary>
    public async Task StopAsync()
    {
        if (_currentMode == VoiceMode.Inactive) return;
        await CleanupSessionAsync();
    }

    private async Task CleanupSessionAsync()
    {
        if (_pipeline != null)
        {
            try { await _pipeline.StopAsync(); } catch { }
            try { await _pipeline.DisposeAsync(); } catch { }
            _pipeline = null;
        }

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;

        SetMode(VoiceMode.Inactive);
    }

    /// <summary>
    /// Handle an agent-initiated stt.listen request:
    /// start the mic, wait for one complete silence-bounded utterance,
    /// return the transcription. Multi-segment utterances are concatenated
    /// before returning so callers never receive a partial first-segment.
    /// </summary>
    public async Task<SttListenResult> ListenOnceAsync(SttListenArgs args, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync();

        using var timeoutCts = new CancellationTokenSource(args.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var pipeline = new AudioPipeline(_logger, _stt, _vad);
        var tcs = new TaskCompletionSource<SttListenResult>();
        var sw = Stopwatch.StartNew();

        pipeline.UtteranceTranscribed += utterance =>
        {
            // Snapshot already immutable (UtteranceResult.Segments is a fresh
            // array). Map to the wire-shape SttSegment.
            var segments = new List<SttSegment>(utterance.Segments.Count);
            foreach (var s in utterance.Segments)
            {
                segments.Add(new SttSegment
                {
                    Text = s.Text,
                    StartMs = (int)s.Start.TotalMilliseconds,
                    EndMs = (int)s.End.TotalMilliseconds
                });
            }

            tcs.TrySetResult(new SttListenResult
            {
                Text = utterance.Text,
                Language = utterance.Language ?? "",
                DurationMs = (int)sw.ElapsedMilliseconds,
                Segments = segments
            });
        };

        var options = new AudioPipelineOptions
        {
            Language = args.Language,
            SilenceTimeoutSeconds = 2.0f,
            VadThreshold = 0.5f
        };

        try
        {
            await pipeline.StartAsync(options, linkedCts.Token);

            // Wait for either an utterance or timeout/cancellation.
            // We don't throw immediately on timeout — pipeline.StopAsync's
            // flush path may still produce an UtteranceTranscribed for
            // speech that was buffered when the timer fired. Only after
            // giving the flush a brief window do we report timeout.
            var sentinel = new TaskCompletionSource<bool>();
            using (linkedCts.Token.Register(() => sentinel.TrySetResult(true)))
            {
                var winner = await Task.WhenAny(tcs.Task, sentinel.Task).ConfigureAwait(false);
                if (winner == tcs.Task)
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }

            // Timeout / external cancellation. Stop the pipeline (which
            // flushes any buffered speech) and give UtteranceTranscribed
            // up to 2 s to fire before reporting timeout.
            try { await pipeline.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
            if (tcs.Task.IsCompletedSuccessfully)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            throw new TimeoutException("No speech detected within timeout");
        }
        finally
        {
            try { await pipeline.StopAsync(); } catch { /* idempotent — already stopped above on timeout */ }
            await pipeline.DisposeAsync();
        }
    }

    /// <summary>
    /// Handle a fixed-duration <c>stt.transcribe</c> request. Captures
    /// audio for exactly <c>args.MaxDurationMs</c> milliseconds (no
    /// VAD-based early termination), then transcribes the entire
    /// captured window. Use this for "record N ms and tell me what's in
    /// it" callers; use <see cref="ListenOnceAsync"/> for "listen until
    /// the user stops speaking" callers.
    /// </summary>
    public async Task<SttTranscribeResult> TranscribeFixedDurationAsync(
        SttTranscribeArgs args,
        CancellationToken cancellationToken)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));
        if (args.MaxDurationMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "maxDurationMs must be positive.");

        await EnsureInitializedAsync();

        var pipeline = new AudioPipeline(_logger, _stt, _vad);
        var sw = Stopwatch.StartNew();
        try
        {
            var samples = await pipeline.CaptureFixedDurationAsync(args.MaxDurationMs, cancellationToken).ConfigureAwait(false);
            var captureMs = (int)sw.ElapsedMilliseconds;

            if (samples.Length == 0)
            {
                return new SttTranscribeResult
                {
                    Transcribed = false,
                    Text = "",
                    DurationMs = captureMs,
                    Language = args.Language ?? "auto",
                    EngineEffective = SttCapability.EngineWhisper
                };
            }

            var lang = !string.IsNullOrWhiteSpace(args.Language)
                ? args.Language!
                : _settings.SttLanguage ?? "auto";

            var results = await _stt.TranscribeAsync(samples, lang, cancellationToken).ConfigureAwait(false);
            var text = string.Join(" ", results.Select(r => r.Text)).Trim();

            return new SttTranscribeResult
            {
                Transcribed = !string.IsNullOrEmpty(text),
                Text = text,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Language = results.Count > 0 ? results[0].Language : lang,
                EngineEffective = SttCapability.EngineWhisper
            };
        }
        finally
        {
            await pipeline.DisposeAsync();
        }
    }

    // GetStatusAsync was previously tied to the old SttStatusResult shape
    // (ModelLoaded / ModelName / IsListening). The unified status now lives
    // in NodeService.OnSttStatusAsync, which probes both engines and reports
    // per-engine readiness. VoiceService just exposes the raw signals
    // (IsWhisperReady, IsWhisperDownloadingModel, WhisperModelDownloadProgress)
    // that the selector consumes.

    /// <summary>
    /// Download the configured Whisper model with progress reporting.
    /// Sets <see cref="IsWhisperDownloadingModel"/> for the duration so the
    /// STT selector can fall back to WinRT while a download is in flight.
    /// </summary>
    public async Task DownloadModelAsync(
        string? modelName = null,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = modelName ?? _settings.SttModelName;
        _modelDownloadInProgress = true;
        _modelDownloadProgress = 0f;
        try
        {
            // Wrap caller's progress reporter so we also keep our internal
            // 0..1 snapshot updated for the stt.status surface.
            var wrapped = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                    _modelDownloadProgress = (float)((double)p.downloaded / p.total);
                progress?.Report(p);
            });
            await _modelManager.DownloadModelAsync(resolved, wrapped, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _modelDownloadInProgress = false;
            _modelDownloadProgress = 0f;
        }
    }

    /// <summary>Check if the specified model is downloaded.</summary>
    public bool CheckModelDownloaded(string? modelName = null)
        => _modelManager.IsModelDownloaded(modelName ?? _settings.SttModelName);

    /// <summary>Get available model information.</summary>
    public WhisperModelInfo[] GetAvailableModels() => WhisperModelManager.AvailableModels;

    private async Task EnsureInitializedAsync()
    {
        if (!_stt.IsModelLoaded)
        {
            await InitializeAsync();
        }
    }

    private void WirePipelineEvents(AudioPipeline pipeline)
    {
        pipeline.TranscriptionReady += result =>
        {
            TranscriptionReceived?.Invoke(result.Text);
        };

        pipeline.UtteranceTranscribed += utterance =>
        {
            UtteranceCompleted?.Invoke(utterance);
        };

        pipeline.VoiceActivityChanged += vad =>
        {
            SpeakingChanged?.Invoke(vad.IsSpeaking);
        };

        pipeline.AudioLevelChanged += level =>
        {
            AudioLevelChanged?.Invoke(level);
        };

        pipeline.StateChanged += state =>
        {
            PipelineStateChanged?.Invoke(state);
        };

        pipeline.DiagnosticMessage += msg =>
        {
            DiagnosticMessage?.Invoke(msg);
        };
    }

    private void SetMode(VoiceMode mode)
    {
        if (_currentMode == mode) return;
        _currentMode = mode;
        ModeChanged?.Invoke(mode);
        _logger.Info($"Voice mode changed: {mode}");
    }

    /// <summary>
    /// Locate the Silero VAD ONNX model. Looks in the app's Assets folder
    /// and the models directory.
    /// </summary>
    private string? FindVadModelPath()
    {
        if (_vadModelPath != null && File.Exists(_vadModelPath))
            return _vadModelPath;

        // Check Assets directory (deployed with the app)
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        if (File.Exists(assetsPath))
        {
            _vadModelPath = assetsPath;
            return assetsPath;
        }

        // Check models directory
        var modelsPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "models", "silero_vad.onnx");
        if (File.Exists(modelsPath))
        {
            _vadModelPath = modelsPath;
            return modelsPath;
        }

        return null;
    }

    private const string VadDownloadUrl = SileroVadModelManifest.DownloadUrl;

    /// <summary>Download the Silero VAD ONNX model if not already present.
    /// SHA-256 is verified before the atomic rename so a tampered or
    /// truncated file never lands at the canonical path. See
    /// <see cref="SileroVadModelManifest"/>.</summary>
    private async Task<string?> DownloadVadModelAsync(CancellationToken cancellationToken)
    {
        var destPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "models", SileroVadModelManifest.FileName);
        if (File.Exists(destPath))
            return destPath;

        _logger.Info("Downloading Silero VAD model...");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tempPath = destPath + ".tmp";
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            using var response = await http.GetAsync(VadDownloadUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cancellationToken);
            }

            // SECURITY: verify SHA-256 BEFORE the atomic rename so a
            // tampered file never reaches ONNX Runtime.
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var verifyStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var actual = await sha.ComputeHashAsync(verifyStream, cancellationToken);
                var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
                if (!string.Equals(actualHex, SileroVadModelManifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.Security.SecurityException(
                        "Silero VAD model failed integrity check. The downloaded file does not match the pinned SHA-256.");
                }
            }

            File.Move(tempPath, destPath, overwrite: true);
            _logger.Info($"Silero VAD model downloaded and verified ({new FileInfo(destPath).Length:N0} bytes)");
            _vadModelPath = destPath;
            return destPath;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to download VAD model", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stt.Dispose();
        _vad.Dispose();
    }
}
