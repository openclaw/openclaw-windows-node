using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace OpenClawTray.Services;

internal sealed record AppNotificationPublishRequest(
    AppNotification? AppNotification = null,
    ToastContentBuilder? ToastBuilder = null,
    string? ToastTag = null,
    string? ToastDeviceId = null);

internal interface IToastNotificationPublisher
{
    void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null);
}

internal static class AppNotificationPublisher
{
    public static void Publish(
        AppNotificationService? appNotificationService,
        IToastNotificationPublisher? toastService,
        AppNotificationPublishRequest request)
    {
        Action<ToastContentBuilder, string?, string?>? showToast = toastService is null
            ? null
            : (builder, tag, deviceId) => toastService.ShowToast(builder, tag, deviceId);
        Publish(appNotificationService, showToast, request);
    }

    internal static void Publish(
        AppNotificationService? appNotificationService,
        Action<ToastContentBuilder, string?, string?>? showToast,
        AppNotificationPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AppNotification is null && request.ToastBuilder is null)
            throw new ArgumentException("Publish request must include an app notification, a toast builder, or both.", nameof(request));

        List<Exception>? failures = null;

        if (request.ToastBuilder is not null && showToast is not null)
        {
            try
            {
                showToast(request.ToastBuilder, request.ToastTag, request.ToastDeviceId);
            }
            catch (Exception ex)
            {
                (failures ??= new()).Add(ex);
            }
        }

        if (request.AppNotification is not null && appNotificationService is not null)
        {
            try
            {
                appNotificationService.Show(request.AppNotification);
            }
            catch (Exception ex)
            {
                (failures ??= new()).Add(ex);
            }
        }

        if (failures is null)
            return;

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        throw new AggregateException(failures);
    }

    public static void Show(
        AppNotificationService? service,
        string title,
        string message,
        string source,
        string category,
        AppNotificationSeverity severity,
        string dedupeKey,
        string actionRoute,
        string actionLabel,
        string? id = null)
    {
        if (service is null)
            return;

        service.Show(new AppNotification
        {
            Id = id ?? "",
            Title = title,
            Message = message,
            Source = source,
            Category = category,
            Severity = severity,
            DedupeKey = dedupeKey,
            ActionRoute = actionRoute,
            ActionLabel = actionLabel
        });
    }
}
