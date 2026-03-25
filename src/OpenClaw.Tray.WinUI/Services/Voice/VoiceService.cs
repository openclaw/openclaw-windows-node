using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace OpenClawTray.Services.Voice;

public sealed class VoiceService : IVoiceRuntime, IVoiceConfigurationApi, IVoiceRuntimeControlApi, IDisposable
{
    private const string DefaultSessionKey = "main";
    private const int HResultSpeechPrivacyDeclined = unchecked((int)0x80045509);
    private static readonly TimeSpan TransportConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LateReplyGraceWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialRecognitionReadyDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecognitionHealthCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DuplicateTranscriptWindow = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RecognitionResumeRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan QueuedReplyPlaybackGap = TimeSpan.FromMilliseconds(500);

    private readonly IOpenClawLogger _logger;
    private readonly SettingsManager _settings;
    private readonly VoiceCloudTextToSpeechClient _cloudTextToSpeechClient;
    private readonly object _gate = new();

    private VoiceStatusInfo _status;
    private VoiceActivationMode? _runtimeModeOverride;
    private CancellationTokenSource? _runtimeCts;
    private OpenClawGatewayClient? _chatClient;
    private ConnectionStatus _chatTransportStatus = ConnectionStatus.Disconnected;
    private TaskCompletionSource<bool>? _transportReadyTcs;
    private SpeechRecognizer? _speechRecognizer;
    private SpeechSynthesizer? _speechSynthesizer;
    private MediaPlayer? _mediaPlayer;
    private bool _recognitionActive;
    private int _recognitionSessionGeneration;
    private bool _recognitionHealthCheckArmed;
    private bool _awaitingReply;
    private bool _isSpeaking;
    private bool _replyPlaybackLoopActive;
    private bool _quickPaused;
    private string? _lastTranscript;
    private DateTime _lastTranscriptUtc;
    private readonly Queue<(string Text, string? SessionKey)> _pendingAssistantReplies = new();
    private CancellationTokenSource? _playbackSkipCts;
    private string? _currentReplyPreview;
    private string? _lateReplySessionKey;
    private DateTime? _lateReplyGraceUntilUtc;
    private bool _disposed;

    public event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;
    public event EventHandler<VoiceTranscriptDraftEventArgs>? TranscriptDraftUpdated;

    public VoiceService(IOpenClawLogger logger, SettingsManager settings)
    {
        _logger = logger;
        _settings = settings;
        _cloudTextToSpeechClient = new VoiceCloudTextToSpeechClient();
        _status = new VoiceStatusInfo();
        _status = BuildStoppedStatus(null, null);
    }

