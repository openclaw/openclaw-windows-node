using System.Text.RegularExpressions;
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
                catch (Exception ex) { _logger.Debug($"OpenClawChatCoordinator: StopSpeaking during mute failed: {ex.Message}"); }
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

        // Manual "play" button - bypass mute (mute is for auto-read only)
        return SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: true);
    }

    /// <summary>Stops any currently playing TTS audio immediately.</summary>
    public void StopSpeaking()
    {
        try { (_nodeServiceAccessor()?.TextToSpeech ?? GetFallbackTextToSpeechService()).StopSpeaking(); }
        catch (Exception ex) { _logger.Debug($"OpenClawChatCoordinator.StopSpeaking failed: {ex.Message}"); }
    }

    public Task SpeakResponseAsync(string text) => SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: false);

    private async Task SpeakConfiguredTextAsync(string text, bool muteVoiceCapture, bool bypassMute = false)
    {
        if (!bypassMute && IsMuted) return;
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

            var speakText = SanitizeForSpeech(text);
            if (string.IsNullOrWhiteSpace(speakText)) return;
            var speakArgs = new TtsSpeakArgs
            {
                Text = speakText,
                // Leave provider unset so TextToSpeechService treats this as
                // configured/default playback and can fall back when needed.
                Provider = null,
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

    /// <summary>
    /// Strips markdown formatting, emojis, code blocks, and other non-speakable
    /// content so TTS output sounds natural.
    /// </summary>
    private static string SanitizeForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var s = text;

        // Remove fenced code blocks entirely (```...```)
        s = Regex.Replace(s, @"```[\s\S]*?```", " ");
        // Remove inline code (`...`)
        s = Regex.Replace(s, @"`[^`]+`", " ");
        // Remove markdown bold/italic markers (**, *, __, _)
        s = Regex.Replace(s, @"\*{1,2}(.+?)\*{1,2}", "$1");
        s = Regex.Replace(s, @"_{1,2}(.+?)_{1,2}", "$1");
        // Remove markdown headers (# ## ### etc.)
        s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        // Remove markdown links [text](url) → text
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Remove raw URLs
        s = Regex.Replace(s, @"https?://\S+", " ");
        // Remove bullet/list markers
        s = Regex.Replace(s, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        // Remove numbered list markers
        s = Regex.Replace(s, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
        // Remove emojis (supplementary plane: emoticons, symbols, etc.)
        s = Regex.Replace(s, @"[\u200B\uFE0F]", "");
        s = Regex.Replace(s, @"\p{Cs}{2}", " "); // surrogate pairs (emojis in supplementary planes)
        // Remove remaining special characters that sound odd when spoken
        s = Regex.Replace(s, @"[~|<>{}[\]\\*]", " ");
        // Collapse multiple spaces/newlines
        s = Regex.Replace(s, @"\s{2,}", " ");

        return s.Trim();
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
