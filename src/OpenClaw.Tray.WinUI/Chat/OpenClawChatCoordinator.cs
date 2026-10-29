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
    private readonly Dictionary<VoiceService, int> _voiceCaptureMutes = new();
    private readonly GatewaySpeechPlayback _gatewaySpeech = new();
    private bool _gatewayVoiceEnabled;
    private string? _speechTarget;
    public GatewaySpeechPlaybackState SpeechState { get; private set; } = new(GatewaySpeechPhase.Idle);
    public event Action<GatewaySpeechPlaybackState>? SpeechStateChanged;
    private bool _disposed;

    /// <summary>
    /// When true, automatic response speech is suppressed. Manual Read Aloud bypasses mute.
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
            _gatewaySpeech.SetMuted(value);
            if (value)
            {
                PublishSpeechState(new(GatewaySpeechPhase.Idle));
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
        _gatewayVoiceEnabled = settings.UseGatewayVoice;
        _gatewaySpeech.Changed += PublishSpeechState;
        settings.Saved += OnSettingsSaved;
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (_gatewayVoiceEnabled == _settings.UseGatewayVoice) return;
        _gatewayVoiceEnabled = _settings.UseGatewayVoice;
        StopSpeaking();
    }

    private void PublishSpeechState(GatewaySpeechPlaybackState state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            SpeechState = state;
        }
        void Notify()
        {
            if (!_disposed && ReferenceEquals(SpeechState, state)) SpeechStateChanged?.Invoke(state);
        }
        if (_post is { } post) post(Notify); else Notify();
    }

    public void SetSpeechTarget(string? target)
    {
        _gatewaySpeech.SetTarget(target);
        if (string.Equals(_speechTarget, target, StringComparison.Ordinal)) return;
        _speechTarget = target;
        PublishSpeechState(new(GatewaySpeechPhase.Idle));
    }

    public Task PreviewGatewayVoiceAsync(string text, CancellationToken cancellationToken) =>
        SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: true,
            forceGateway: true, cancellationToken: cancellationToken);

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
        _gatewaySpeech.SetClient(client);
        PublishSpeechState(new(GatewaySpeechPhase.Idle));

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

        // Manual "play" button — bypass mute (mute is for auto-read only)
        return SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: true);
    }

    /// <summary>Stops any currently playing TTS audio immediately.</summary>
    public void StopGatewaySpeaking()
    {
        _gatewaySpeech.Stop();
        PublishSpeechState(new(GatewaySpeechPhase.Idle));
    }

    public void StopSpeaking()
    {
        StopGatewaySpeaking();
        try { (_nodeServiceAccessor()?.TextToSpeech ?? GetFallbackTextToSpeechService()).StopSpeaking(); }
        catch (Exception ex) { _logger.Debug($"OpenClawChatCoordinator.StopSpeaking failed: {ex.Message}"); }
    }

    public Task SpeakResponseAsync(string text, string? sessionKey = null) =>
        SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: false, responseTarget: sessionKey);

    private async Task SpeakConfiguredTextAsync(string text, bool muteVoiceCapture, bool bypassMute = false,
        bool forceGateway = false, string? responseTarget = null, CancellationToken cancellationToken = default)
    {
        if (!bypassMute && IsMuted) return;
        var useGatewayVoice = forceGateway || _settings.UseGatewayVoice;
        var voiceService = _nodeServiceAccessor()?.VoiceService;
        var mutedVoiceCapture = false;

        try
        {
            if (muteVoiceCapture && voiceService is not null)
            {
                lock (_gate)
                {
                    _voiceCaptureMutes.TryGetValue(voiceService, out var count);
                    _voiceCaptureMutes[voiceService] = count + 1;
                    voiceService.IsMutedForPlayback = true;
                    mutedVoiceCapture = true;
                }
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
            if (useGatewayVoice)
            {
                await _gatewaySpeech.SpeakAsync(speakText, manual: bypassMute,
                    ttsService.PlayGatewayAudioAsync, responseTarget, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ttsService.SpeakAsync(speakArgs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (GatewaySpeechException ex)
        {
            if (useGatewayVoice) PublishSpeechState(new(GatewaySpeechPhase.Failed, Failure: ex.Failure));
            _logger.Warn($"Gateway speech failed ({ex.Failure}).");
        }
        catch (Exception)
        {
            if (useGatewayVoice) PublishSpeechState(new(GatewaySpeechPhase.Failed, Failure: GatewaySpeechFailure.PlaybackFailed));
            _logger.Warn("TTS response playback failed.");
        }
        finally
        {
            if (mutedVoiceCapture && voiceService is not null)
            {
                await Task.Delay(300).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_voiceCaptureMutes.TryGetValue(voiceService, out var count) && count > 1)
                        _voiceCaptureMutes[voiceService] = count - 1;
                    else
                    {
                        _voiceCaptureMutes.Remove(voiceService);
                        voiceService.IsMutedForPlayback = false;
                    }
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

        _settings.Saved -= OnSettingsSaved;
        _gatewaySpeech.Dispose();
        provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fallbackTextToSpeech?.Dispose();
    }
}
