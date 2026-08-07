using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.VoiceAssistant;

namespace OpenClawTray.Presentation;

internal sealed class VoiceAssistantSettingsViewModel :
    INavigationAware,
    IDisposable,
    INotifyPropertyChanged
{
    private readonly ISettingsStore _settings;
    private readonly IVoiceAssistantSettingsEnvironment _environment;
    private readonly IAppCommands _appCommands;
    private bool _active;
    private bool _loading;
    private int _selectedModeIndex;
    private string _wakePhraseDraft = VoiceAssistantSettingsPolicy.DefaultWakePhrase;
    private bool _canEnableWakeMode;
    private string _statusText = string.Empty;
    private VoiceAssistantReadinessResult _prerequisiteReadiness;

    public VoiceAssistantSettingsViewModel(
        ISettingsStore settings,
        IVoiceAssistantSettingsEnvironment environment,
        IAppCommands appCommands)
    {
        _settings = settings;
        _environment = environment;
        _appCommands = appCommands;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set
        {
            var normalized = value == 1 ? 1 : 0;
            if (_loading || normalized == _selectedModeIndex)
                return;

            if (normalized == 1 && (!CommitWakePhrase() || !CanEnableWakeMode))
            {
                OnPropertyChanged();
                return;
            }

            _selectedModeIndex = normalized;
            OnPropertyChanged();
            Persist(editor =>
                editor.VoiceAssistantMode = normalized == 1
                    ? VoiceAssistantSettingsPolicy.WakeOneShotMode
                    : VoiceAssistantSettingsPolicy.OffMode);
            Refresh();
        }
    }

    public string WakePhraseDraft
    {
        get => _wakePhraseDraft;
        set
        {
            if (SetField(ref _wakePhraseDraft, value ?? string.Empty))
                RefreshDraft();
        }
    }

    public bool CanEnableWakeMode
    {
        get => _canEnableWakeMode;
        private set => SetField(ref _canEnableWakeMode, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool CommitWakePhrase()
    {
        if (!VoiceAssistantSettingsPolicy.TryNormalizeWakePhrase(_wakePhraseDraft, out var normalized))
        {
            RefreshDraft();
            return false;
        }

        WakePhraseDraft = normalized;
        if (!string.Equals(_settings.Current.VoiceAssistantWakePhrase, normalized, StringComparison.Ordinal))
            Persist(editor => editor.VoiceAssistantWakePhrase = normalized);
        return true;
    }

    public void Refresh()
    {
        _prerequisiteReadiness =
            _environment.GetReadiness(VoiceAssistantSettingsPolicy.DefaultWakePhrase);
        RefreshDraft();
    }

    private void RefreshDraft()
    {
        var readiness = VoiceAssistantSettingsPolicy.TryNormalizeWakePhrase(_wakePhraseDraft, out _)
            ? _prerequisiteReadiness
            : new VoiceAssistantReadinessResult(
                false,
                VoiceAssistantReadinessReason.WakePhraseInvalid);
        CanEnableWakeMode = readiness.IsReady;

        var statusKey = readiness.IsReady
            ? RuntimeStatusKey(_environment.RuntimeState)
            : ReadinessStatusKey(readiness.Reason);
        StatusText = _environment.GetString(statusKey);
    }

    public void Activate(object? parameter)
    {
        if (_active)
            return;
        _active = true;
        _settings.Changed += OnChanged;
        _environment.Changed += OnChanged;
        Load();
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _settings.Changed -= OnChanged;
        _environment.Changed -= OnChanged;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var snapshot = _settings.Current;
            _selectedModeIndex = string.Equals(
                snapshot.VoiceAssistantMode,
                VoiceAssistantSettingsPolicy.WakeOneShotMode,
                StringComparison.Ordinal)
                    ? 1
                    : 0;
            _wakePhraseDraft = snapshot.VoiceAssistantWakePhrase;
            OnPropertyChanged(nameof(SelectedModeIndex));
            OnPropertyChanged(nameof(WakePhraseDraft));
            Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Load();

    private void Persist(Action<ISettingsEditor> edit)
    {
        _settings.Update(edit);
        _appCommands.NotifySettingsSaved();
    }

    private static string RuntimeStatusKey(VoiceAssistantState state) => state switch
    {
        VoiceAssistantState.WakeListening => "VoiceSettingsPage_AssistantStatusListening",
        VoiceAssistantState.Dispatching => "VoiceSettingsPage_AssistantStatusDispatching",
        VoiceAssistantState.WaitingForReply => "VoiceSettingsPage_AssistantStatusWaiting",
        VoiceAssistantState.Speaking => "VoiceSettingsPage_AssistantStatusSpeaking",
        VoiceAssistantState.Starting => "VoiceSettingsPage_AssistantStatusStarting",
        VoiceAssistantState.Error => "VoiceSettingsPage_AssistantStatusError",
        VoiceAssistantState.Unavailable => "VoiceSettingsPage_AssistantStatusPaused",
        _ => "VoiceSettingsPage_AssistantStatusOff"
    };

    private static string ReadinessStatusKey(VoiceAssistantReadinessReason reason) => reason switch
    {
        VoiceAssistantReadinessReason.SttDisabled => "VoiceSettingsPage_AssistantStatusSttDisabled",
        VoiceAssistantReadinessReason.SttModelMissing => "VoiceSettingsPage_AssistantStatusModelMissing",
        VoiceAssistantReadinessReason.TtsDisabled => "VoiceSettingsPage_AssistantStatusTtsDisabled",
        VoiceAssistantReadinessReason.PiperVoiceMissing => "VoiceSettingsPage_AssistantStatusPiperMissing",
        VoiceAssistantReadinessReason.ElevenLabsApiKeyMissing => "VoiceSettingsPage_AssistantStatusApiKeyMissing",
        VoiceAssistantReadinessReason.ElevenLabsVoiceMissing => "VoiceSettingsPage_AssistantStatusVoiceIdMissing",
        VoiceAssistantReadinessReason.WakePhraseInvalid => "VoiceSettingsPage_AssistantStatusWakeInvalid",
        _ => "VoiceSettingsPage_AssistantStatusProviderInvalid"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => Deactivate();
}
