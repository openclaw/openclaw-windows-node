using Microsoft.UI.Xaml;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace OpenClawTray.ViewModels;

/// <summary>
/// Presentation model for one Inbox row, shared by the footer bell flyout and the
/// full Inbox page through <c>InboxView</c>. Rows are reconciled in place with
/// <see cref="UpdateFrom"/> so expansion and keyboard focus survive updates.
/// </summary>
internal sealed class InboxItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private DateTimeOffset _createdAt;

    private InboxItemViewModel(string id) => Id = id;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public InboxEntryKind Kind { get; private set; }
    public bool CanDismiss { get; private set; }
    public string Glyph { get; private set; } = "";
    public Visibility ErrorIconVisibility { get; private set; }
    public Visibility WarningIconVisibility { get; private set; }
    public Visibility NeutralIconVisibility { get; private set; }
    public string Title { get; private set; } = "";
    public string State { get; private set; } = "";
    public string Details { get; private set; } = "";
    public string Metadata { get; private set; } = "";
    public string AgeText { get; private set; } = "";
    public string CreatedText { get; private set; } = "";
    public string? ActionLabel { get; private set; }
    public string? ActionRoute { get; private set; }
    public Visibility ActionVisibility { get; private set; }
    public string OccurrenceText { get; private set; } = "";
    public Visibility OccurrenceVisibility { get; private set; }
    public Visibility DismissVisibility { get; private set; }
    public string DismissAutomationName { get; private set; } = "";
    public string RowAutomationName { get; private set; } = "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    // ListViewItem derives its default accessible name from the item string.
    public override string ToString() => RowAutomationName;

    public static InboxItemViewModel From(InboxEntry entry, DateTimeOffset now)
    {
        var item = new InboxItemViewModel(entry.Id);
        item.Apply(entry, now, raise: false);
        return item;
    }

    /// <summary>Copies a newer projection of the same entry, raising change notifications.</summary>
    public void UpdateFrom(InboxEntry entry, DateTimeOffset now)
    {
        if (!string.Equals(entry.Id, Id, StringComparison.Ordinal))
            throw new ArgumentException("Inbox rows can only be updated from the same entry.", nameof(entry));
        Apply(entry, now, raise: true);
    }

    /// <summary>Advances the relative age without replacing the row or its expansion state.</summary>
    public void RefreshClock(DateTimeOffset now)
    {
        var age = FormatAge(InboxProjection.Age(_createdAt, now), _createdAt);
        if (!string.Equals(age, AgeText, StringComparison.Ordinal))
        {
            AgeText = age;
            OnPropertyChanged(nameof(AgeText));
        }
    }

    private void Apply(InboxEntry entry, DateTimeOffset now, bool raise)
    {
        var notification = entry.Notification;
        var hasAction = !string.IsNullOrWhiteSpace(notification.ActionLabel) &&
            !string.IsNullOrWhiteSpace(notification.ActionRoute);
        var state = FirstLine(notification.Message);

        Kind = entry.Kind;
        CanDismiss = entry.CanDismiss;
        Glyph = ToGlyph(entry);
        ErrorIconVisibility = Show(entry.Severity == InboxSeverity.Error);
        WarningIconVisibility = Show(entry.Severity == InboxSeverity.Warning);
        NeutralIconVisibility = Show(entry.Severity == InboxSeverity.Neutral);
        Title = notification.Title;
        State = state;
        Details = notification.Message;
        Metadata = string.Join(" - ", new[]
            {
                LocalizeMetadataValue(notification.Source),
                LocalizeMetadataValue(notification.Category),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        _createdAt = notification.CreatedAt;
        AgeText = FormatAge(InboxProjection.Age(_createdAt, now), _createdAt);
        CreatedText = notification.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        ActionLabel = hasAction ? notification.ActionLabel : null;
        ActionRoute = hasAction ? notification.ActionRoute : null;
        ActionVisibility = Show(hasAction);
        OccurrenceText = LocalizationHelper.Format("AppNotification_RepeatedBadgeFormat", notification.OccurrenceCount);
        OccurrenceVisibility = Show(notification.OccurrenceCount > 1);
        DismissVisibility = Show(entry.CanDismiss);
        DismissAutomationName = LocalizationHelper.Format("Inbox_DismissItemFormat", notification.Title);
        RowAutomationName = string.IsNullOrWhiteSpace(state) || string.Equals(state, notification.Title, StringComparison.Ordinal)
            ? notification.Title
            : LocalizationHelper.Format("Inbox_RowAutomationNameFormat", notification.Title, state);

        if (raise)
            OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static string FirstLine(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline].TrimEnd();
    }

    private static string FormatAge(InboxAge age, DateTimeOffset createdAt) => age.Unit switch
    {
        InboxAgeUnit.Now => LocalizationHelper.GetString("Inbox_AgeNow"),
        InboxAgeUnit.Minutes => LocalizationHelper.Format("Inbox_AgeMinutesFormat", age.Value),
        InboxAgeUnit.Hours => LocalizationHelper.Format("Inbox_AgeHoursFormat", age.Value),
        InboxAgeUnit.Days => LocalizationHelper.Format("Inbox_AgeDaysFormat", age.Value),
        _ => createdAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
    };

    private static string ToGlyph(InboxEntry entry) => entry.Kind switch
    {
        InboxEntryKind.Approval => FluentIconCatalog.Permissions,
        InboxEntryKind.Outbox => FluentIconCatalog.Chat,
        InboxEntryKind.UpdateError or InboxEntryKind.UpdateWarning => FluentIconCatalog.Unarchive,
        InboxEntryKind.AccessRequest => FluentIconCatalog.Key,
        InboxEntryKind.Mention => FluentIconCatalog.Chat,
        _ when entry.Category == InboxCategory.Automations => FluentIconCatalog.Cron,
        _ when string.Equals(entry.Notification.Source, "connection", StringComparison.OrdinalIgnoreCase) => FluentIconCatalog.Plug,
        InboxEntryKind.Error => FluentIconCatalog.StatusErr,
        _ => FluentIconCatalog.StatusWarn,
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
