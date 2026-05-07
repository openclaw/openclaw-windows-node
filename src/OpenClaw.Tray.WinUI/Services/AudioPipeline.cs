using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;

namespace OpenClawTray.Services;

/// <summary>
/// End-to-end audio pipeline: microphone capture → resample → VAD → buffer → Whisper STT.
/// Fires events for transcription results, VAD state changes, and audio levels.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly SpeechToTextService _stt;
    private readonly VoiceActivityDetector _vad;

    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private AudioPipelineOptions _options = new();

    // Resampling state
    private readonly List<float> _resampleBuffer = new();

    // VAD + buffering state
    private readonly List<float> _speechBuffer = new();
    // Pre-buffer: keeps the last ~300ms of audio before VAD triggers,
    // so the beginning of speech isn't lost.
    private readonly Queue<float[]> _preBuffer = new();
    private const int PreBufferChunks = 10; // ~320ms at 512 samples/16kHz
    private bool _isSpeaking;
    private int _silenceChunksCount;
    private int _silenceChunksThreshold;

    // State
    private AudioPipelineState _state = AudioPipelineState.Stopped;
    private CancellationTokenSource? _cts;

    // Backpressure: cap how many transcription Task.Run callbacks may be
    // outstanding at once. Each holds its own copy of the audio samples
    // for an entire silence-bounded utterance, so an unbounded queue
    // means unbounded RAM if Whisper falls behind. When we hit the cap
    // we drop the *new* segment with a diagnostic instead of queueing,
    // because piling up old utterances behind a stuck Whisper is a worse
    // UX than the user noticing one missed segment.
    private int _inFlightTranscriptions;
    private const int MaxConcurrentTranscriptions = 2;
    // Flag set by StopAsync so TranscribeSamplesAsync can distinguish
    // "Whisper actually failed" from "Whisper was interrupted by our own
    // cancel during shutdown" — the latter often surfaces as a misleading
    // "Failed to encode audio features" exception.
    private volatile bool _isStopping;

    // Fixed-duration capture mode: when set, OnDataAvailable bypasses the
    // VAD pipeline entirely and just appends every chunk to
    // _fixedCaptureBuffer for the duration of CaptureFixedDurationAsync.
    // This gives stt.transcribe a true bounded-window capture (vs.
    // stt.listen's silence-bounded behavior).
    private bool _fixedCaptureMode;
    private readonly List<float> _fixedCaptureBuffer = new();

    /// <summary>Fired when a single Whisper segment has been transcribed.
    /// Multiple of these may fire per silence-bounded utterance — useful
    /// for streaming bubble updates. Consumers that want a complete
    /// utterance (chat submission, stt.listen result) should listen on
    /// <see cref="UtteranceTranscribed"/> instead.</summary>
    public event Action<TranscriptionResult>? TranscriptionReady;

    /// <summary>Fired exactly once per silence-bounded utterance, after
    /// all Whisper segments for that utterance have been emitted. Carries
    /// an immutable snapshot of every segment plus the concatenated text.</summary>
    public event Action<UtteranceResult>? UtteranceTranscribed;

    /// <summary>Fired when VAD detects speech start/end.</summary>
    public event Action<VadEvent>? VoiceActivityChanged;

    /// <summary>Fired with RMS audio level for visualization (0.0–1.0).</summary>
    public event Action<float>? AudioLevelChanged;

    /// <summary>Fired when pipeline state changes.</summary>
    public event Action<AudioPipelineState>? StateChanged;

    /// <summary>Fired with diagnostic status messages for the UI.</summary>
    public event Action<string>? DiagnosticMessage;

    /// <summary>Current pipeline state.</summary>
    public AudioPipelineState State => _state;

    /// <summary>When true, incoming audio is ignored (prevents echo during TTS playback).</summary>
    public bool IsMuted { get; set; }

    public AudioPipeline(IOpenClawLogger logger, SpeechToTextService stt, VoiceActivityDetector vad)
    {
        _logger = logger;
        _stt = stt;
        _vad = vad;
    }

    /// <summary>Start capturing and processing audio.</summary>
    public async Task StartAsync(AudioPipelineOptions options, CancellationToken cancellationToken = default)
    {
        if (_state != AudioPipelineState.Stopped)
            throw new InvalidOperationException($"Pipeline is {_state}, must be Stopped to start.");

        _options = options;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Calculate silence threshold: how many VAD chunks = silence timeout
        float chunkDurationSec = (float)VoiceActivityDetector.ChunkSamples / VoiceActivityDetector.SampleRate;
        _silenceChunksThreshold = Math.Max(1, (int)(options.SilenceTimeoutSeconds / chunkDurationSec));

        SetState(AudioPipelineState.Starting);

        try
        {
            // WASAPI COM objects must be created on an MTA thread, not the
            // WinUI STA dispatcher thread. Run capture init on the thread pool.
            await Task.Run(() =>
            {
                _capture = new WasapiCapture();
                _captureFormat = _capture.WaveFormat;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
            });

            _speechBuffer.Clear();
            _resampleBuffer.Clear();
            _isSpeaking = false;
            _silenceChunksCount = 0;
            _dataCallbackCount = 0;
            _vadChunkCount = 0;

            SetState(AudioPipelineState.Listening);
            var sttStatus = _stt.IsModelLoaded ? "loaded" : "NOT loaded";
            _logger.Info($"Audio pipeline started: {_captureFormat.SampleRate}Hz {_captureFormat.BitsPerSample}bit {_captureFormat.Channels}ch → 16kHz mono, VAD=energy, STT={sttStatus}");
            DiagnosticMessage?.Invoke($"Mic: {_captureFormat.SampleRate}Hz, STT model: {sttStatus}");
        }
        catch (System.Runtime.InteropServices.COMException ex) when (
            ex.HResult == unchecked((int)0x80070005) || // E_ACCESSDENIED
            ex.HResult == unchecked((int)0x88890008))   // AUDCLNT_E_DEVICE_INVALIDATED
        {
            _logger.Error("Microphone access denied", ex);
            SetState(AudioPipelineState.Error);
            DiagnosticMessage?.Invoke("⚠️ Microphone access denied — check Windows Settings → Privacy → Microphone");
            // Release the partially-initialised capture device.
            CleanupCapture();
            throw new InvalidOperationException(
                "Microphone access denied. Open Windows Settings → Privacy & Security → Microphone and enable 'Let desktop apps access your microphone'.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start audio capture", ex);
            SetState(AudioPipelineState.Error);
            DiagnosticMessage?.Invoke($"⚠️ Mic error: {ex.Message}");
            // Release the partially-initialised capture device and CTS so
            // the mic LED doesn't stay on after a failed start.
            CleanupCapture();
            throw;
        }
    }

    /// <summary>
    /// Capture audio for exactly <paramref name="durationMs"/> milliseconds
    /// (or until the token is cancelled), then return the entire 16 kHz
    /// mono float buffer. Bypasses VAD entirely — every sample in the
    /// window is preserved. Used by stt.transcribe to honor the
    /// "bounded fixed-duration capture" contract.
    /// </summary>
    public async Task<float[]> CaptureFixedDurationAsync(int durationMs, CancellationToken cancellationToken = default)
    {
        if (_state != AudioPipelineState.Stopped)
            throw new InvalidOperationException($"Pipeline is {_state}, must be Stopped to start capture.");
        if (durationMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "Duration must be positive.");

        _fixedCaptureMode = true;
        _fixedCaptureBuffer.Clear();
        _resampleBuffer.Clear();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetState(AudioPipelineState.Starting);
        try
        {
            await Task.Run(() =>
            {
                _capture = new WasapiCapture();
                _captureFormat = _capture.WaveFormat;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
            });

            SetState(AudioPipelineState.Listening);
            try { DiagnosticMessage?.Invoke($"Recording {durationMs / 1000.0:F1}s..."); } catch { }

            try
            {
                await Task.Delay(durationMs, _cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // External cancellation: return whatever we have so far.
            }

            // Stop capture and give NAudio a moment to flush its last buffer.
            try { _capture?.StopRecording(); } catch { /* swallow */ }
            await Task.Delay(150).ConfigureAwait(false);

            return _fixedCaptureBuffer.ToArray();
        }
        finally
        {
            _fixedCaptureMode = false;
            _fixedCaptureBuffer.Clear();
            CleanupCapture();
            SetState(AudioPipelineState.Stopped);
        }
    }

    /// <summary>Stop capturing and processing.</summary>
    public async Task StopAsync()
    {
        if (_state == AudioPipelineState.Stopped)
            return;

        _isStopping = true;
        try
        {
            // Order matters here. Previously we cancelled `_cts` first and THEN
            // tried to flush the speech buffer — but the flush passed `_cts.Token`
            // straight into Whisper.net, which honored the cancel and dropped the
            // final utterance. Now:
            //
            //   1. Stop capturing new audio so the buffer doesn't grow further.
            //   2. Wait briefly for any in-flight transcriptions (Task.Run-spawned
            //      from earlier VAD bursts) to finish — so the user's last
            //      utterance reaches Whisper instead of being killed mid-encode.
            //   3. Flush any buffered speech using a fresh (non-cancelled) token
            //      so anything left over also reaches Whisper.
            //   4. Cancel `_cts` to stop background work that hasn't drained yet.
            //   5. Tear down capture resources.
            if (_capture != null)
            {
                try { _capture.StopRecording(); }
                catch (Exception ex) { _logger.Error("Error stopping capture", ex); }
            }

            // Drain in-flight transcriptions, capped at 3 s so Stop never hangs.
            var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (Volatile.Read(ref _inFlightTranscriptions) > 0 && DateTime.UtcNow < drainDeadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (_speechBuffer.Count > 0 && _stt.IsModelLoaded)
            {
                await FlushSpeechBufferAsync();
            }

            _cts?.Cancel();

            CleanupCapture();
            SetState(AudioPipelineState.Stopped);
            _logger.Info("Audio pipeline stopped");
        }
        finally
        {
            _isStopping = false;
        }
    }

    private int _dataCallbackCount;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_cts?.IsCancellationRequested == true || e.BytesRecorded == 0 || IsMuted)
            return;

        _dataCallbackCount++;

        try
        {
            var sourceSamples = ConvertToFloat(e.Buffer, e.BytesRecorded, _captureFormat!);
            var resampled = ResampleTo16kMono(sourceSamples, _captureFormat!);

            // Amplify: many laptop mics produce very low levels.
            const float gain = 5.0f;
            for (int i = 0; i < resampled.Length; i++)
                resampled[i] = Math.Clamp(resampled[i] * gain, -1.0f, 1.0f);

            // Compute RMS for level visualization
            if (resampled.Length > 0)
            {
                float sumSquares = 0;
                for (int i = 0; i < resampled.Length; i++)
                    sumSquares += resampled[i] * resampled[i];
                var rms = MathF.Sqrt(sumSquares / resampled.Length);
                AudioLevelChanged?.Invoke(Math.Clamp(rms * 3f, 0f, 1f));
            }

            // Fixed-duration capture mode: skip VAD entirely; we want every
            // sample for the full window. CaptureFixedDurationAsync drains
            // the buffer when the timer fires.
            if (_fixedCaptureMode)
            {
                _fixedCaptureBuffer.AddRange(resampled);
                return;
            }

            _resampleBuffer.AddRange(resampled);
            ProcessVadChunks();
        }
        catch (Exception ex)
        {
            _logger.Error("Error processing audio data", ex);
            if (_dataCallbackCount <= 3)
                try { DiagnosticMessage?.Invoke($"⚠️ Audio error: {ex.Message}"); } catch { }
        }
    }

    private int _vadChunkCount;
    private int _speechChunkCount; // how many speech chunks in current utterance

    private void ProcessVadChunks()
    {
        while (_resampleBuffer.Count >= VoiceActivityDetector.ChunkSamples)
        {
            var chunk = _resampleBuffer.GetRange(0, VoiceActivityDetector.ChunkSamples).ToArray();
            _resampleBuffer.RemoveRange(0, VoiceActivityDetector.ChunkSamples);

            // Compute RMS energy of this chunk
            float energy = 0;
            for (int i = 0; i < chunk.Length; i++)
                energy += chunk[i] * chunk[i];
            energy = MathF.Sqrt(energy / chunk.Length);

            _vadChunkCount++;

            // Hysteresis: use a higher threshold to START detecting speech,
            // and a lower threshold to STAY in speech mode. This prevents
            // brief pauses between words from ending the utterance.
            const float startThreshold = 0.03f;  // energy to begin speech
            const float stayThreshold = 0.008f;   // energy to remain in speech (much lower)

            bool chunkIsSpeech = _isSpeaking
                ? energy >= stayThreshold
                : energy >= startThreshold;

            if (chunkIsSpeech)
            {
                if (!_isSpeaking)
                {
                    _isSpeaking = true;
                    _silenceChunksCount = 0;
                    _speechChunkCount = 0;
                    try { VoiceActivityChanged?.Invoke(new VadEvent { IsSpeaking = true, Probability = energy }); } catch { }
                    try { DiagnosticMessage?.Invoke("🗣️ Listening..."); } catch { }

                    // Prepend the pre-buffer so we don't lose the speech onset
                    while (_preBuffer.Count > 0)
                        _speechBuffer.AddRange(_preBuffer.Dequeue());
                }
                _speechBuffer.AddRange(chunk);
                _speechChunkCount++;
                _silenceChunksCount = 0;
            }
            else if (_isSpeaking)
            {
                _speechBuffer.AddRange(chunk);
                _silenceChunksCount++;

                if (_silenceChunksCount >= _silenceChunksThreshold)
                {
                    _isSpeaking = false;
                    try { VoiceActivityChanged?.Invoke(new VadEvent { IsSpeaking = false, Probability = energy }); } catch { }

                    var samples = _speechBuffer.ToArray();
                    _speechBuffer.Clear();
                    _silenceChunksCount = 0;

                    // Only transcribe if we had enough speech (not just a brief noise)
                    var durationSec = (float)samples.Length / VoiceActivityDetector.SampleRate;
                    if (_speechChunkCount < 10) // less than ~320ms of actual speech
                    {
                        try { DiagnosticMessage?.Invoke("Speak now — I'm listening"); } catch { }
                    }
                    else
                    {
                        try { DiagnosticMessage?.Invoke($"Transcribing {durationSec:F1}s of speech..."); } catch { }

                        // Bounded in-flight count. If Whisper is stuck or
                        // slow, dropping a segment is preferable to letting
                        // a queue of stale utterances arrive minutes later.
                        if (Interlocked.Increment(ref _inFlightTranscriptions) > MaxConcurrentTranscriptions)
                        {
                            Interlocked.Decrement(ref _inFlightTranscriptions);
                            try { DiagnosticMessage?.Invoke("⚠️ Transcription backlog — segment dropped"); } catch { }
                        }
                        else
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await TranscribeSamplesAsync(samples);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error("Transcription task failed", ex);
                                    try { DiagnosticMessage?.Invoke($"⚠️ Error: {ex.Message}"); } catch { }
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref _inFlightTranscriptions);
                                }
                            });
                        }
                    }
                }
            }
            else
            {
                // Not speaking — maintain rolling pre-buffer
                _preBuffer.Enqueue(chunk);
                while (_preBuffer.Count > PreBufferChunks)
                    _preBuffer.Dequeue();
            }
        }
    }

    private async Task TranscribeSamplesAsync(float[] samples, CancellationToken? overrideToken = null)
    {
        if (!_stt.IsModelLoaded || samples.Length == 0)
        {
            DiagnosticMessage?.Invoke(_stt.IsModelLoaded ? "Empty audio segment" : "⚠️ Model not loaded");
            return;
        }

        // Skip very short segments (< 0.3 seconds)
        if (samples.Length < VoiceActivityDetector.SampleRate * 0.3f)
        {
            DiagnosticMessage?.Invoke("Segment too short, skipped");
            return;
        }

        SetState(AudioPipelineState.Processing);
        try
        {
            // overrideToken is used by FlushSpeechBufferAsync during teardown
            // so the final utterance isn't dropped when the pipeline cancel
            // token is about to fire.
            var token = overrideToken ?? _cts?.Token ?? CancellationToken.None;
            var results = await _stt.TranscribeAsync(samples, _options.Language, token);

            if (results.Count == 0)
            {
                try { DiagnosticMessage?.Invoke("No speech recognized in segment"); } catch { }
            }

            foreach (var result in results)
            {
                _logger.Info($"Transcription: \"{result.Text}\"");
                try { TranscriptionReady?.Invoke(result); } catch (Exception ex)
                {
                    _logger.Error("TranscriptionReady handler failed", ex);
                }
            }

            // Emit a single completed-utterance event so consumers that care
            // about "the full thing the user just said" (chat submission,
            // stt.listen) don't fire on every fragment.
            if (results.Count > 0)
            {
                var snapshot = results.ToArray();
                var aggregate = new UtteranceResult
                {
                    Text = string.Join(" ", snapshot.Select(r => r.Text)).Trim(),
                    Language = snapshot[0].Language,
                    Start = snapshot[0].Start,
                    End = snapshot[^1].End,
                    Segments = snapshot
                };
                try { UtteranceTranscribed?.Invoke(aggregate); } catch (Exception ex)
                {
                    _logger.Error("UtteranceTranscribed handler failed", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // If we're tearing the pipeline down, mid-encode interruptions
            // surface from Whisper.net as misleading exceptions like
            // "Failed to encode audio features." instead of a clean
            // OperationCanceledException. Suppress those — the user already
            // knows they pressed Stop.
            if (_isStopping || (_cts?.IsCancellationRequested ?? false))
            {
                _logger.Info($"Transcription interrupted during shutdown ({ex.GetType().Name})");
                return;
            }
            _logger.Error("Transcription failed", ex);
            // Sanitized — the raw ex.Message can include sample lengths,
            // language tags, or other audio-shape detail.
            try { DiagnosticMessage?.Invoke("⚠️ Transcription error"); } catch { }
        }
        finally
        {
            if (_state == AudioPipelineState.Processing)
                SetState(AudioPipelineState.Listening);
        }
    }

    private async Task FlushSpeechBufferAsync()
    {
        if (_speechBuffer.Count == 0) return;

        var samples = _speechBuffer.ToArray();
        _speechBuffer.Clear();

        try
        {
            // Pass CancellationToken.None — the flush is the last chance
            // to transcribe the user's final utterance during teardown,
            // so it must not be killable by the pipeline's own cancel
            // token (which StopAsync is about to fire).
            await TranscribeSamplesAsync(samples, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Flush transcription failed", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.Error("Recording stopped with error", e.Exception);
            SetState(AudioPipelineState.Error);
            DiagnosticMessage?.Invoke($"⚠️ Microphone error: {e.Exception.Message}");
        }
    }

    /// <summary>Convert raw audio bytes to float samples based on wave format.</summary>
    private static float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;
        var result = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * bytesPerSample;
            if (offset + bytesPerSample > bytesRecorded) break;

            result[i] = format.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                32 when format.Encoding == WaveFormatEncoding.IeeeFloat
                    => BitConverter.ToSingle(buffer, offset),
                32 => BitConverter.ToInt32(buffer, offset) / (float)int.MaxValue,
                24 => (buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16)) / 8388608f,
                _ => 0f
            };
        }

        return result;
    }

    /// <summary>Resample multi-channel audio to 16 kHz mono.</summary>
    private static float[] ResampleTo16kMono(float[] input, WaveFormat sourceFormat)
    {
        int sourceRate = sourceFormat.SampleRate;
        int channels = sourceFormat.Channels;

        // First: downmix to mono if needed
        float[] mono;
        if (channels > 1)
        {
            int monoSamples = input.Length / channels;
            mono = new float[monoSamples];
            for (int i = 0; i < monoSamples; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                    sum += input[i * channels + ch];
                mono[i] = sum / channels;
            }
        }
        else
        {
            mono = input;
        }

        // If already 16kHz, return as-is
        if (sourceRate == 16000)
            return mono;

        // Simple linear interpolation resampling
        double ratio = (double)sourceRate / 16000;
        int outputSamples = (int)(mono.Length / ratio);
        if (outputSamples == 0) return [];

        var output = new float[outputSamples];
        for (int i = 0; i < outputSamples; i++)
        {
            double srcIndex = i * ratio;
            int idx = (int)srcIndex;
            float frac = (float)(srcIndex - idx);

            if (idx + 1 < mono.Length)
                output[i] = mono[idx] * (1 - frac) + mono[idx + 1] * frac;
            else if (idx < mono.Length)
                output[i] = mono[idx];
        }

        return output;
    }

    private void SetState(AudioPipelineState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    private void CleanupCapture()
    {
        if (_capture != null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
            }
            catch (Exception ex)
            {
                _logger.Error("Error detaching capture event handlers", ex);
            }

            try
            {
                _capture.Dispose();
            }
            catch (Exception ex)
            {
                // NAudio's WasapiCapture.Dispose may throw on a stuck COM
                // object. Log but never propagate — this method is called
                // from finally-blocks and re-throwing would mask the original
                // failure AND leave the mic device held by the OS until
                // process exit.
                _logger.Error("Error disposing audio capture", ex);
            }
            finally
            {
                _capture = null;
            }
        }

        try
        {
            _cts?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("Error disposing pipeline cancellation source", ex);
        }
        finally
        {
            _cts = null;
        }

        _resampleBuffer.Clear();
        _speechBuffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
