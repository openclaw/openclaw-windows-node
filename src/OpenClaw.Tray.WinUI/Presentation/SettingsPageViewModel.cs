using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Transient view-model placeholder for the Settings page. It exercises the DI wiring
/// end to end: it is constructor-injected with app services, participates in the
/// navigation activation/deactivation lifetime, and is disposed when its navigation
/// scope ends. It intentionally holds no behavior yet. Nothing is started in the
/// constructor.
/// </summary>
internal sealed class SettingsPageViewModel : INavigationAware, IDisposable
{
    public SettingsPageViewModel(IAppCommands appCommands, SettingsManager settings, IUiDispatcher dispatcher)
    {
        AppCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal IAppCommands AppCommands { get; }
    internal SettingsManager Settings { get; }
    internal IUiDispatcher Dispatcher { get; }

    internal bool IsActive { get; private set; }
    internal bool IsDisposed { get; private set; }

    public void Activate(object? parameter) => IsActive = true;

    public void Deactivate() => IsActive = false;

    public void Dispose() => IsDisposed = true;
}
