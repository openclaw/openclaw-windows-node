using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Transient view-model placeholder for the Permissions page. Mirrors
/// <see cref="SettingsPageViewModel"/>: it exercises DI construction, navigation
/// activation/deactivation, and scope-driven disposal. It intentionally holds no
/// behavior yet. Nothing is started in the constructor.
/// </summary>
internal sealed class PermissionsPageViewModel : INavigationAware, IDisposable
{
    public PermissionsPageViewModel(IAppCommands appCommands, SettingsManager settings, IUiDispatcher dispatcher)
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
