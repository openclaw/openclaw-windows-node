using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Carries the already-constructed, App-owned singletons that the composition root
/// registers as pre-built instances. Registering them as instances (rather than
/// letting the container construct them) means the DI container never disposes them,
/// so App keeps sole ownership of their lifetime and there is no double-dispose.
/// </summary>
internal sealed class AppServiceContext
{
    public AppServiceContext(IUiDispatcher dispatcher, IAppCommands appCommands, SettingsManager settings)
        : this(dispatcher, appCommands, settings, UnavailableVoiceAssistantEnvironment.Instance)
    {
    }

    public AppServiceContext(
        IUiDispatcher dispatcher,
        IAppCommands appCommands,
        SettingsManager settings,
        IVoiceAssistantSettingsEnvironment voiceAssistantEnvironment)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        AppCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        VoiceAssistantEnvironment = voiceAssistantEnvironment ??
            throw new ArgumentNullException(nameof(voiceAssistantEnvironment));
    }

    public IUiDispatcher Dispatcher { get; }
    public IAppCommands AppCommands { get; }
    public SettingsManager Settings { get; }
    public IVoiceAssistantSettingsEnvironment VoiceAssistantEnvironment { get; }

    private sealed class UnavailableVoiceAssistantEnvironment : IVoiceAssistantSettingsEnvironment
    {
        public static UnavailableVoiceAssistantEnvironment Instance { get; } = new();
        public event EventHandler? Changed { add { } remove { } }
        public OpenClawTray.Services.VoiceAssistant.VoiceAssistantState RuntimeState =>
            OpenClawTray.Services.VoiceAssistant.VoiceAssistantState.Off;
        public OpenClawTray.Services.VoiceAssistant.VoiceAssistantReadinessResult GetReadiness(string wakePhrase) =>
            new(false, OpenClawTray.Services.VoiceAssistant.VoiceAssistantReadinessReason.SttDisabled);
        public string GetString(string key) => key;
    }
}
