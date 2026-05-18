using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Toast notification display with dedup and sound configuration.
/// Extracted from App.xaml.cs to group toast-related state and logic.
/// </summary>
internal sealed class ToastService
{
    private readonly Func<SettingsManager?> _getSettings;
    private readonly Dictionary<string, DateTime> _recentToastKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shownPairedToasts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private static readonly TimeSpan ToastDedupeWindow = TimeSpan.FromSeconds(30);

    public ToastService(Func<SettingsManager?> getSettings)
    {
        _getSettings = getSettings;
    }

    /// <summary>Shows a toast with optional dedup by tag + device ID.</summary>
    public void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null)
    {
        if (!ShouldShowToast(toastTag, deviceId))
            return;

        var sound = _getSettings()?.NotificationSound;
        if (string.Equals(sound, "None", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new ToastAudio { Silent = true });
        }
        else if (string.Equals(sound, "Subtle", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new Uri("ms-winsoundevent:Notification.IM"), silent: false);
        }
        builder.Show();
    }

    /// <summary>Check whether a toast with this tag was recently shown (within dedup window).</summary>
    public bool HasRecentToast(string toastTag, string? deviceId)
    {
        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        lock (_gate)
        {
            return _recentToastKeys.TryGetValue(BuildToastKey(toastTag, normalizedDeviceId), out var lastShown) &&
                DateTime.UtcNow - lastShown < ToastDedupeWindow;
        }
    }

    /// <summary>
    /// Per-device idempotency for "Node paired" toast. Prevents duplicate
    /// toasts when PairingStatusChanged(Paired) re-fires on WS reconnect.
    /// </summary>
    public bool HasShownPairedToast(string deviceId)
    {
        lock (_gate)
        {
            return _shownPairedToasts.Contains(deviceId);
        }
    }

    public void MarkPairedToastShown(string deviceId)
    {
        lock (_gate)
        {
            _shownPairedToasts.Add(deviceId);
        }
    }

    private bool ShouldShowToast(string? toastTag, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(toastTag))
            return true;

        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        var dedupeKey = BuildToastKey(toastTag, normalizedDeviceId);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            foreach (var staleKey in _recentToastKeys
                .Where(pair => now - pair.Value >= ToastDedupeWindow)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _recentToastKeys.Remove(staleKey);
            }

            if (_recentToastKeys.TryGetValue(dedupeKey, out var lastShown) &&
                now - lastShown < ToastDedupeWindow)
            {
                Logger.Info($"[ToastDeduper] Suppressed duplicate toast tag={toastTag} deviceId={normalizedDeviceId}");
                return false;
            }

            _recentToastKeys[dedupeKey] = now;
        }

        Logger.Info($"[ToastDeduper] Showing toast tag={toastTag} deviceId={normalizedDeviceId}");
        return true;
    }

    private static string NormalizeToastDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? "global" : deviceId.Trim();

    private static string BuildToastKey(string toastTag, string normalizedDeviceId) =>
        $"{toastTag.Trim()}:{normalizedDeviceId}";
}