    public VoiceStatusInfo CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return Clone(_status);
            }
        }
    }

    public Task<VoiceSettings> GetSettingsAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(Clone(_settings.Voice));
        }
    }

    public Task<VoiceSettings> UpdateSettingsAsync(VoiceSettingsUpdateArgs update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            _settings.Voice = Clone(update.Settings);
            if (update.Persist)
            {
                _settings.Save();
            }

            if (! _settings.Voice.Enabled || _settings.Voice.Mode == VoiceActivationMode.Off)
            {
                _quickPaused = false;
                _status = BuildStoppedStatus(_status.SessionKey, _status.LastError);
            }
            else if (_quickPaused || _status.State == VoiceRuntimeState.Paused)
            {
                _status = BuildPausedStatus(
                    _runtimeModeOverride ?? _settings.Voice.Mode,
                    _status.SessionKey,
                    _status.LastError);
            }
            else if (_status.Running)
            {
                _status = BuildRunningStatus(
                    _runtimeModeOverride ?? _settings.Voice.Mode,
                    _status.SessionKey,
                    _status.State,
                    _status.LastError);
            }
            else
            {
                _status = BuildStoppedStatus(_status.SessionKey, _status.LastError);
            }

            return Task.FromResult(Clone(_settings.Voice));
        }
    }

    public VoiceProviderConfigurationStore GetProviderConfiguration()
    {
        lock (_gate)
        {
            return _settings.VoiceProviderConfiguration.Clone();
        }
    }

    public void SetProviderConfiguration(VoiceProviderConfigurationStore configurationStore)
    {
        ArgumentNullException.ThrowIfNull(configurationStore);

        lock (_gate)
        {
            _settings.VoiceProviderConfiguration = configurationStore.Clone();
        }
    }

    public Task<VoiceStatusInfo> GetStatusAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(Clone(_status));
        }
    }

    public async Task<VoiceStatusInfo> ToggleQuickPauseAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        VoiceActivationMode mode;
        string? sessionKey;
        bool shouldResume;

        lock (_gate)
        {
            mode = _runtimeModeOverride ?? _settings.Voice.Mode;
            sessionKey = _status.SessionKey;

            if (!_settings.Voice.Enabled || mode == VoiceActivationMode.Off)
            {
                _quickPaused = false;
                _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                return Clone(_status);
            }

            shouldResume = _quickPaused || _status.State == VoiceRuntimeState.Paused;
            if (!shouldResume)
            {
                _quickPaused = true;
            }
        }

        if (shouldResume)
        {
            lock (_gate)
            {
                _quickPaused = false;
            }

            var resumed = await StartAsync(new VoiceStartArgs
            {
                Mode = mode,
                SessionKey = sessionKey
            });
            _logger.Info($"Voice runtime resumed via quick toggle ({mode})");
            return resumed;
        }

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        lock (_gate)
        {
            _status = BuildPausedStatus(mode, sessionKey, null);
            _logger.Info($"Voice runtime paused via quick toggle ({mode})");
            return Clone(_status);
        }
    }

    public async Task<VoiceStatusInfo> StartAsync(VoiceStartArgs args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        args ??= new VoiceStartArgs();

        VoiceSettings effectiveSettings;
        VoiceActivationMode requestedMode;
        string? sessionKey;

        lock (_gate)
        {
            effectiveSettings = Clone(_settings.Voice);
            requestedMode = args.Mode ?? effectiveSettings.Mode;
            sessionKey = args.SessionKey ?? _status.SessionKey;

            if (args.Mode.HasValue && args.Mode.Value != VoiceActivationMode.Off)
            {
                effectiveSettings.Enabled = true;
                effectiveSettings.Mode = args.Mode.Value;
                _runtimeModeOverride = args.Mode.Value;
            }
            else if (args.Mode == VoiceActivationMode.Off)
            {
                _runtimeModeOverride = null;
            }

            if (!effectiveSettings.Enabled || requestedMode == VoiceActivationMode.Off)
            {
                _quickPaused = false;
                _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                return Clone(_status);
            }

            if (_quickPaused)
            {
                _status = BuildPausedStatus(requestedMode, sessionKey, _status.LastError);
                return Clone(_status);
            }
        }

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        try
        {
            switch (requestedMode)
            {
                case VoiceActivationMode.TalkMode:
                    await StartTalkModeRuntimeAsync(effectiveSettings, sessionKey);
                    break;
                case VoiceActivationMode.VoiceWake:
                    lock (_gate)
                    {
                        _status = BuildRunningStatus(
                            VoiceActivationMode.VoiceWake,
                            sessionKey,
                            VoiceRuntimeState.ListeningForVoiceWake,
                            "Voice Wake capture is not implemented yet");
                    }
                    _logger.Info("Voice runtime started in mode VoiceWake");
                    break;
                default:
                    lock (_gate)
                    {
                        _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Voice runtime start failed", ex);
            lock (_gate)
            {
                _status = BuildErrorStatus(requestedMode, sessionKey, GetUserFacingErrorMessage(ex));
            }
        }

        return CurrentStatus;
    }

    public async Task<VoiceStatusInfo> StopAsync(VoiceStopArgs args)
    {
        args ??= new VoiceStopArgs();

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        lock (_gate)
        {
            _quickPaused = false;
            _runtimeModeOverride = null;
            _status = BuildStoppedStatus(_status.SessionKey, args.Reason);
            _logger.Info($"Voice runtime stopped{(string.IsNullOrWhiteSpace(args.Reason) ? string.Empty : $": {args.Reason}")}");
            return Clone(_status);
        }
    }

    public async Task<VoiceAudioDeviceInfo[]> ListDevicesAsync()
    {
        try
        {
            var inputDefaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            var outputDefaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            var results = new List<VoiceAudioDeviceInfo>();

            var inputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            foreach (var device in inputDevices)
            {
                results.Add(new VoiceAudioDeviceInfo
                {
                    DeviceId = device.Id,
                    Name = device.Name,
                    IsDefault = string.Equals(device.Id, inputDefaultId, StringComparison.Ordinal),
                    IsInput = true
                });
            }

            var outputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
            foreach (var device in outputDevices)
            {
                results.Add(new VoiceAudioDeviceInfo
                {
                    DeviceId = device.Id,
                    Name = device.Name,
                    IsDefault = string.Equals(device.Id, outputDefaultId, StringComparison.Ordinal),
                    IsOutput = true
                });
            }

            return results
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.IsInput ? 0 : 1)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice device enumeration failed: {ex.Message}");
            return
            [
                new VoiceAudioDeviceInfo
                {
                    DeviceId = "default-input",
                    Name = "System default microphone",
                    IsDefault = true,
                    IsInput = true
                },
                new VoiceAudioDeviceInfo
                {
                    DeviceId = "default-output",
                    Name = "System default speaker",
                    IsDefault = true,
                    IsOutput = true
                }
            ];
        }
    }

    public VoiceProviderCatalog GetProviderCatalog()
    {
        return VoiceProviderCatalogService.LoadCatalog(_logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Task.Run(() => StopRuntimeResourcesAsync(updateStoppedStatus: true)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice runtime dispose cleanup failed: {ex.Message}");
        }
    }

    public async Task<VoiceStatusInfo> PauseAsync(VoicePauseArgs? args = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        args ??= new VoicePauseArgs();

        VoiceActivationMode mode;
        string? sessionKey;

        lock (_gate)
        {
            mode = _runtimeModeOverride ?? _settings.Voice.Mode;
            sessionKey = _status.SessionKey;

            if (!_settings.Voice.Enabled || mode == VoiceActivationMode.Off)
            {
                _quickPaused = false;
                _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                return Clone(_status);
            }

            if (_quickPaused || _status.State == VoiceRuntimeState.Paused)
            {
                return Clone(_status);
            }

            _quickPaused = true;
        }

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        lock (_gate)
        {
            _status = BuildPausedStatus(mode, sessionKey, args.Reason);
            _logger.Info($"Voice runtime paused{(string.IsNullOrWhiteSpace(args.Reason) ? string.Empty : $": {args.Reason}")}");
            return Clone(_status);
        }
    }

    public async Task<VoiceStatusInfo> ResumeAsync(VoiceResumeArgs? args = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        args ??= new VoiceResumeArgs();

        VoiceActivationMode mode;
        string? sessionKey;

        lock (_gate)
        {
            mode = _runtimeModeOverride ?? _settings.Voice.Mode;
            sessionKey = _status.SessionKey;
            _quickPaused = false;
        }

        var resumed = await StartAsync(new VoiceStartArgs
        {
            Mode = mode,
            SessionKey = sessionKey
        });

        _logger.Info($"Voice runtime resumed{(string.IsNullOrWhiteSpace(args.Reason) ? string.Empty : $": {args.Reason}")}");
        return resumed;
    }

    public async Task<VoiceStatusInfo> SkipCurrentReplyAsync(VoiceSkipArgs? args = null)
    {
        args ??= new VoiceSkipArgs();

        CancellationTokenSource? playbackSkipCts;

        lock (_gate)
        {
            playbackSkipCts = _playbackSkipCts;
            if (playbackSkipCts == null && _pendingAssistantReplies.Count == 0)
            {
                return Clone(_status);
            }
        }

        playbackSkipCts?.Cancel();

        await Task.Yield();

        lock (_gate)
        {
            _logger.Info($"Voice reply skipped{(string.IsNullOrWhiteSpace(args.Reason) ? string.Empty : $": {args.Reason}")}");
            return Clone(_status);
        }
    }

    private async Task StartTalkModeRuntimeAsync(VoiceSettings settings, string? sessionKey)
    {
        var effectiveSessionKey = string.IsNullOrWhiteSpace(sessionKey) ? DefaultSessionKey : sessionKey;
        var selectedSpeechToText = VoiceProviderCatalogService.ResolveSpeechToTextProvider(
            settings.SpeechToTextProviderId,
            _logger);
        var selectedTextToSpeech = VoiceProviderCatalogService.ResolveTextToSpeechProvider(
            settings.TextToSpeechProviderId,
            _logger);
        var fallbackMessage = BuildProviderFallbackMessage(selectedSpeechToText, selectedTextToSpeech);

        await EnsureMicrophoneConsentAsync();

        CancellationTokenSource? runtimeCts = null;
        SpeechRecognizer? recognizer = null;
        SpeechSynthesizer? synthesizer = null;
        MediaPlayer? player = null;

        try
        {
            runtimeCts = new CancellationTokenSource();
            recognizer = await CreateSpeechRecognizerAsync(settings);
            synthesizer = new SpeechSynthesizer();
            player = new MediaPlayer();

            if (!string.IsNullOrWhiteSpace(settings.InputDeviceId))
            {
                _logger.Warn("Selected input device is saved, but Talk Mode currently uses the system speech input device.");
            }

            if (!string.IsNullOrWhiteSpace(settings.OutputDeviceId))
            {
                _logger.Warn("Selected output device is saved, but Talk Mode currently uses the default speech output device.");
            }

            recognizer.HypothesisGenerated += OnSpeechHypothesisGenerated;
            recognizer.ContinuousRecognitionSession.ResultGenerated += OnSpeechResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed += OnSpeechRecognitionCompleted;

            lock (_gate)
            {
                _runtimeCts = runtimeCts;
                _speechRecognizer = recognizer;
                _speechSynthesizer = synthesizer;
                _mediaPlayer = player;
                _status = BuildRunningStatus(
                    VoiceActivationMode.TalkMode,
                    effectiveSessionKey,
                    VoiceRuntimeState.Arming,
                    fallbackMessage);
            }

            await EnsureChatTransportAsync(runtimeCts.Token);
            await StartRecognitionSessionAsync(updateListeningStatus: false);
            ArmRecognitionHealthCheck();
            await Task.Delay(InitialRecognitionReadyDelay, runtimeCts.Token);

            lock (_gate)
            {
                if (_status.Running)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        effectiveSessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        fallbackMessage);
                }
            }

            _logger.Info($"Speech recognition warm-up completed ({InitialRecognitionReadyDelay.TotalMilliseconds:0}ms)");
            _logger.Info("Voice runtime started in mode TalkMode");
        }
        catch
        {
            var cleanupStoredState = false;
            lock (_gate)
            {
                cleanupStoredState = ReferenceEquals(_runtimeCts, runtimeCts);
            }

            if (cleanupStoredState)
            {
                await StopRuntimeResourcesAsync(updateStoppedStatus: false);
            }
            else
            {
                if (recognizer != null)
                {
                    try { recognizer.HypothesisGenerated -= OnSpeechHypothesisGenerated; } catch { }
                    try { recognizer.ContinuousRecognitionSession.ResultGenerated -= OnSpeechResultGenerated; } catch { }
                    try { recognizer.ContinuousRecognitionSession.Completed -= OnSpeechRecognitionCompleted; } catch { }
                    try { recognizer.Dispose(); } catch { }
                }

                try { player?.Dispose(); } catch { }
                try { synthesizer?.Dispose(); } catch { }
                try { runtimeCts?.Dispose(); } catch { }
            }

            throw;
        }
    }

    private async Task<SpeechRecognizer> CreateSpeechRecognizerAsync(VoiceSettings settings)
    {
        var recognizer = new SpeechRecognizer();
        recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(settings.TalkMode.EndSilenceMs);
        recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(10);
        recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(4);
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "always-on-dictation"));

        var compilation = await recognizer.CompileConstraintsAsync();
        if (compilation.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            throw new InvalidOperationException($"Speech recognizer unavailable: {compilation.Status}");
        }

        _logger.Info($"Speech recognizer compiled successfully ({compilation.Status})");

        return recognizer;
    }

    private async Task EnsureMicrophoneConsentAsync()
    {
        if (!PackageHelper.IsPackaged)
        {
            return;
        }

        using var capture = new MediaCapture();
        var initSettings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        };

        await capture.InitializeAsync(initSettings);
    }

    private async Task EnsureChatTransportAsync(CancellationToken cancellationToken)
    {
        OpenClawGatewayClient? existingClient;
        TaskCompletionSource<bool> readySource;
        bool shouldStartConnection;

        lock (_gate)
        {
            existingClient = _chatClient;
            if (_chatTransportStatus == ConnectionStatus.Connected)
            {
                return;
            }

            readySource = GetOrCreateTransportReadySource(
                _chatTransportStatus,
                _transportReadyTcs,
                out shouldStartConnection);
            _transportReadyTcs = readySource;

            if (shouldStartConnection)
            {
                _chatTransportStatus = ConnectionStatus.Connecting;

                if (existingClient == null)
                {
                    _chatClient = new OpenClawGatewayClient(_settings.GatewayUrl, _settings.Token, _logger);
                    _chatClient.StatusChanged += OnChatTransportStatusChanged;
                    _chatClient.ChatMessageReceived += OnChatMessageReceived;
                    existingClient = _chatClient;
                }
            }
        }

        if (shouldStartConnection)
        {
            await existingClient!.ConnectAsync();
        }

        var readyTask = readySource.Task;
        var timeoutTask = Task.Delay(TransportConnectTimeout, cancellationToken);
        var completed = await Task.WhenAny(readyTask, timeoutTask);
        if (completed != readyTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out connecting voice chat transport.");
        }

        await readyTask;
    }

    private static TaskCompletionSource<bool> GetOrCreateTransportReadySource(
        ConnectionStatus transportStatus,
        TaskCompletionSource<bool>? existingReadySource,
        out bool shouldStartConnection)
    {
        if (transportStatus == ConnectionStatus.Connecting && existingReadySource != null)
        {
            shouldStartConnection = false;
            return existingReadySource;
        }

        shouldStartConnection = true;
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task StartRecognitionSessionAsync(bool updateListeningStatus = true)
    {
        SpeechRecognizer? recognizer;
        CancellationToken runtimeToken;
        int generation;

        lock (_gate)
        {
            recognizer = _speechRecognizer;
            if (recognizer == null || _recognitionActive || _runtimeCts == null)
            {
                return;
            }

            runtimeToken = _runtimeCts.Token;
            generation = ++_recognitionSessionGeneration;
        }

        _logger.Info("Starting speech recognition session");
        await recognizer.ContinuousRecognitionSession.StartAsync();

        lock (_gate)
        {
            _recognitionActive = true;
            if (updateListeningStatus && _status.Running && !_awaitingReply && !_isSpeaking)
            {
                _status = BuildRunningStatus(
                    VoiceActivationMode.TalkMode,
                    _status.SessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    null);
            }
        }

        _logger.Info("Speech recognition session started");
        _ = MonitorRecognitionSessionHealthAsync(generation, runtimeToken);
    }

    private async Task ResumeRecognitionSessionAsync(
        CancellationToken cancellationToken,
        string reason,
        string? lastError = null)
    {
        const int maxAttempts = 2;
        string? currentError = lastError;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await StartRecognitionSessionAsync();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                currentError = GetUserFacingErrorMessage(ex);
                _logger.Warn(
                    $"Voice recognition resume failed ({reason}, attempt {attempt}/{maxAttempts}): {ex.Message}");

                lock (_gate)
                {
                    if (_runtimeCts == null ||
                        !_status.Running ||
                        _status.Mode != VoiceActivationMode.TalkMode ||
                        _awaitingReply ||
                        _isSpeaking)
                    {
                        return;
                    }

                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        currentError);
                }

                if (attempt == maxAttempts)
                {
                    return;
                }

                await Task.Delay(RecognitionResumeRetryDelay, cancellationToken);
            }
        }
    }

    private async Task StopRecognitionSessionAsync()
    {
        SpeechRecognizer? recognizer;

        lock (_gate)
        {
            recognizer = _speechRecognizer;
            if (recognizer == null || !_recognitionActive)
            {
                return;
            }

            _recognitionActive = false;
        }

        try
        {
            await recognizer.ContinuousRecognitionSession.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice recognition stop failed: {ex.Message}");
        }
    }

    private async void OnSpeechResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        try
        {
            var result = args.Result;
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (result.Status != SpeechRecognitionResultStatus.Success ||
                result.Confidence == SpeechRecognitionConfidence.Rejected ||
                result.Confidence == SpeechRecognitionConfidence.Low)
            {
                _logger.Info($"Voice recognition ignored result with confidence {result.Confidence}: {text}");
                return;
            }

            _logger.Info($"Voice recognition result ({result.Confidence}): {text}");
            await HandleRecognizedTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.Error("Voice recognition handler failed", ex);
            CancellationToken cancellationToken;
            var shouldResume = false;
            var userMessage = GetUserFacingErrorMessage(ex);
            lock (_gate)
            {
                if (_runtimeCts != null &&
                    _status.Running &&
                    _status.Mode == VoiceActivationMode.TalkMode)
                {
                    cancellationToken = _runtimeCts.Token;
                    _awaitingReply = false;
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        userMessage);
                    shouldResume = true;
                }
                else
                {
                    return;
                }
            }

            if (shouldResume)
            {
                try
                {
                    await ResumeRecognitionSessionAsync(cancellationToken, "result handler failure", userMessage);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private void OnSpeechHypothesisGenerated(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        string? sessionKey = null;
        string? text = null;

        lock (_gate)
        {
            if (_runtimeCts == null ||
                _status.Mode != VoiceActivationMode.TalkMode ||
                !_status.Running ||
                _awaitingReply ||
                _isSpeaking)
            {
                return;
            }

            text = args.Hypothesis?.Text?.Trim();
            sessionKey = GetCurrentVoiceSessionKey();
            _recognitionHealthCheckArmed = false;
            if (_status.State != VoiceRuntimeState.RecordingUtterance)
            {
                _status = BuildRunningStatus(
                    VoiceActivationMode.TalkMode,
                    _status.SessionKey,
                    VoiceRuntimeState.RecordingUtterance,
                    _status.LastError);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        RaiseTranscriptDraft(text, sessionKey, clear: false);
    }

    private async Task HandleRecognizedTextAsync(string text)
    {
        CancellationToken cancellationToken;
        string sessionKey;
        var pipelineStopwatch = Stopwatch.StartNew();
        long recognitionStopElapsedMs = 0;
        long transportReadyElapsedMs = 0;
        long directSendElapsedMs = 0;

        lock (_gate)
        {
            if (_runtimeCts == null || _status.Mode != VoiceActivationMode.TalkMode || !_status.Running)
            {
                return;
            }

            if (_awaitingReply || _isSpeaking)
            {
                return;
            }

            if (string.Equals(text, _lastTranscript, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _lastTranscriptUtc < DuplicateTranscriptWindow)
            {
                _logger.Info($"Voice recognition suppressed duplicate transcript: {text}");
                return;
            }

            _lastTranscript = text;
            _lastTranscriptUtc = DateTime.UtcNow;
            _recognitionHealthCheckArmed = false;
            cancellationToken = _runtimeCts.Token;
            sessionKey = GetCurrentVoiceSessionKey();
        }

        RaiseTranscriptDraft(text, sessionKey, clear: false);

        await StopRecognitionSessionAsync();
        recognitionStopElapsedMs = pipelineStopwatch.ElapsedMilliseconds;

        try
        {
            await EnsureChatTransportAsync(cancellationToken);
            transportReadyElapsedMs = pipelineStopwatch.ElapsedMilliseconds - recognitionStopElapsedMs;

            OpenClawGatewayClient? client;
            lock (_gate)
            {
                client = _chatClient;
            }

            if (client == null)
            {
                throw new InvalidOperationException("Voice chat transport is unavailable.");
            }

            _logger.Info($"Voice transcript captured: {text}");
            var directSendStopwatch = Stopwatch.StartNew();
            await client.SendChatMessageAsync(text, sessionKey);
            directSendElapsedMs = directSendStopwatch.ElapsedMilliseconds;
            _logger.Info($"Voice direct send path: elapsed={directSendElapsedMs}ms");

            _logger.Info(
                $"Voice pre-response latency: recognitionStop={recognitionStopElapsedMs}ms transportReady={transportReadyElapsedMs}ms directSend={directSendElapsedMs}ms total={pipelineStopwatch.ElapsedMilliseconds}ms");
            lock (_gate)
            {
                _awaitingReply = true;
                _lateReplySessionKey = null;
                _lateReplyGraceUntilUtc = null;
                _status = BuildRunningStatus(
                    VoiceActivationMode.TalkMode,
                    _status.SessionKey,
                    VoiceRuntimeState.AwaitingResponse,
                    _status.LastError);
                _status.LastUtteranceUtc = DateTime.UtcNow;
            }

            _logger.Info("Voice response wait started");
            RaiseConversationTurn(VoiceConversationDirection.Outgoing, text, sessionKey);
            RaiseTranscriptDraft(string.Empty, sessionKey, clear: true);
            _ = MonitorReplyTimeoutAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Voice transcript submit failed", ex);
            var userMessage = GetUserFacingErrorMessage(ex);

            lock (_gate)
            {
                _awaitingReply = false;
                _status = BuildRunningStatus(
                    VoiceActivationMode.TalkMode,
                    _status.SessionKey,
                    VoiceRuntimeState.Arming,
                    userMessage);
            }

            await ResumeRecognitionSessionAsync(cancellationToken, "transcript submit failure", userMessage);
        }
    }

    private async Task MonitorReplyTimeoutAsync(string transcript, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReplyTimeout, cancellationToken);

            var shouldResume = false;
            string? lateReplySessionKey = null;
            lock (_gate)
            {
                if (_awaitingReply &&
                    string.Equals(_lastTranscript, transcript, StringComparison.OrdinalIgnoreCase))
                {
                    _awaitingReply = false;
                    lateReplySessionKey = GetCurrentVoiceSessionKey();
                    _lateReplySessionKey = lateReplySessionKey;
                    _lateReplyGraceUntilUtc = DateTime.UtcNow.Add(LateReplyGraceWindow);
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        "Timed out waiting for an assistant reply.");
                    shouldResume = true;
                }
            }

            if (shouldResume)
            {
                _logger.Warn(
                    $"Voice reply wait timed out after {ReplyTimeout.TotalSeconds:0}s; accepting late replies for {LateReplyGraceWindow.TotalSeconds:0}s on session {lateReplySessionKey ?? "(none)"}");
                await ResumeRecognitionSessionAsync(cancellationToken, "reply timeout");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnChatMessageReceived(object? sender, ChatMessageEventArgs args)
    {
        try
        {
            if (!args.IsFinal ||
                !string.Equals(args.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(args.Message))
            {
                return;
            }

            string text;
            bool shouldStartPlaybackLoop = false;
            bool acceptedViaLateReplyGrace = false;

            lock (_gate)
            {
                if (!_status.Running || _status.Mode != VoiceActivationMode.TalkMode)
                {
                    return;
                }

                if (!IsMatchingSessionKey(args.SessionKey, GetCurrentVoiceSessionKey()))
                {
                    return;
                }

                acceptedViaLateReplyGrace = ShouldAcceptLateAssistantReply(
                    _awaitingReply,
                    _isSpeaking,
                    _pendingAssistantReplies.Count,
                    _lateReplySessionKey,
                    _lateReplyGraceUntilUtc,
                    args.SessionKey,
                    DateTime.UtcNow);

                if (!ShouldAcceptAssistantReply(_awaitingReply, _isSpeaking, _pendingAssistantReplies.Count, acceptedViaLateReplyGrace))
                {
                    return;
                }

                _awaitingReply = false;
                if (acceptedViaLateReplyGrace)
                {
                    _lateReplySessionKey = null;
                    _lateReplyGraceUntilUtc = null;
                }
                text = PrepareReplyForSpeech(args.Message);
            }

            if (acceptedViaLateReplyGrace)
            {
                _logger.Warn($"Voice accepted late assistant reply after timeout for session {args.SessionKey}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                var shouldResumeRecognition = false;
                lock (_gate)
                {
                    if (_status.Running && !_replyPlaybackLoopActive)
                    {
                        _status = BuildRunningStatus(
                            VoiceActivationMode.TalkMode,
                            _status.SessionKey,
                            VoiceRuntimeState.ListeningContinuously,
                            _status.LastError);
                        shouldResumeRecognition = true;
                    }
                }

                if (shouldResumeRecognition)
                {
                    await ResumeRecognitionSessionAsync(CancellationToken.None, "empty assistant reply");
                }
                return;
            }

            RaiseConversationTurn(VoiceConversationDirection.Incoming, text, args.SessionKey);

            lock (_gate)
            {
                _pendingAssistantReplies.Enqueue((text, args.SessionKey));
                _logger.Info($"Voice reply queued: pending={_pendingAssistantReplies.Count}");

                if (!_replyPlaybackLoopActive)
                {
                    _replyPlaybackLoopActive = true;
                    _isSpeaking = true;
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.PlayingResponse,
                        _status.LastError);
                    shouldStartPlaybackLoop = true;
                }
                else
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.PlayingResponse,
                        _status.LastError);
                }
            }

            if (shouldStartPlaybackLoop)
            {
                _ = ProcessQueuedAssistantRepliesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice chat message handler failed: {ex.Message}");
        }
    }

    private async Task ProcessQueuedAssistantRepliesAsync()
    {
        try
        {
            while (true)
            {
                (string Text, string? SessionKey) reply;
                var shouldPauseBeforeNextReply = false;
                CancellationTokenSource? playbackSkipCts = null;

                lock (_gate)
                {
                    if (_pendingAssistantReplies.Count == 0)
                    {
                        _replyPlaybackLoopActive = false;
                        _isSpeaking = false;
                        _currentReplyPreview = null;

                        if (_status.Running)
                        {
                            _status = BuildRunningStatus(
                                VoiceActivationMode.TalkMode,
                                _status.SessionKey,
                                VoiceRuntimeState.ListeningContinuously,
                                _status.LastError);
                        }

                        break;
                    }

                    reply = _pendingAssistantReplies.Dequeue();
                    shouldPauseBeforeNextReply = _pendingAssistantReplies.Count > 0;
                    _currentReplyPreview = CreateReplyPreview(reply.Text);
                    _isSpeaking = true;
                    _playbackSkipCts = playbackSkipCts = new CancellationTokenSource();
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.PlayingResponse,
                        _status.LastError);
                }

                try
                {
                    await SpeakTextAsync(reply.Text, playbackSkipCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.Info($"Voice reply playback canceled: remainingQueue={CurrentStatus.PendingReplyCount}");
                }
                catch (Exception ex)
                {
                    _logger.Error("Voice reply playback failed", ex);
                    lock (_gate)
                    {
                        _status = BuildRunningStatus(
                            VoiceActivationMode.TalkMode,
                            _status.SessionKey,
                            shouldPauseBeforeNextReply ? VoiceRuntimeState.PlayingResponse : VoiceRuntimeState.ListeningContinuously,
                            GetUserFacingErrorMessage(ex));
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_playbackSkipCts, playbackSkipCts))
                        {
                            _playbackSkipCts = null;
                        }

                        _currentReplyPreview = null;
                    }

                    playbackSkipCts?.Dispose();
                }

                if (shouldPauseBeforeNextReply)
                {
                    _logger.Info($"Voice reply playback paused before next queued response ({QueuedReplyPlaybackGap.TotalMilliseconds}ms)");
                    await Task.Delay(QueuedReplyPlaybackGap);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _replyPlaybackLoopActive = false;
                _isSpeaking = false;
                _currentReplyPreview = null;
                if (_status.Running)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        _status.LastError);
                }
            }

            try
            {
                await ResumeRecognitionSessionAsync(CancellationToken.None, "queued assistant reply playback completed");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Voice recognition resume failed: {ex.Message}");
            }
        }
    }

    private async Task SpeakTextAsync(string text, CancellationToken cancellationToken)
    {
        VoiceSettings settings;
        VoiceProviderConfigurationStore providerConfiguration;
        SpeechSynthesizer? synthesizer;
        MediaPlayer? player;

        lock (_gate)
        {
            settings = Clone(_settings.Voice);
            providerConfiguration = _settings.VoiceProviderConfiguration.Clone();
            synthesizer = _speechSynthesizer;
            player = _mediaPlayer;
        }

        if (player == null)
        {
            throw new InvalidOperationException("Speech playback is not ready.");
        }

        var provider = VoiceProviderCatalogService.ResolveTextToSpeechProvider(
            settings.TextToSpeechProviderId,
            _logger);

        if (UsesCloudTextToSpeechRuntime(provider))
        {
            using var result = await _cloudTextToSpeechClient.SynthesizeAsync(text, provider, providerConfiguration, _logger);
            await PlayStreamAsync(player, result.Stream, result.ContentType, cancellationToken);
            return;
        }

        if (synthesizer == null)
        {
            throw new InvalidOperationException("Speech playback is not ready.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        _logger.Info($"Windows TTS latency: total={stopwatch.ElapsedMilliseconds}ms");
        await PlayStreamAsync(player, stream, stream.ContentType, cancellationToken);
    }

    private static bool UsesCloudTextToSpeechRuntime(VoiceProviderOption provider)
    {
        return provider.TextToSpeechHttp != null || provider.TextToSpeechWebSocket != null;
    }

    internal static bool ShouldAcceptAssistantReply(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        bool acceptedViaLateReplyGrace = false)
    {
        return awaitingReply || isSpeaking || queuedReplyCount > 0 || acceptedViaLateReplyGrace;
    }

    internal static bool ShouldAcceptLateAssistantReply(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        string? lateReplySessionKey,
        DateTime? lateReplyGraceUntilUtc,
        string? incomingSessionKey,
        DateTime utcNow)
    {
        return !awaitingReply &&
               !isSpeaking &&
               queuedReplyCount == 0 &&
               !string.IsNullOrWhiteSpace(lateReplySessionKey) &&
               !string.IsNullOrWhiteSpace(incomingSessionKey) &&
               string.Equals(lateReplySessionKey, incomingSessionKey, StringComparison.OrdinalIgnoreCase) &&
               lateReplyGraceUntilUtc.HasValue &&
               utcNow <= lateReplyGraceUntilUtc.Value;
    }

    private static string CreateReplyPreview(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= 120)
        {
            return trimmed;
        }

        return $"{trimmed[..117]}...";
    }

    private static async Task PlayStreamAsync(
        MediaPlayer player,
        IRandomAccessStream stream,
        string contentType,
        CancellationToken cancellationToken)
    {
        stream.Seek(0);
        var playbackEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TypedEventHandler<MediaPlayer, object>? endedHandler = null;
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? failedHandler = null;

        endedHandler = (sender, _) => playbackEnded.TrySetResult(true);
        failedHandler = (sender, args) => playbackEnded.TrySetException(new InvalidOperationException(args.ErrorMessage));

        player.MediaEnded += endedHandler;
        player.MediaFailed += failedHandler;
        using var registration = cancellationToken.Register(() =>
        {
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            playbackEnded.TrySetCanceled(cancellationToken);
        });

        try
        {
            player.Source = MediaSource.CreateFromStream(stream, contentType);
            player.Play();
            await playbackEnded.Task;
        }
        finally
        {
            player.MediaEnded -= endedHandler;
            player.MediaFailed -= failedHandler;
            player.Source = null;
        }
    }

    private async void OnSpeechRecognitionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        try
        {
            CancellationToken token;
            var shouldRestart = false;

            lock (_gate)
            {
                if (_runtimeCts == null || _runtimeCts.IsCancellationRequested)
                {
                    return;
                }

                _recognitionActive = false;
                _recognitionHealthCheckArmed =
                    args.Status == SpeechRecognitionResultStatus.UserCanceled ||
                    args.Status == SpeechRecognitionResultStatus.TimeoutExceeded;
                token = _runtimeCts.Token;
                shouldRestart = _status.Running &&
                                _status.Mode == VoiceActivationMode.TalkMode &&
                                !_awaitingReply &&
                                !_isSpeaking;
            }

            _logger.Warn($"Speech recognition session completed with status {args.Status}; restart={shouldRestart}");

            if (shouldRestart && !token.IsCancellationRequested)
            {
                await Task.Delay(250, token);
                await ResumeRecognitionSessionAsync(token, $"recognition completed ({args.Status})");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice recognition completion handler failed: {ex.Message}");
        }
    }

    private void OnChatTransportStatusChanged(object? sender, ConnectionStatus status)
    {
        lock (_gate)
        {
            _chatTransportStatus = status;

            if (status == ConnectionStatus.Connected)
            {
                _transportReadyTcs?.TrySetResult(true);

                if (_status.Running &&
                    _status.Mode == VoiceActivationMode.TalkMode &&
                    !_awaitingReply &&
                    !_isSpeaking)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        _status.LastError);
                }
            }
            else if (status == ConnectionStatus.Error)
            {
                _transportReadyTcs?.TrySetException(
                    new InvalidOperationException("Voice chat transport failed to connect."));

                if (_status.Running && _status.Mode == VoiceActivationMode.TalkMode)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        "Voice chat transport failed.");
                }
            }
            else if (status == ConnectionStatus.Disconnected)
            {
                if (_status.Running && _status.Mode == VoiceActivationMode.TalkMode)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        "Voice chat transport disconnected.");
                }
            }
        }
    }

    private async Task StopRuntimeResourcesAsync(bool updateStoppedStatus)
    {
        CancellationTokenSource? runtimeCts;
        CancellationTokenSource? playbackSkipCts;
        OpenClawGatewayClient? chatClient;
        SpeechRecognizer? recognizer;
        SpeechSynthesizer? synthesizer;
        MediaPlayer? player;
        var sessionKey = CurrentStatus.SessionKey;

        lock (_gate)
        {
            runtimeCts = _runtimeCts;
            _runtimeCts = null;

            chatClient = _chatClient;
            _chatClient = null;
            _chatTransportStatus = ConnectionStatus.Disconnected;
            _transportReadyTcs = null;

            recognizer = _speechRecognizer;
            _speechRecognizer = null;
            _recognitionActive = false;

            synthesizer = _speechSynthesizer;
            _speechSynthesizer = null;

            player = _mediaPlayer;
            _mediaPlayer = null;

            _awaitingReply = false;
            _isSpeaking = false;
            _replyPlaybackLoopActive = false;
            _pendingAssistantReplies.Clear();
            _currentReplyPreview = null;
            _lateReplySessionKey = null;
            _lateReplyGraceUntilUtc = null;
            playbackSkipCts = _playbackSkipCts;
            _playbackSkipCts = null;
        }

        try { runtimeCts?.Cancel(); } catch { }
        try { playbackSkipCts?.Cancel(); } catch { }

        if (recognizer != null)
        {
            recognizer.HypothesisGenerated -= OnSpeechHypothesisGenerated;
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnSpeechResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed -= OnSpeechRecognitionCompleted;

            try { await recognizer.ContinuousRecognitionSession.CancelAsync(); } catch { }
            try { recognizer.Dispose(); } catch { }
        }

        if (player != null)
        {
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
        }

        try { synthesizer?.Dispose(); } catch { }

        if (chatClient != null)
        {
            chatClient.StatusChanged -= OnChatTransportStatusChanged;
            chatClient.ChatMessageReceived -= OnChatMessageReceived;
            try { await chatClient.DisconnectAsync(); } catch { }
            try { chatClient.Dispose(); } catch { }
        }

        try { runtimeCts?.Dispose(); } catch { }
        try { playbackSkipCts?.Dispose(); } catch { }

        if (updateStoppedStatus)
        {
            lock (_gate)
            {
                _status = BuildStoppedStatus(sessionKey, "Disposed");
            }
        }

        RaiseTranscriptDraft(string.Empty, sessionKey, clear: true);
    }

    private string GetCurrentVoiceSessionKey()
    {
        return string.IsNullOrWhiteSpace(_status.SessionKey) ? DefaultSessionKey : _status.SessionKey!;
    }

    private static bool IsMatchingSessionKey(string? actualSessionKey, string? expectedSessionKey)
    {
        actualSessionKey = string.IsNullOrWhiteSpace(actualSessionKey) ? DefaultSessionKey : actualSessionKey;
        expectedSessionKey = string.IsNullOrWhiteSpace(expectedSessionKey) ? DefaultSessionKey : expectedSessionKey;

        if (string.Equals(actualSessionKey, expectedSessionKey, StringComparison.Ordinal))
        {
            return true;
        }

        return IsMainSessionKey(actualSessionKey) && IsMainSessionKey(expectedSessionKey);
    }

    private static bool IsMainSessionKey(string sessionKey)
    {
        return sessionKey == DefaultSessionKey || sessionKey.Contains(":main:", StringComparison.Ordinal);
    }

    private static string PrepareReplyForSpeech(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline <= 0)
        {
            return trimmed;
        }

        var firstLine = trimmed[..firstNewline].Trim();
        if (!firstLine.StartsWith("{", StringComparison.Ordinal))
        {
            return trimmed;
        }

        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("voice", out _) &&
                !doc.RootElement.TryGetProperty("voiceId", out _) &&
                !doc.RootElement.TryGetProperty("voice_id", out _))
            {
                return trimmed;
            }

            return trimmed[(firstNewline + 1)..].TrimStart();
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private VoiceStatusInfo BuildRunningStatus(
        VoiceActivationMode mode,
        string? sessionKey,
        VoiceRuntimeState state,
        string? lastError)
    {
        var settings = _settings.Voice;
        return new VoiceStatusInfo
        {
            Available = true,
            Running = true,
            Mode = mode,
            State = state,
            SessionKey = sessionKey,
            InputDeviceId = settings.InputDeviceId,
            OutputDeviceId = settings.OutputDeviceId,
            VoiceWakeModelId = settings.VoiceWake.ModelId,
            VoiceWakeLoaded = mode == VoiceActivationMode.VoiceWake,
            LastVoiceWakeUtc = _status.LastVoiceWakeUtc,
            LastUtteranceUtc = _status.LastUtteranceUtc,
            PendingReplyCount = _pendingAssistantReplies.Count,
            CanSkipReply = _isSpeaking || _pendingAssistantReplies.Count > 0,
            CurrentReplyPreview = _currentReplyPreview,
            LastError = lastError
        };
    }

    private void ArmRecognitionHealthCheck()
    {
        lock (_gate)
        {
            _recognitionHealthCheckArmed = true;
        }
    }

    private async Task MonitorRecognitionSessionHealthAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecognitionHealthCheckDelay, cancellationToken);

            var shouldRecycle = false;
            lock (_gate)
            {
                shouldRecycle =
                    _recognitionHealthCheckArmed &&
                    _recognitionActive &&
                    _runtimeCts != null &&
                    !_runtimeCts.IsCancellationRequested &&
                    _status.Running &&
                    _status.Mode == VoiceActivationMode.TalkMode &&
                    !_awaitingReply &&
                    !_isSpeaking &&
                    generation == _recognitionSessionGeneration;

                if (shouldRecycle)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.TalkMode,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        "Speech recognizer stalled; restarting listening.");
                }
            }

            if (!shouldRecycle)
            {
                return;
            }

            _logger.Warn(
                $"Speech recognition session produced no hypotheses/results within {RecognitionHealthCheckDelay.TotalSeconds:0}s; recycling session");
            await ResumeRecognitionSessionAsync(cancellationToken, "recognition health check");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Speech recognition health check failed: {ex.Message}");
        }
    }

    private VoiceStatusInfo BuildStoppedStatus(string? sessionKey, string? reason)
    {
        var settings = _settings.Voice;
        return new VoiceStatusInfo
        {
            Available = true,
            Running = false,
            Mode = _runtimeModeOverride ?? settings.Mode,
            State = VoiceRuntimeState.Stopped,
            SessionKey = sessionKey,
            InputDeviceId = settings.InputDeviceId,
            OutputDeviceId = settings.OutputDeviceId,
            VoiceWakeModelId = settings.VoiceWake.ModelId,
            VoiceWakeLoaded = false,
            LastVoiceWakeUtc = _status.LastVoiceWakeUtc,
            LastUtteranceUtc = _status.LastUtteranceUtc,
            PendingReplyCount = _pendingAssistantReplies.Count,
            CanSkipReply = _isSpeaking || _pendingAssistantReplies.Count > 0,
            CurrentReplyPreview = _currentReplyPreview,
            LastError = reason
        };
    }

    private VoiceStatusInfo BuildPausedStatus(VoiceActivationMode mode, string? sessionKey, string? reason)
    {
        var settings = _settings.Voice;
        return new VoiceStatusInfo
        {
            Available = true,
            Running = false,
            Mode = mode,
            State = VoiceRuntimeState.Paused,
            SessionKey = sessionKey,
            InputDeviceId = settings.InputDeviceId,
            OutputDeviceId = settings.OutputDeviceId,
            VoiceWakeModelId = settings.VoiceWake.ModelId,
            VoiceWakeLoaded = false,
            LastVoiceWakeUtc = _status.LastVoiceWakeUtc,
            LastUtteranceUtc = _status.LastUtteranceUtc,
            PendingReplyCount = _pendingAssistantReplies.Count,
            CanSkipReply = _isSpeaking || _pendingAssistantReplies.Count > 0,
            CurrentReplyPreview = _currentReplyPreview,
            LastError = reason
        };
    }

    private VoiceStatusInfo BuildErrorStatus(VoiceActivationMode mode, string? sessionKey, string? reason)
    {
        var status = BuildRunningStatus(mode, sessionKey, VoiceRuntimeState.Error, reason);
        status.Running = false;
        return status;
    }

    private static VoiceSettings Clone(VoiceSettings source)
    {
        return new VoiceSettings
        {
            Mode = source.Mode,
            Enabled = source.Enabled,
            ShowConversationToasts = source.ShowConversationToasts,
            StripInjectedMemoriesInChat = source.StripInjectedMemoriesInChat,
            SpeechToTextProviderId = source.SpeechToTextProviderId,
            TextToSpeechProviderId = source.TextToSpeechProviderId,
            InputDeviceId = source.InputDeviceId,
            OutputDeviceId = source.OutputDeviceId,
            SampleRateHz = source.SampleRateHz,
            CaptureChunkMs = source.CaptureChunkMs,
            BargeInEnabled = source.BargeInEnabled,
            VoiceWake = new VoiceWakeSettings
            {
                Engine = source.VoiceWake.Engine,
                ModelId = source.VoiceWake.ModelId,
                TriggerThreshold = source.VoiceWake.TriggerThreshold,
                TriggerCooldownMs = source.VoiceWake.TriggerCooldownMs,
                PreRollMs = source.VoiceWake.PreRollMs,
                EndSilenceMs = source.VoiceWake.EndSilenceMs
            },
            TalkMode = new TalkModeSettings
            {
                MinSpeechMs = source.TalkMode.MinSpeechMs,
                EndSilenceMs = source.TalkMode.EndSilenceMs,
                MaxUtteranceMs = source.TalkMode.MaxUtteranceMs
            }
        };
    }

    private static VoiceStatusInfo Clone(VoiceStatusInfo source)
    {
        return new VoiceStatusInfo
        {
            Available = source.Available,
            Running = source.Running,
            Mode = source.Mode,
            State = source.State,
            SessionKey = source.SessionKey,
            InputDeviceId = source.InputDeviceId,
            OutputDeviceId = source.OutputDeviceId,
            VoiceWakeModelId = source.VoiceWakeModelId,
            VoiceWakeLoaded = source.VoiceWakeLoaded,
            LastVoiceWakeUtc = source.LastVoiceWakeUtc,
            LastUtteranceUtc = source.LastUtteranceUtc,
            PendingReplyCount = source.PendingReplyCount,
            CanSkipReply = source.CanSkipReply,
            CurrentReplyPreview = source.CurrentReplyPreview,
            LastError = source.LastError
        };
    }

    private static string? BuildProviderFallbackMessage(
        VoiceProviderOption speechToTextProvider,
        VoiceProviderOption textToSpeechProvider)
    {
        var fallbacks = new List<string>();

        if (!VoiceProviderCatalogService.SupportsWindowsRuntime(speechToTextProvider.Id))
        {
            fallbacks.Add($"STT '{speechToTextProvider.Name}' is not implemented yet; using Windows Speech Recognition.");
        }

        if (!VoiceProviderCatalogService.SupportsTextToSpeechRuntime(textToSpeechProvider.Id))
        {
            fallbacks.Add($"TTS '{textToSpeechProvider.Name}' is not implemented yet; using Windows Speech Synthesis.");
        }

        return fallbacks.Count == 0 ? null : string.Join(" ", fallbacks);
    }

    private static string GetUserFacingErrorMessage(Exception ex)
    {
        if (IsSpeechPrivacyDeclined(ex))
        {
            return "Windows online speech recognition is disabled. Open Settings > Privacy & security > Speech and turn on Online speech recognition, then restart Voice Mode.";
        }

        if (ex is UnauthorizedAccessException)
        {
            return "Microphone access is blocked. Open Settings > Privacy & security > Microphone and allow desktop apps to use the microphone.";
        }

        return ex.Message;
    }

    private static bool IsSpeechPrivacyDeclined(Exception ex)
    {
        if (ex.HResult == HResultSpeechPrivacyDeclined)
        {
            return true;
        }

        return ex.Message.Contains("speech privacy policy", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("online speech recognition", StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseConversationTurn(VoiceConversationDirection direction, string text, string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ConversationTurnAvailable?.Invoke(this, new VoiceConversationTurnEventArgs
        {
            Direction = direction,
            Message = text,
            SessionKey = string.IsNullOrWhiteSpace(sessionKey) ? DefaultSessionKey : sessionKey,
            Mode = _runtimeModeOverride ?? _settings.Voice.Mode
        });
    }

    private void RaiseTranscriptDraft(string text, string? sessionKey, bool clear)
    {
        TranscriptDraftUpdated?.Invoke(this, new VoiceTranscriptDraftEventArgs
        {
            SessionKey = string.IsNullOrWhiteSpace(sessionKey) ? DefaultSessionKey : sessionKey,
            Text = clear ? string.Empty : text,
            Clear = clear,
            Mode = _runtimeModeOverride ?? _settings.Voice.Mode
        });
    }
}

public enum VoiceConversationDirection
{
    Outgoing,
    Incoming
}

public sealed class VoiceConversationTurnEventArgs : EventArgs
{
    public VoiceConversationDirection Direction { get; set; }
    public string SessionKey { get; set; } = "main";
    public string Message { get; set; } = "";
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
}

public sealed class VoiceTranscriptDraftEventArgs : EventArgs
{
    public string SessionKey { get; set; } = "main";
    public string Text { get; set; } = "";
    public bool Clear { get; set; }
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
}

