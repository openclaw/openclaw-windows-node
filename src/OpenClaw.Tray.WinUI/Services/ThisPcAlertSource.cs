// Keeps the Inbox's "This PC" rows in step with Windows privacy settings: publishes one
// System-tab warning per blocked device that an enabled node capability needs, withdraws it
// when the block clears or the capability is turned off, and re-reads on settings saves and
// Windows access-change events. A dismissed row stays dismissed until its block clears or
// changes kind. Device access is read through IDeviceAccessReader, which never prompts.
//
// Usage:
//   var thisPc = new ThisPcAlertSource(appNotifications, settingsStore,
//       WindowsDeviceAccessReader.Create(), LocalizationHelper.GetString);
//   thisPc.Refresh();
//   ...
//   if (ThisPcAlertSource.TryOpenPrivacySettings(route)) return;   // Inbox and banner routes
//   thisPc.Dispose();

using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Diagnostics;

namespace OpenClawTray.Services;

/// <summary>Reads Windows device access without prompting and reports access changes on any thread.</summary>
internal interface IDeviceAccessReader : IDisposable
{
    ThisPcDeviceAccess Read(ThisPcDevice device);

    event EventHandler? Changed;
}

internal sealed class ThisPcAlertSource : IDisposable
{
    private readonly AppNotificationService _notifications;
    private readonly ISettingsStore _settings;
    private readonly IDeviceAccessReader _reader;
    private readonly Func<string, string> _getString;
    private readonly object _gate = new();
    private readonly Dictionary<ThisPcDevice, ThisPcDeviceAccess> _published = new();
    private bool _disposed;

    public ThisPcAlertSource(
        AppNotificationService notifications,
        ISettingsStore settings,
        IDeviceAccessReader reader,
        Func<string, string> getString)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _settings.Changed += OnInputsChanged;
        _reader.Changed += OnInputsChanged;
    }

    /// <summary>Recomputes the blocked devices and reconciles the Inbox rows. Any thread.</summary>
    public void Refresh()
    {
        // Rows are reconciled under the lock so overlapping refreshes cannot reorder show and withdraw.
        lock (_gate)
        {
            if (_disposed)
                return;
            var alerts = ThisPcAlerts.Build(ThisPcCapabilities.From(_settings.Current), _reader.Read);
            foreach (var device in _published.Keys.ToList())
            {
                if (alerts.Any(alert => alert.Device == device && alert.Access == _published[device]))
                    continue;
                _published.Remove(device);
                _notifications.DismissByDedupeKey(ThisPcAlerts.DedupeKey(device));
            }
            foreach (var alert in alerts)
            {
                if (_published.ContainsKey(alert.Device))
                    continue;
                _published[alert.Device] = alert.Access;
                _notifications.Show(ThisPcAlerts.ToNotification(alert, _getString));
            }
        }
    }

    /// <summary>Opens a "This PC" privacy settings route; false for every other route.</summary>
    public static bool TryOpenPrivacySettings(string? route)
    {
        if (!ThisPcAlerts.IsPrivacySettingsRoute(route))
            return false;
        // Fixture runs never reach host apps; the synthetic row's action is a no-op there.
        if (GatewayFixtureIsolation.IsEnabled)
        {
            Logger.Info("[ThisPc] privacy settings launch suppressed in fixture mode");
            return true;
        }
        try
        {
            Process.Start(new ProcessStartInfo(route!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ThisPc] privacy settings could not be opened: {ex.GetType().Name}");
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _settings.Changed -= OnInputsChanged;
        _reader.Changed -= OnInputsChanged;
        _reader.Dispose();
    }

    private void OnInputsChanged(object? sender, EventArgs e)
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ThisPc] refresh failed: {ex.GetType().Name}");
        }
    }
}
