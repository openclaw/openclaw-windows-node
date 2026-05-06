using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace OpenClawTray.Services;

public sealed class TextToSpeechService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly SettingsManager _settings;
    private readonly ElevenLabsTextToSpeechClient _elevenLabsClient;
    private readonly PiperVoiceManager _piperVoices;
    private readonly object _piperLock = new();
    private PiperTextToSpeechClient? _piperClient;  // lazily loaded; reused across calls for the same voice
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private readonly object _activeLock = new();
    private MediaPlayer? _activePlayer;
    private TaskCompletionSource<bool>? _activeCompletion;

    public TextToSpeechService(IOpenClawLogger logger, SettingsManager settings)
        : this(logger, settings, new ElevenLabsTextToSpeechClient())
    {
    }

    internal TextToSpeechService(
        IOpenClawLogger logger,
        SettingsManager settings,
        ElevenLabsTextToSpeechClient elevenLabsClient)
    {
        _logger = logger;
        _settings = settings;
        _elevenLabsClient = elevenLabsClient;
        // Piper voices live under the same data directory as Whisper models
        // so the user has a single "AI assets" folder to point at.
        _piperVoices = new PiperVoiceManager(SettingsManager.SettingsDirectoryPath, logger);
    }

    /// <summary>Exposed so Settings UI can drive download/delete from the same instance.</summary>
    public PiperVoiceManager PiperVoices => _piperVoices;

    public async Task<TtsSpeakResult> SpeakAsync(TtsSpeakArgs args, CancellationToken cancellationToken = default)
    {
        var provider = TtsCapability.ResolveProvider(args.Provider, _settings.TtsProvider);
        var stopwatch = Stopwatch.StartNew();

        if (string.Equals(provider, TtsCapability.WindowsProvider, StringComparison.OrdinalIgnoreCase))
        {
            await SpeakWithWindowsAsync(args, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(provider, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase))
        {
            await SpeakWithElevenLabsAsync(args, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(provider, TtsCapability.PiperProvider, StringComparison.OrdinalIgnoreCase))
        {
            await SpeakWithPiperAsync(args, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported TTS provider '{provider}'.");
        }

        stopwatch.Stop();
        return new TtsSpeakResult
        {
            Provider = provider,
            ContentType = string.Equals(provider, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase)
                ? "audio/mpeg"
                : "audio/wav",
            DurationMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue)
        };
    }

    private async Task SpeakWithWindowsAsync(TtsSpeakArgs args, CancellationToken cancellationToken)
    {
        using var synthesizer = new SpeechSynthesizer();
        var requestedVoice = string.IsNullOrWhiteSpace(args.VoiceId)
            ? _settings.TtsWindowsVoiceId
            : args.VoiceId;
        if (!string.IsNullOrWhiteSpace(requestedVoice))
        {
            requestedVoice = requestedVoice.Trim();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                string.Equals(v.Id, requestedVoice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.DisplayName, requestedVoice, StringComparison.OrdinalIgnoreCase));
            if (voice == null)
                throw new InvalidOperationException($"Windows TTS voice '{requestedVoice}' was not found.");

            synthesizer.Voice = voice;
        }

        using var stream = await synthesizer
            .SynthesizeTextToStreamAsync(args.Text)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        await PlayStreamAsync(stream, stream.ContentType, args.Interrupt, cancellationToken).ConfigureAwait(false);
    }

    private async Task SpeakWithElevenLabsAsync(TtsSpeakArgs args, CancellationToken cancellationToken)
    {
        var apiKey = _settings.TtsElevenLabsApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ElevenLabs API key is required in Settings.");

        var voiceId = string.IsNullOrWhiteSpace(args.VoiceId)
            ? _settings.TtsElevenLabsVoiceId
            : args.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException("ElevenLabs voice ID is required in Settings or the tts.speak voiceId argument.");

        var model = string.IsNullOrWhiteSpace(args.Model)
            ? _settings.TtsElevenLabsModel
            : args.Model;

        var audio = await _elevenLabsClient.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = apiKey,
            VoiceId = voiceId,
            Text = args.Text,
            ModelId = model
        }, cancellationToken).ConfigureAwait(false);

        using var stream = await CreateStreamAsync(audio.AudioBytes, cancellationToken).ConfigureAwait(false);
        await PlayStreamAsync(stream, audio.ContentType, args.Interrupt, cancellationToken).ConfigureAwait(false);
    }

    private async Task SpeakWithPiperAsync(TtsSpeakArgs args, CancellationToken cancellationToken)
    {
        var voiceId = string.IsNullOrWhiteSpace(args.VoiceId)
            ? _settings.TtsPiperVoiceId
            : args.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException("Piper voice ID is required in Settings or the tts.speak voiceId argument.");

        if (!_piperVoices.IsVoiceDownloaded(voiceId))
        {
            // Privacy: don't echo the voiceId — it's user-controlled. The
            // SttCapability sanitization layer wraps "Speak failed" anyway,
            // but we also keep this throw site free of caller args.
            throw new InvalidOperationException("Piper voice not downloaded. Open Voice Settings to download it.");
        }

        var client = AcquirePiperClient(voiceId);
        var wavBytes = await client.GenerateWavAsync(args.Text, speed: 1.0f, cancellationToken).ConfigureAwait(false);
        using var stream = await CreateStreamAsync(wavBytes, cancellationToken).ConfigureAwait(false);
        await PlayStreamAsync(stream, "audio/wav", args.Interrupt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reuse the Piper client across calls for the same voice id (model
    /// load is the expensive part, ~200-500 ms). Switch atomically when
    /// the requested voice changes.
    /// </summary>
    private PiperTextToSpeechClient AcquirePiperClient(string voiceId)
    {
        lock (_piperLock)
        {
            if (_piperClient != null && string.Equals(_piperClient.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase))
                return _piperClient;

            // Voice changed (or first call) — dispose the old client before
            // loading the new model so we don't double the memory footprint.
            try { _piperClient?.Dispose(); } catch { /* swallow */ }
            _piperClient = new PiperTextToSpeechClient(_logger, _piperVoices, voiceId);
            return _piperClient;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreateStreamAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    private async Task PlayStreamAsync(
        IRandomAccessStream stream,
        string contentType,
        bool interrupt,
        CancellationToken cancellationToken)
    {
        if (interrupt)
            InterruptActivePlayback();

        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        MediaPlayer? player = null;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            player = new MediaPlayer();
            player.MediaEnded += (_, _) => completion.TrySetResult(true);
            player.MediaFailed += (_, e) =>
                completion.TrySetException(new InvalidOperationException($"TTS playback failed: {e.ErrorMessage}"));
            player.Source = MediaSource.CreateFromStream(stream, contentType);

            lock (_activeLock)
            {
                _activePlayer = player;
                _activeCompletion = completion;
            }

            player.Play();

            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                completion);
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_activeLock)
            {
                if (ReferenceEquals(_activePlayer, player))
                {
                    _activePlayer = null;
                    _activeCompletion = null;
                }
            }

            if (player != null)
            {
                player.Pause();
                player.Source = null;
                player.Dispose();
            }

            _playbackGate.Release();
        }
    }

    private void InterruptActivePlayback()
    {
        TaskCompletionSource<bool>? completion;
        lock (_activeLock)
        {
            completion = _activeCompletion;
        }

        if (completion != null)
        {
            _logger.Info("Interrupting active TTS playback");
            completion.TrySetException(new InvalidOperationException("TTS playback was interrupted."));
        }
    }

    public void Dispose()
    {
        InterruptActivePlayback();
        // Playback may still release the gate after an interrupt during shutdown.
        _elevenLabsClient.Dispose();
        lock (_piperLock)
        {
            try { _piperClient?.Dispose(); } catch { /* swallow */ }
            _piperClient = null;
        }
    }
}
