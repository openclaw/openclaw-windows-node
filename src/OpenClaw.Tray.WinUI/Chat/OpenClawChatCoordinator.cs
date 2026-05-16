using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Owns native chat provider lifecycle and chat-specific speech playback.
/// </summary>
public sealed class OpenClawChatCoordinator : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly Func<NodeService?> _nodeServiceAccessor;
    private readonly IOpenClawLogger _logger;
    private readonly Action<Action>? _post;
    private readonly object _gate = new();
    private readonly object _manualSpeechGate = new();
    private OpenClawChatDataProvider? _provider;
    private TextToSpeechService? _fallbackTextToSpeech;
    private string? _lastManualSpeechText;
    private DateTimeOffset _lastManualSpeechAt;
    private int _ttsMuteCount;
    private bool _disposed;

    /// <summary>
    /// When true, all TTS playback (manual Read Aloud and auto-response speech) is suppressed.
    /// Toggled by the speaker mute button in the chat composer.
    /// Setting to true also interrupts any currently playing speech.
    /// </summary>
    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (value)
            {
                // Stop any currently playing speech immediately
                try { (_nodeServiceAccessor()?.TextToSpeech ?? GetFallbackTextToSpeechService()).StopSpeaking(); }
                catch { /* best effort */ }
            }
        }
    }

    public OpenClawChatCoordinator(
        SettingsManager settings,
        Func<NodeService?> nodeServiceAccessor,
        IOpenClawLogger logger,
        Action<Action>? post)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _nodeServiceAccessor = nodeServiceAccessor ?? throw new ArgumentNullException(nameof(nodeServiceAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _post = post;
    }

    public OpenClawChatDataProvider? Provider
    {
        get
        {
            lock (_gate)
            {
                return _provider;
            }
        }
    }

    public void SetOperatorClient(OpenClawGatewayClient? client)
    {
        OpenClawChatDataProvider? oldProvider;

        lock (_gate)
        {
            if (_disposed) return;
            oldProvider = _provider;
            _provider = null;
        }

        oldProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (client is null)
        {
            return;
        }

        var newProvider = new OpenClawChatDataProvider(new GatewayClientChatBridge(client), _post);
        lock (_gate)
        {
            if (_disposed)
            {
                newProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }

            _provider = newProvider;
        }
    }

    public Task SpeakChatTextAsync(string text)
    {
        if (ShouldSuppressDuplicateManualSpeech(text))
        {
            return Task.CompletedTask;
        }

        return SpeakConfiguredTextAsync(text, muteVoiceCapture: true);
    }

    public Task SpeakResponseAsync(string text) => SpeakConfiguredTextAsync(text, muteVoiceCapture: true);

    private async Task SpeakConfiguredTextAsync(string text, bool muteVoiceCapture)
    {
        if (IsMuted) return;
        var voiceService = _nodeServiceAccessor()?.VoiceService;
        var mutedVoiceCapture = false;

        try
        {
            if (muteVoiceCapture && voiceService is not null)
            {
                Interlocked.Increment(ref _ttsMuteCount);
                mutedVoiceCapture = true;
                voiceService.IsMutedForPlayback = true;
            }

            var speakText = text.Length > 500 ? text[..500] + "..." : text;
            var speakArgs = new TtsSpeakArgs
            {
                Text = speakText,
                Provider = _settings.TtsProvider ?? TtsCapability.PiperProvider,
                Interrupt = true
            };

            var ttsService = _nodeServiceAccessor()?.TextToSpeech
                ?? GetFallbackTextToSpeechService();
            await ttsService.SpeakAsync(speakArgs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"TTS response playback failed: {ex.Message}");
        }
        finally
        {
            if (mutedVoiceCapture && voiceService is not null)
            {
                await Task.Delay(300).ConfigureAwait(false);
                if (Interlocked.Decrement(ref _ttsMuteCount) <= 0)
                {
                    voiceService.IsMutedForPlayback = false;
                }
            }
        }
    }

    private TextToSpeechService GetFallbackTextToSpeechService()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fallbackTextToSpeech ??= new TextToSpeechService(_logger, _settings);
        }
    }

    private bool ShouldSuppressDuplicateManualSpeech(string text)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_manualSpeechGate)
        {
            if (string.Equals(_lastManualSpeechText, text, StringComparison.Ordinal)
                && now - _lastManualSpeechAt < TimeSpan.FromSeconds(1))
            {
                return true;
            }

            _lastManualSpeechText = text;
            _lastManualSpeechAt = now;
            return false;
        }
    }

    public void Dispose()
    {
        OpenClawChatDataProvider? provider;
        TextToSpeechService? fallbackTextToSpeech;

        lock (_gate)
        {
            provider = _provider;
            fallbackTextToSpeech = _fallbackTextToSpeech;
            _provider = null;
            _fallbackTextToSpeech = null;
            _disposed = true;
        }

        provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fallbackTextToSpeech?.Dispose();
    }
}
