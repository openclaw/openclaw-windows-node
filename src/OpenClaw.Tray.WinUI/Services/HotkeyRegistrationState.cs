namespace OpenClawTray.Services;

/// <summary>
/// Which global hotkeys actually registered. Voice and Settings are
/// independent. Turning hotkeys off must release every live id.
/// </summary>
public readonly record struct HotkeyRegistrationState(bool Voice, bool Settings)
{
    public bool AnyLive => Voice || Settings;

    public HotkeyRegistrationState WithVoice(bool registered) => this with { Voice = registered };

    public HotkeyRegistrationState WithSettings(bool registered) => this with { Settings = registered };

    public HotkeyRegistrationState AfterUnregister(bool voiceUnregistered, bool settingsUnregistered) =>
        new(Voice && !voiceUnregistered, Settings && !settingsUnregistered);

    public HotkeyRegistrationState Cleared() => new(false, false);
}
