using Microsoft.UI.Dispatching;
using System;
using WinUIEx;

namespace OpenClawTray.Services;

internal sealed class TrayIconCoordinator
{
    private readonly TrayIcon _trayIcon;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<DispatcherQueueHandler> _marshal;
    private readonly Func<TrayStateSnapshot> _captureSnapshot;
    private readonly Func<bool> _isAlive;

    internal TrayIconCoordinator(
        TrayIcon trayIcon,
        Func<bool> hasThreadAccess,
        Action<DispatcherQueueHandler> marshal,
        Func<TrayStateSnapshot> captureSnapshot,
        Func<bool> isAlive)
    {
        _trayIcon = trayIcon;
        _hasThreadAccess = hasThreadAccess;
        _marshal = marshal;
        _captureSnapshot = captureSnapshot;
        _isAlive = isAlive;
    }

    internal void UpdateTrayIcon()
    {
        if (!_hasThreadAccess())
        {
            _marshal(UpdateTrayIcon);
            return;
        }

        // A queued update may run after shutdown has disposed the tray icon.
        // Bail out so we never touch a disposed instance.
        if (!_isAlive())
            return;

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "openclaw.ico");
        var tooltip = BuildTrayTooltip();

        try
        {
            _trayIcon.SetIcon(iconPath);
            ApplyTrayTooltip(tooltip);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update tray icon: {ex.Message}");
        }
    }

    internal void ApplyTrayTooltip(string tooltip)
    {
        if (string.Equals(_trayIcon.Tooltip, tooltip, StringComparison.Ordinal))
            _trayIcon.Tooltip = string.Empty;

        _trayIcon.Tooltip = tooltip;
    }

    private string BuildTrayTooltip() =>
        new TrayTooltipBuilder(_captureSnapshot()).Build();
}
