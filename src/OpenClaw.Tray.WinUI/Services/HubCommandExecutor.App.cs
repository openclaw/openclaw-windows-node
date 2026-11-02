using Microsoft.UI.Xaml;

namespace OpenClawTray.Services;

internal sealed partial class HubCommandExecutor
{
    public HubCommandExecutor()
        : this(
            () => ((App)Application.Current).SettingsOrNull,
            () => ((App)Application.Current).SettingsStore,
            path => ((IAppCommands)Application.Current).OpenDashboard(path))
    {
    }
}
