using Microsoft.UI.Xaml;
using OpenClaw.Shared;
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
    private DateTimeOffset? _expiresAt;

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
    public bool OpensDashboard { get; private set; }
    public Visibility ActionVisibility { get; private set; }
    /// <summary>Prompt sent to OpenClaw by "Ask OpenClaw" to explain a failure; null when not offered.</summary>
    public string? AskQuestion { get; private set; }
    public Visibility AskVisibility { get; private set; }
    /// <summary>Link to the native page that fixes the item (Automations, Models).</summary>
    public string? SecondaryActionLabel { get; private set; }
    public string? SecondaryActionRoute { get; private set; }
    public Visibility SecondaryActionVisibility { get; private set; }
    public Visibility DetailsVisibility { get; private set; }
    public string CommandText { get; private set; } = "";
    public Visibility CommandVisibility { get; private set; }
    public string ScopeText { get; private set; } = "";
    public Visibility ScopeVisibility { get; private set; }
    public string WarningText { get; private set; } = "";
    public Visibility WarningVisibility { get; private set; }
    public string ExpiryText { get; private set; } = "";
    public Visibility ExpiryVisibility { get; private set; }
    public bool HasCountdown => _expiresAt is not null;
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

    /// <summary>Dispatches a row action without interpreting Gateway text as a URL or native route.</summary>
    public void OpenAction(Action<string> openRoute, Action openDashboard)
    {
        if (OpensDashboard)
            openDashboard();
        else if (!string.IsNullOrWhiteSpace(ActionRoute))
            openRoute(ActionRoute);
    }

    /// <summary>Copies a newer projection of the same entry, raising change notifications.</summary>
    public void UpdateFrom(InboxEntry entry, DateTimeOffset now)
    {
        if (!string.Equals(entry.Id, Id, StringComparison.Ordinal))
            throw new ArgumentException("Inbox rows can only be updated from the same entry.", nameof(entry));
        Apply(entry, now, raise: true);
    }

    /// <summary>Advances the relative age and approval countdown without reprojecting the row.</summary>
    public void RefreshClock(DateTimeOffset now)
    {
        var age = FormatAge(InboxProjection.Age(_createdAt, now), _createdAt);
        if (!string.Equals(age, AgeText, StringComparison.Ordinal))
        {
            AgeText = age;
            OnPropertyChanged(nameof(AgeText));
        }
        if (_expiresAt is { } expiresAt)
        {
            var expiry = FormatExpiry(expiresAt, now);
            if (!string.Equals(expiry, ExpiryText, StringComparison.Ordinal))
            {
                ExpiryText = expiry;
                OnPropertyChanged(nameof(ExpiryText));
            }
        }
    }

    private void Apply(InboxEntry entry, DateTimeOffset now, bool raise)
    {
        var content = entry.GatewayItem is { } item ? FromGatewayItem(item) : FromNotification(entry.Notification!);
        var state = content.State;

        _createdAt = entry.CreatedAt;
        _expiresAt = content.ExpiresAt;
        Kind = entry.Kind;
        CanDismiss = entry.CanDismiss;
        Glyph = ToGlyph(entry);
        ErrorIconVisibility = Show(entry.Severity == InboxSeverity.Error);
        WarningIconVisibility = Show(entry.Severity == InboxSeverity.Warning);
        NeutralIconVisibility = Show(entry.Severity == InboxSeverity.Neutral);
        Title = content.Title;
        State = state;
        Details = content.Details;
        DetailsVisibility = Show(!string.IsNullOrWhiteSpace(content.Details));
        Metadata = content.Metadata;
        AgeText = FormatAge(InboxProjection.Age(entry.CreatedAt, now), entry.CreatedAt);
        CreatedText = entry.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        OpensDashboard = content.OpensDashboard;
        var hasAction = !string.IsNullOrWhiteSpace(content.ActionLabel) &&
            (OpensDashboard || !string.IsNullOrWhiteSpace(content.ActionRoute));
        ActionLabel = hasAction ? content.ActionLabel : null;
        ActionRoute = hasAction ? content.ActionRoute : null;
        ActionVisibility = Show(hasAction);
        AskQuestion = content.AskQuestion;
        AskVisibility = Show(content.AskQuestion is not null);
        SecondaryActionLabel = content.SecondaryLabel;
        SecondaryActionRoute = content.SecondaryRoute;
        SecondaryActionVisibility = Show(content.SecondaryLabel is not null && content.SecondaryRoute is not null);
        CommandText = content.Command ?? "";
        CommandVisibility = Show(content.Command is not null);
        ScopeText = content.Scope ?? "";
        ScopeVisibility = Show(content.Scope is not null);
        WarningText = content.Warning ?? "";
        WarningVisibility = Show(content.Warning is not null);
        ExpiryText = content.ExpiresAt is { } expiresAt ? FormatExpiry(expiresAt, now) : "";
        ExpiryVisibility = Show(content.ExpiresAt is not null);
        OccurrenceText = LocalizationHelper.Format("AppNotification_RepeatedBadgeFormat", content.OccurrenceCount);
        OccurrenceVisibility = Show(content.OccurrenceCount > 1);
        DismissVisibility = Show(entry.CanDismiss);
        DismissAutomationName = LocalizationHelper.Format("Inbox_DismissItemFormat", content.Title);
        RowAutomationName = string.IsNullOrWhiteSpace(state) || string.Equals(state, content.Title, StringComparison.Ordinal)
            ? content.Title
            : LocalizationHelper.Format("Inbox_RowAutomationNameFormat", content.Title, state);

        if (raise)
            OnPropertyChanged(string.Empty);
    }

    private sealed record RowContent(string Title, string State, string Details, string Metadata)
    {
        public string? ActionLabel { get; init; }
        public string? ActionRoute { get; init; }
        public bool OpensDashboard { get; init; }
        public string? AskQuestion { get; init; }
        public string? SecondaryLabel { get; init; }
        public string? SecondaryRoute { get; init; }
        public string? Command { get; init; }
        public string? Scope { get; init; }
        public string? Warning { get; init; }
        /// <summary>Local-clock approval deadline (see InboxGatewayItem.ExpiresAt).</summary>
        public DateTimeOffset? ExpiresAt { get; init; }
        public int OccurrenceCount { get; init; } = 1;
    }

    private static RowContent FromNotification(AppNotification notification) =>
        new(notification.Title, FirstLine(notification.Message), notification.Message,
            string.Join(" - ", new[]
                {
                    LocalizeMetadataValue(notification.Source),
                    LocalizeMetadataValue(notification.Category),
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))))
        {
            ActionLabel = notification.ActionLabel,
            ActionRoute = notification.ActionRoute,
            OccurrenceCount = notification.OccurrenceCount,
        };

    // Gateway text (commands, titles, errors) is shown as plain, selectable text only.
    private static RowContent FromGatewayItem(InboxGatewayItem item) => item.Kind switch
    {
        InboxGatewayItemKind.Approval when item.Approval is { } approval => FromApproval(approval, item.ExpiresAt),
        InboxGatewayItemKind.AutomationFailed => new(item.Label, LocalizationHelper.GetString("Inbox_AutomationFailed"), item.Detail ?? "", "")
        {
            AskQuestion = LocalizationHelper.Format("Inbox_AskAutomationFailedFormat", item.Label),
            SecondaryLabel = LocalizationHelper.GetString("Inbox_OpenAutomations"),
            SecondaryRoute = InboxRoutes.Automations,
        },
        InboxGatewayItemKind.AutomationOverdue => new(item.Label, LocalizationHelper.GetString("Inbox_AutomationOverdue"), "", "")
        {
            AskQuestion = LocalizationHelper.Format("Inbox_AskAutomationOverdueFormat", item.Label,
                (item.DueAt ?? item.ArrivedAt).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            SecondaryLabel = LocalizationHelper.GetString("Inbox_OpenAutomations"),
            SecondaryRoute = InboxRoutes.Automations,
        },
        InboxGatewayItemKind.ModelAuth when item.ModelAuth is { } auth => FromModelAuth(auth),
        _ => new(item.Label, "", "", ""),
    };

    private static RowContent FromApproval(GatewayPendingApproval approval, DateTimeOffset? expiresAt)
    {
        var title = LocalizationHelper.GetString(approval.Kind switch
        {
            GatewayApprovalKind.Exec => "Inbox_ApprovalExecTitle",
            GatewayApprovalKind.Plugin => "Inbox_ApprovalPluginTitle",
            _ => "Inbox_ApprovalTitle",
        });
        var isExec = approval.Kind == GatewayApprovalKind.Exec;
        var fullTitle = InboxGatewayItems.DisplayCommand(approval.Title ?? approval.Command);
        var state = isExec ? InboxGatewayItems.CompactCommand(approval.Command) : fullTitle;
        // The collapsed state is ellipsized. Keep the entire non-exec title in the expanded
        // plain-text detail even when the optional description is missing.
        var details = isExec ? "" : string.Join("\n\n", new[]
        {
            fullTitle, InboxGatewayItems.DisplayCommand(approval.Description),
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
        var metadata = string.Join(" · ", new[] { HostLabel(approval.Host), InboxGatewayItems.DisplayCommand(approval.Cwd) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(title, state, details, metadata)
        {
            ActionLabel = LocalizationHelper.GetString(isExec ? "Inbox_OpenChat" : "Menu_OpenDashboard"),
            // Native chat translates only exec approvals. Non-exec requests use the existing
            // Dashboard launcher at its home, never a URL/session/route from the request.
            OpensDashboard = !isExec,
            ActionRoute = !isExec ? null : string.IsNullOrWhiteSpace(approval.SessionKey)
                ? "chat"
                : AppNotificationActionRoutes.Chat(approval.SessionKey),
            // The full command, never trimmed, so what is reviewed is what would run.
            Command = approval.Kind == GatewayApprovalKind.Plugin ? null : InboxGatewayItems.DisplayCommand(approval.Command),
            Scope = approval.Scope is { } scope ? ScopeLabel(scope) : null,
            Warning = string.IsNullOrWhiteSpace(approval.WarningText) ? null : InboxGatewayItems.DisplayCommand(approval.WarningText),
            ExpiresAt = expiresAt,
        };
    }

    private static RowContent FromModelAuth(InboxModelAuthAttention auth)
    {
        var status = LocalizationHelper.GetString(auth.Repair switch
        {
            InboxModelAuthRepair.SignIn => "Inbox_ModelAuthSignInRequired",
            InboxModelAuthRepair.ApiKey => "Inbox_ModelAuthApiKeyRequired",
            _ => "Inbox_ModelAuthNeedsAttention",
        });
        var state = string.IsNullOrWhiteSpace(auth.Scope) ? status : $"{status} · {auth.Scope}";
        return new(LocalizationHelper.Format("Inbox_ModelAuthTitleFormat", auth.DisplayName), state, "", "")
        {
            AskQuestion = LocalizationHelper.Format("Inbox_AskModelAuthFormat", $"{auth.DisplayName}: {auth.Status}"),
            SecondaryLabel = LocalizationHelper.GetString(auth.Repair switch
            {
                InboxModelAuthRepair.SignIn => "Inbox_ModelAuthSignIn",
                InboxModelAuthRepair.ApiKey => "Inbox_ModelAuthUpdateApiKey",
                _ => "Inbox_ModelAuthReview",
            }),
            SecondaryRoute = InboxRoutes.Models,
        };
    }

    private static string ScopeLabel(GatewayApprovalScope scope) => scope.Kind switch
    {
        GatewayApprovalScopeKind.StandingGrant when scope.ExpiresInDays is { } days =>
            LocalizationHelper.Format("Inbox_ApprovalScopeStandingGrantDaysFormat", InboxGatewayItems.DisplayCommand(scope.Automation), days),
        GatewayApprovalScopeKind.StandingGrant =>
            LocalizationHelper.Format("Inbox_ApprovalScopeStandingGrantFormat", InboxGatewayItems.DisplayCommand(scope.Automation)),
        GatewayApprovalScopeKind.MessageSend =>
            LocalizationHelper.Format("Inbox_ApprovalScopeMessageSendFormat", scope.RecipientCount, InboxGatewayItems.DisplayCommand(scope.Target)),
        GatewayApprovalScopeKind.Payment =>
            LocalizationHelper.Format("Inbox_ApprovalScopePaymentFormat", InboxGatewayItems.DisplayCommand(scope.Amount), InboxGatewayItems.DisplayCommand(scope.Currency), InboxGatewayItems.DisplayCommand(scope.Target)),
        _ => LocalizationHelper.Format("Inbox_ApprovalScopeExternalPostFormat", InboxGatewayItems.DisplayCommand(scope.Target)),
    };

    private static string? HostLabel(string? host) => host switch
    {
        // "gateway" is the Gateway's machine, which may be remote; never present it as this PC.
        "gateway" => LocalizationHelper.GetString("Inbox_ApprovalHostGateway"),
        "node" => LocalizationHelper.GetString("Inbox_ApprovalHostDevice"),
        "sandbox" => LocalizationHelper.GetString("Inbox_ApprovalHostSandbox"),
        _ => InboxGatewayItems.DisplayCommand(host),
    };

    private static string FormatExpiry(DateTimeOffset expiresAt, DateTimeOffset now) =>
        InboxGatewayItems.FormatCountdown(expiresAt.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds()) is { } remaining
            ? LocalizationHelper.Format("Inbox_ApprovalExpiresInFormat", remaining)
            : LocalizationHelper.GetString("Inbox_ApprovalExpired");

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
        _ when entry.GatewayItem?.Kind == InboxGatewayItemKind.ModelAuth => FluentIconCatalog.Plug,
        _ when string.Equals(entry.Notification?.Source, "connection", StringComparison.OrdinalIgnoreCase) => FluentIconCatalog.Plug,
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
