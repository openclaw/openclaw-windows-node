using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Services.VoiceAssistant;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class VoiceAssistantSettingsViewModelTests
{
    [Fact]
    public void Activate_LoadsDefaultOffState()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.Activate(null);

        Assert.Equal(0, fixture.ViewModel.SelectedModeIndex);
        Assert.Equal(VoiceAssistantSettingsPolicy.DefaultWakePhrase, fixture.ViewModel.WakePhraseDraft);
        Assert.True(fixture.ViewModel.CanEnableWakeMode);
        Assert.Equal("VoiceSettingsPage_AssistantStatusOff", fixture.ViewModel.StatusText);
    }

    [Fact]
    public void WakePhraseDraft_ValidatesWithoutPersisting()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);

        fixture.ViewModel.WakePhraseDraft = "too many words in this phrase";

        Assert.False(fixture.ViewModel.CanEnableWakeMode);
        Assert.Equal("VoiceSettingsPage_AssistantStatusWakeInvalid", fixture.ViewModel.StatusText);
        Assert.Equal(VoiceAssistantSettingsPolicy.DefaultWakePhrase, fixture.Settings.VoiceAssistantWakePhrase);
        Assert.Equal(0, fixture.AppCommands.NotifySettingsSavedCount);
    }

    [Fact]
    public void WakePhraseDraft_UsesCachedPrerequisitesWhileTyping()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);
        var readinessCalls = fixture.Environment.GetReadinessCount;

        fixture.ViewModel.WakePhraseDraft = "Hey";
        fixture.ViewModel.WakePhraseDraft = "Hey Claw";
        fixture.ViewModel.WakePhraseDraft = "too many words in this phrase";

        Assert.Equal(readinessCalls, fixture.Environment.GetReadinessCount);
        Assert.False(fixture.ViewModel.CanEnableWakeMode);
    }

    [Fact]
    public void SelectingWakeMode_WithInvalidPhrase_IsRefused()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);
        fixture.ViewModel.WakePhraseDraft = "too many words in this phrase";

        fixture.ViewModel.SelectedModeIndex = 1;

        Assert.Equal(0, fixture.ViewModel.SelectedModeIndex);
        Assert.Equal(VoiceAssistantSettingsPolicy.OffMode, fixture.Settings.VoiceAssistantMode);
        Assert.Equal(0, fixture.AppCommands.NotifySettingsSavedCount);
    }

    [Fact]
    public void CommitWakePhrase_NormalizesPersistsAndNotifies()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);
        fixture.ViewModel.WakePhraseDraft = "  Hey   Claw  ";

        var committed = fixture.ViewModel.CommitWakePhrase();

        Assert.True(committed);
        Assert.Equal("Hey Claw", fixture.ViewModel.WakePhraseDraft);
        Assert.Equal("Hey Claw", fixture.Settings.VoiceAssistantWakePhrase);
        Assert.Equal(1, fixture.AppCommands.NotifySettingsSavedCount);
    }

    [Fact]
    public void SelectingWakeMode_CommitsDraftAndPersistsMode()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);
        fixture.ViewModel.WakePhraseDraft = "Hey Claw";

        fixture.ViewModel.SelectedModeIndex = 1;

        Assert.Equal(1, fixture.ViewModel.SelectedModeIndex);
        Assert.Equal("Hey Claw", fixture.Settings.VoiceAssistantWakePhrase);
        Assert.Equal(VoiceAssistantSettingsPolicy.WakeOneShotMode, fixture.Settings.VoiceAssistantMode);
        Assert.Equal(2, fixture.AppCommands.NotifySettingsSavedCount);
    }

    [Theory]
    [InlineData(VoiceAssistantState.WakeListening, "VoiceSettingsPage_AssistantStatusListening")]
    [InlineData(VoiceAssistantState.WaitingForReply, "VoiceSettingsPage_AssistantStatusWaiting")]
    [InlineData(VoiceAssistantState.Speaking, "VoiceSettingsPage_AssistantStatusSpeaking")]
    [InlineData(VoiceAssistantState.Unavailable, "VoiceSettingsPage_AssistantStatusPaused")]
    public void EnvironmentChange_ProjectsRuntimeStatus(
        VoiceAssistantState state,
        string expectedStatus)
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);

        fixture.Environment.RuntimeState = state;
        fixture.Environment.RaiseChanged();

        Assert.Equal(expectedStatus, fixture.ViewModel.StatusText);
    }

    [Fact]
    public void ExternalSettingsChange_ReloadsWithoutNotifyingRuntime()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Activate(null);
        fixture.Settings.VoiceAssistantWakePhrase = "Computer";
        fixture.Settings.VoiceAssistantMode = VoiceAssistantSettingsPolicy.WakeOneShotMode;

        fixture.Settings.Save();

        Assert.Equal("Computer", fixture.ViewModel.WakePhraseDraft);
        Assert.Equal(1, fixture.ViewModel.SelectedModeIndex);
        Assert.Equal(0, fixture.AppCommands.NotifySettingsSavedCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _temp = new();

        public Fixture()
        {
            Settings = new SettingsManager(_temp.Path);
            Store = new SettingsStore(Settings, new RecordingUiDispatcher());
            Environment = new FakeEnvironment();
            AppCommands = new FakeAppCommands();
            ViewModel = new VoiceAssistantSettingsViewModel(Store, Environment, AppCommands);
        }

        public SettingsManager Settings { get; }
        public SettingsStore Store { get; }
        public FakeEnvironment Environment { get; }
        public FakeAppCommands AppCommands { get; }
        public VoiceAssistantSettingsViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class FakeEnvironment : IVoiceAssistantSettingsEnvironment
    {
        public event EventHandler? Changed;

        public VoiceAssistantState RuntimeState { get; set; } = VoiceAssistantState.Off;
        public int GetReadinessCount { get; private set; }

        public VoiceAssistantReadinessResult GetReadiness(string wakePhrase)
        {
            GetReadinessCount++;
            return VoiceAssistantSettingsPolicy.TryNormalizeWakePhrase(wakePhrase, out _)
                ? new(true, VoiceAssistantReadinessReason.Ready)
                : new(false, VoiceAssistantReadinessReason.WakePhraseInvalid);
        }

        public string GetString(string key) => key;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
