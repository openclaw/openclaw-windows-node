using OpenClawTray.Windows;

namespace OpenClawTray.Services;

internal sealed partial class WindowManager
{
    private PersonalDashboardWindow? _personalDashboardWindow;

    public async Task<bool> ShowPersonalDashboardAsync(string? path = null)
    {
        if (_isShuttingDown || !_dispatcherQueue.HasThreadAccess)
            return false;

        var manager = _callbacks.GetConnectionManager();
        var lease = manager?.CurrentPersonalOperatorLease;
        if (manager is null || lease is null)
            return false;

        if (_personalDashboardWindow is not null && !_personalDashboardWindow.CanReuseFor(lease))
        {
            var retiring = _personalDashboardWindow;
            await retiring.CloseAsync();
            if (ReferenceEquals(_personalDashboardWindow, retiring))
                _personalDashboardWindow = null;
            if (_isShuttingDown || !ReferenceEquals(lease, manager.CurrentPersonalOperatorLease))
                return false;
        }

        if (_personalDashboardWindow is not null && !_personalDashboardWindow.CanReuseFor(lease))
            return false;

        if (_personalDashboardWindow is null)
        {
            var window = new PersonalDashboardWindow(manager, lease, path);
            _personalDashboardWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_personalDashboardWindow, window))
                    _personalDashboardWindow = null;
            };
            _callbacks.ApplyTheme(window);
        }
        else
        {
            _personalDashboardWindow.NavigateToPath(path);
        }

        _personalDashboardWindow.Activate();
        return true;
    }
}
