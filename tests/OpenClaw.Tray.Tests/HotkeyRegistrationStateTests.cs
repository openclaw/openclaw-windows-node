using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class HotkeyRegistrationStateTests
{
    [Fact]
    public void SettingsChordStaysLiveWhenVoiceRegistrationFails()
    {
        var state = new HotkeyRegistrationState(Voice: false, Settings: true);

        Assert.True(state.AnyLive);
        Assert.False(state.AllLive);
        Assert.False(state.Voice);
        Assert.True(state.Settings);
        Assert.True(state.NeedsVoiceRegistration);
        Assert.False(state.NeedsSettingsRegistration);
    }

    [Fact]
    public void NeitherChord_IsNotLive()
    {
        var state = new HotkeyRegistrationState(Voice: false, Settings: false);

        Assert.False(state.AnyLive);
        Assert.False(state.AllLive);
        Assert.True(state.NeedsVoiceRegistration);
        Assert.True(state.NeedsSettingsRegistration);
    }

    [Fact]
    public void SuccessfulUnregister_DropsBothChords()
    {
        var state = new HotkeyRegistrationState(true, true)
            .AfterUnregister(voiceUnregistered: true, settingsUnregistered: true);

        Assert.False(state.AnyLive);
    }

    [Fact]
    public void FailedSettingsUnregister_RemainsLiveForRetry()
    {
        var state = new HotkeyRegistrationState(true, true)
            .AfterUnregister(voiceUnregistered: true, settingsUnregistered: false);

        Assert.False(state.Voice);
        Assert.True(state.Settings);
        Assert.True(state.AnyLive);
    }

    [Fact]
    public void Service_WiresIndependentRegistrationAndUnregistration()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "GlobalHotkeyService.cs"));

        Assert.Contains("GetHotkeys().AllLive", source, StringComparison.Ordinal);
        Assert.Contains("previous.NeedsSettingsRegistration", source, StringComparison.Ordinal);
        Assert.Contains("TryUnregisterHotkey(hWnd, HOTKEY_ID_SETTINGS, \"settings\")", source, StringComparison.Ordinal);
        Assert.Contains("Failed to unregister {name} hotkey", source, StringComparison.Ordinal);
        Assert.Contains("SetHotkeys(default)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_registered)", source, StringComparison.Ordinal);
    }
}
