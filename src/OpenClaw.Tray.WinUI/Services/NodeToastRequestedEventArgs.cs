using Microsoft.Toolkit.Uwp.Notifications;
using System;

namespace OpenClawTray.Services;

public sealed class NodeToastRequestedEventArgs(
    ToastContentBuilder toastBuilder,
    AppNotification? appNotification = null,
    string? toastTag = null,
    string? toastDeviceId = null) : EventArgs
{
    public ToastContentBuilder ToastBuilder { get; } = toastBuilder ?? throw new ArgumentNullException(nameof(toastBuilder));
    public AppNotification? AppNotification { get; } = appNotification;
    public string? ToastTag { get; } = toastTag;
    public string? ToastDeviceId { get; } = toastDeviceId;
}
