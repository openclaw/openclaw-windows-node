using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using SherpaOnnx;

namespace OpenClawTray.Services;

/// <summary>
/// Wraps Sherpa-ONNX <see cref="OfflineTts"/> with a Piper voice for
/// fully-local neural text-to-speech.
///
/// One instance owns one loaded voice. Callers ensure the voice is
/// downloaded (via <see cref="PiperVoiceManager"/>) before constructing
/// this service. Generation is single-flight: a second concurrent
/// <see cref="GenerateWavAsync"/> call waits behind the first.
///
/// Output is 16-bit PCM mono WAV at the model's native sample rate
/// (typically 22 050 Hz for Piper-low, 16 000 Hz for some others). The
/// caller is responsible for playback.
/// </summary>
public sealed class PiperTextToSpeechClient : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly string _voiceId;
    private readonly OfflineTts _tts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public string VoiceId => _voiceId;
    public int SampleRate => _tts.SampleRate;

    public PiperTextToSpeechClient(IOpenClawLogger logger, PiperVoiceManager voices, string voiceId)
    {
        _logger = logger;
        _voiceId = voiceId;

        if (!voices.IsVoiceDownloaded(voiceId))
            throw new InvalidOperationException($"Piper voice '{voiceId}' is not downloaded.");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = voices.GetModelPath(voiceId);
        config.Model.Vits.Tokens = voices.GetTokensPath(voiceId);
        config.Model.Vits.DataDir = voices.GetEspeakDataDir(voiceId);
        // Piper defaults — produce natural-sounding speech.
        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        config.MaxNumSentences = 2;

        _tts = new OfflineTts(config);
        _logger.Info($"Piper voice '{_voiceId}' loaded (sample rate {_tts.SampleRate} Hz, {config.Model.NumThreads} threads)");
    }

    /// <summary>
    /// Synthesize <paramref name="text"/> to a WAV byte array.
    /// <paramref name="speed"/> &gt; 1 speeds up; &lt; 1 slows down.
    /// </summary>
    public async Task<byte[]> GenerateWavAsync(string text, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PiperTextToSpeechClient));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text must be non-empty", nameof(text));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Inference is CPU-bound — push it off the caller thread so
            // cancellation can race the synthesis.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var audio = _tts.Generate(text, speed: speed, speakerId: 0);
                cancellationToken.ThrowIfCancellationRequested();
                return ConvertFloatPcmToWav(audio.Samples, audio.SampleRate);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Convert Sherpa's 32-bit float PCM samples (range -1..1) to a
    /// standard 16-bit PCM mono WAV blob the WinUI MediaPlayer can play.
    /// </summary>
    private static byte[] ConvertFloatPcmToWav(float[] samples, int sampleRate)
    {
        const int bitsPerSample = 16;
        const int channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = samples.Length * sizeof(short);

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);
        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        // fmt chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);  // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        // 16-bit PCM: clamp + scale.
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            w.Write((short)(s * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _tts.Dispose(); } catch { /* swallow */ }
        try { _gate.Dispose(); } catch { /* swallow */ }
    }
}
