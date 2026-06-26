using Microsoft.UI.Xaml;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.ViewModels;

/// <summary>
/// Presentation model for a single app notification card. Shared by the
/// full <c>NotificationsPage</c> and the title-bar notifications bell flyout
/// so severity glyphs and metadata localization stay defined in one place.
/// </summary>
internal sealed record NotificationItemViewModel(
    string Id,
    string SeverityGlyph,
    string Title,
    string Message,
    string Metadata,
    string? ActionLabel,
    string? ActionRoute,
    Visibility ActionVisibility,
    string OccurrenceText,
    Visibility OccurrenceVisibility,
    string DismissAutomationName)
{
    public static NotificationItemViewModel From(AppNotification notification)
    {
        var metadata = new[]
            {
                LocalizeMetadataValue(notification.Source),
                LocalizeMetadataValue(notification.Category),
                notification.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            }
            .Where(value => !string.IsNullOrWhiteSpace(value));

        var occurrenceText = LocalizationHelper.Format(
            "AppNotification_RepeatedBadgeFormat",
            notification.OccurrenceCount);

        return new(
            notification.Id,
            ToSeverityGlyph(notification.Severity),
            notification.Title,
            notification.Message,
            string.Join(" - ", metadata),
            notification.ActionLabel,
            notification.ActionRoute,
            !string.IsNullOrWhiteSpace(notification.ActionLabel) &&
            !string.IsNullOrWhiteSpace(notification.ActionRoute)
                ? Visibility.Visible
                : Visibility.Collapsed,
            occurrenceText,
            notification.OccurrenceCount > 1 ? Visibility.Visible : Visibility.Collapsed,
            LocalizationHelper.Format("NotificationsPage_DismissAutomationNameFormat", notification.Title));
    }

    private static string ToSeverityGlyph(AppNotificationSeverity severity) => severity switch
    {
        AppNotificationSeverity.Success => "\uE930",
        AppNotificationSeverity.Warning => "\uE7BA",
        AppNotificationSeverity.Error => "\uE783",
        _ => "\uE946"
    };

    private static string? LocalizeMetadataValue(string? value) => value switch
    {
        null or "" => null,
        "authentication" => LocalizationHelper.GetString("NotificationsPage_MetadataAuthentication"),
        "bindings" => LocalizationHelper.GetString("NotificationsPage_MetadataBindings"),
        "channels" => LocalizationHelper.GetString("NotificationsPage_MetadataChannels"),
        "config" => LocalizationHelper.GetString("NotificationsPage_MetadataConfig"),
        "connection" => LocalizationHelper.GetString("NotificationsPage_MetadataConnection"),
        "cron" => LocalizationHelper.GetString("NotificationsPage_MetadataCron"),
        "exec-approval" => LocalizationHelper.GetString("NotificationsPage_MetadataSourceExecApproval"),
        "gateway" => LocalizationHelper.GetString("NotificationsPage_MetadataGateway"),
        "jobs" => LocalizationHelper.GetString("NotificationsPage_MetadataJobs"),
        "load" => LocalizationHelper.GetString("NotificationsPage_MetadataLoad"),
        "lifecycle" => LocalizationHelper.GetString("NotificationsPage_MetadataLifecycle"),
        "local-gateway" => LocalizationHelper.GetString("NotificationsPage_MetadataLocalGateway"),
        "node.invoke" => LocalizationHelper.GetString("NotificationsPage_MetadataCategoryNodeInvoke"),
        "node" => LocalizationHelper.GetString("NotificationsPage_MetadataNode"),
        "pairing" => LocalizationHelper.GetString("NotificationsPage_MetadataPairing"),
        "sandbox" => LocalizationHelper.GetString("NotificationsPage_MetadataSandbox"),
        "settings" => LocalizationHelper.GetString("NotificationsPage_MetadataSettings"),
        "status" => LocalizationHelper.GetString("NotificationsPage_MetadataStatus"),
        "system.run" => LocalizationHelper.GetString("NotificationsPage_MetadataSystemRun"),
        _ => value
    };
}
