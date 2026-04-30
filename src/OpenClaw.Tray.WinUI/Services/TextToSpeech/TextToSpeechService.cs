using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
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
    }

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
        if (!string.IsNullOrWhiteSpace(args.VoiceId))
        {
            var requestedVoice = args.VoiceId.Trim();
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
    }
}
