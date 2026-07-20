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
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        AppCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IUiDispatcher Dispatcher { get; }
    public IAppCommands AppCommands { get; }
    public SettingsManager Settings { get; }
}
