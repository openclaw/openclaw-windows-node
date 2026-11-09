using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Toast notification display with dedup and sound configuration.
/// Extracted from App.xaml.cs to group toast-related state and logic.
/// </summary>
internal sealed class ToastService : IToastNotificationPublisher
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

    /// <summary>
    /// Shows a toast with optional dedup by tag + device ID. Do not disturb silences every
    /// <see cref="AlertKind.Standard"/> toast; approvals still come through.
    /// </summary>
    public void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null, AlertKind alertKind = AlertKind.Standard)
    {
        // Showing a toast also lazily initializes the toolkit's OS registration.
        if (GatewayFixtureIsolation.IsEnabled)
            return;

        if (AppIdentity.IsIsolated)
        {
            Logger.Info("Windows toast suppressed for an isolated app instance.");
            return;
        }

        if (!DoNotDisturbPolicy.AllowsAlert(_getSettings()?.DoNotDisturb == true, alertKind))
        {
            Logger.Info("Windows toast suppressed by Do not disturb.");
            return;
        }

        if (!ShouldShowToast(toastTag, deviceId))
            return;

        ApplySound(builder);
        builder.Show();
    }

    /// <summary>
    /// Shows an approval toast the app withdraws by <paramref name="tag"/>/<paramref name="group"/>
    /// once it is decided; Windows also drops it at the approval's expiry. False when suppressed.
    /// </summary>
    public bool ShowWithdrawableToast(ApprovalToastPlan plan, string tag, string group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (GatewayFixtureIsolation.IsEnabled || AppIdentity.IsIsolated || plan.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        var builder = CreateApprovalContent(plan);
        ApplySound(builder);
        builder.Show(toast =>
        {
            toast.Tag = tag;
            toast.Group = group;
            toast.ExpirationTime = plan.ExpiresAt;
        });
        return true;
    }

    /// <summary>Materializes the approval plan through the shipping toast toolkit before delivery.</summary>
    internal static ToastContentBuilder CreateApprovalContent(ApprovalToastPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new ToastContentBuilder().AddText(plan.Title);
        foreach (var line in plan.Lines)
            builder.AddText(line);
        foreach (var (key, value) in plan.LaunchArguments)
            builder.AddArgument(key, value);
        foreach (var planned in plan.Buttons)
        {
            var button = new ToastButton().SetContent(planned.Label);
            foreach (var (key, value) in planned.Arguments)
                button.AddArgument(key, value);
            builder.AddButton(button);
        }
        return builder;
    }

    /// <summary>Removes a toast shown by <see cref="ShowWithdrawableToast"/> from the screen and Action Center.</summary>
    public void RemoveToast(string tag, string group)
    {
        if (GatewayFixtureIsolation.IsEnabled || AppIdentity.IsIsolated)
            return;
        ToastNotificationManagerCompat.History.Remove(tag, group);
    }

    private void ApplySound(ToastContentBuilder builder)
    {
        var sound = _getSettings()?.NotificationSound;
        if (string.Equals(sound, "None", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new ToastAudio { Silent = true });
        }
        else if (string.Equals(sound, "Subtle", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new Uri("ms-winsoundevent:Notification.IM"), silent: false);
        }
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
