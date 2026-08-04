using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using WinUIEx;

namespace OpenClawTray.Services;

internal sealed record TrayControllerCallbacks(
    Func<TrayMenuSnapshot> CaptureMenuSnapshot,
    Func<TrayStateSnapshot> CaptureIconSnapshot,
    Func<bool> IsOperatorConnected,
    Action ShowChat,
    Action ShowConnection,
    Action<string> DispatchMenuAction,
    Action<Window> ApplyTheme,
    Func<bool> IsDispatcherAvailable,
    Func<bool> HasThreadAccess,
    Action<DispatcherQueueHandler> Marshal,
    Action<string, Exception> LogCrash);

internal sealed class TrayController : ITrayController
{
    private readonly TrayControllerCallbacks _callbacks;
    private TrayIcon? _trayIcon;
    private TrayIconCoordinator? _trayIconCoordinator;
    private TrayMenuWindow? _trayMenuWindow;
    private WeakReference<ToggleSwitch>? _connectionToggleRef;
    private bool _suspendConnectionToggleEvent;
    private bool _initialized;
    private bool _isClosing;
    private bool _disposed;

    internal TrayController(TrayControllerCallbacks callbacks)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized || _isClosing)
            return;

        InitializeTrayMenuWindow();

        var iconPath = StatusBadgeIconFactory.GetBadgedIconPath(ConnectionStatusAccent.Neutral);
        _trayIcon = new TrayIcon(1, iconPath, BuildTrayTooltip());
        _trayIconCoordinator = new TrayIconCoordinator(
            _trayIcon,
            _callbacks.HasThreadAccess,
            _callbacks.Marshal,
            _callbacks.CaptureIconSnapshot,
            () => !_disposed && _trayIcon != null);
        _trayIcon.IsVisible = true;
        _trayIconCoordinator.ApplyTrayTooltip(BuildTrayTooltip());
        _trayIcon.Selected += OnTrayIconSelected;
        _trayIcon.ContextMenu += OnTrayContextMenu;
        _initialized = true;
    }

    public void BeginShutdown() => _isClosing = true;

    public void RefreshIcon()
    {
        if (_disposed || _isClosing)
            return;

        _trayIconCoordinator?.UpdateTrayIcon();
    }

    public void ApplyConnectionState(ConnectionStatus status, OverallConnectionState? overallState)
    {
        if (_disposed || _isClosing)
            return;

        RefreshIcon();
        SyncConnectionToggle(status, overallState);
        if (status is ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error)
            HideMenu();
    }

    public void HideMenu()
    {
        if (_disposed || _isClosing)
            return;

        _trayMenuWindow?.HideCascade();
    }

    public void ApplyTheme()
    {
        if (_disposed || _isClosing || _trayMenuWindow == null)
            return;

        _callbacks.ApplyTheme(_trayMenuWindow);
    }

    public void CloseMenuForShutdown()
    {
        BeginShutdown();
        var menu = _trayMenuWindow;
        if (menu == null)
            return;

        _trayMenuWindow = null;
        _connectionToggleRef = null;
        menu.MenuItemClicked -= OnTrayMenuItemClicked;
        menu.CloseCascadeForShutdown();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        List<Exception>? failures = null;
        try
        {
            CloseMenuForShutdown();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        var icon = _trayIcon;
        _trayIcon = null;
        _trayIconCoordinator = null;
        if (icon != null)
        {
            try
            {
                icon.Selected -= OnTrayIconSelected;
                icon.ContextMenu -= OnTrayContextMenu;
                icon.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        Logger.Info("[TrayController] Disposed owned tray resources");
        if (failures is { Count: > 0 })
        {
            throw new AggregateException("One or more owned tray resources failed to close.", failures);
        }
    }

    private void InitializeTrayMenuWindow()
    {
        var menu = new TrayMenuWindow();
        _callbacks.ApplyTheme(menu);
        menu.MenuItemClicked += OnTrayMenuItemClicked;
        _trayMenuWindow = menu;
    }

    private void OnTrayIconSelected(TrayIcon sender, TrayIconEventArgs args)
    {
        if (_isClosing || _disposed)
            return;

        if (_callbacks.IsOperatorConnected())
            _callbacks.ShowChat();
        else
            _callbacks.ShowConnection();
    }

    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs args) => ShowMenu();

    public void ShowMenu()
    {
        if (_disposed || _isClosing)
            return;

        try
        {
            if (!_callbacks.IsDispatcherAvailable())
            {
                Logger.Error("DispatcherQueue is null - cannot show menu");
                return;
            }

            if (_trayMenuWindow == null)
                InitializeTrayMenuWindow();

            var menu = _trayMenuWindow!;
            _connectionToggleRef = null;
            menu.ClearItems();
            BuildTrayMenuPopup(menu);
            menu.ShowAtCursor();
        }
        catch (Exception ex)
        {
            _callbacks.LogCrash("ShowTrayMenuPopup", ex);
            Logger.Error($"Failed to show tray menu: {ex.Message}");
        }
    }

    private void BuildTrayMenuPopup(TrayMenuWindow menu)
    {
        var snapshot = _callbacks.CaptureMenuSnapshot();
        var presentation = new TrayMenuPresenter(snapshot).Present();
        var callbacks = new TrayMenuCallbacks(
            DispatchAction: _callbacks.DispatchMenuAction,
            TrackConnectionToggle: toggle => _connectionToggleRef = new WeakReference<ToggleSwitch>(toggle),
            IsConnectionToggleSuspended: () => _suspendConnectionToggleEvent);
        var renderer = new TrayMenuRenderer(presentation, callbacks);

        menu.BeginUpdate();
        try
        {
            renderer.Render(menu);
        }
        finally
        {
            menu.EndUpdate();
        }
    }

    private string BuildTrayTooltip() =>
        new TrayTooltipBuilder(_callbacks.CaptureIconSnapshot()).Build();

    private void SyncConnectionToggle(ConnectionStatus status, OverallConnectionState? overallState)
    {
        if (_connectionToggleRef == null ||
            !_connectionToggleRef.TryGetTarget(out var toggle))
        {
            return;
        }

        if (toggle.XamlRoot == null)
        {
            _connectionToggleRef = null;
            return;
        }

        var presentation = ConnectionTogglePresenter.Present(status, overallState);
        _suspendConnectionToggleEvent = true;
        try
        {
            TrayMenuWindow.SetMenuToggleSwitchState(toggle, presentation.IsOn, presentation.IsEnabled);
            ToolTipService.SetToolTip(toggle, presentation.ToolTip);
        }
        finally
        {
            _suspendConnectionToggleEvent = false;
        }
    }

    private void OnTrayMenuItemClicked(object? sender, string action)
    {
        if (!_isClosing && !_disposed)
            _callbacks.DispatchMenuAction(action);
    }
}
