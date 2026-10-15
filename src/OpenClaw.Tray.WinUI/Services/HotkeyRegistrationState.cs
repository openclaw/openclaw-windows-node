namespace OpenClawTray.Services;

/// <summary>
/// Which global hotkeys actually registered. Voice and Settings are
/// independent. Turning hotkeys off must release every live id.
/// </summary>
public readonly record struct HotkeyRegistrationState(bool Voice, bool Settings)
{
    public bool AnyLive => Voice || Settings;

    public bool AllLive => Voice && Settings;

    public bool NeedsVoiceRegistration => !Voice;

    public bool NeedsSettingsRegistration => !Settings;

    public HotkeyRegistrationState AfterUnregister(bool voiceUnregistered, bool settingsUnregistered) =>
        new(Voice && !voiceUnregistered, Settings && !settingsUnregistered);
}
