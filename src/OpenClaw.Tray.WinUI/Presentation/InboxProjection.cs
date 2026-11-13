// Projects active app notifications and Gateway-sourced items into the web-aligned
// Inbox: only items that still need the user, in a fixed action-first order, grouped
// into tabs, with the bell badge derived from the same entries. WinUI-free so it is
// unit-testable.
//
// Usage:
//   var inbox = InboxProjection.Build(service.Snapshot.ActiveNotifications, gateway.Items);
//   footer.ApplyInboxBadge(inbox.Badge, helpText);
//   foreach (var entry in inbox.For(InboxTab.Approvals)) { ... }
//   var age = InboxProjection.Age(entry.CreatedAt, DateTimeOffset.UtcNow);

using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>Inbox entry kinds, declared in the web Control UI's action-first order.</summary>
internal enum InboxEntryKind
{
    Approval,
    Outbox,
    UpdateError,
    AccessRequest,
    Error,
    UpdateWarning,
    Warning,
    Mention,
}

internal enum InboxCategory { Approvals, Mentions, Automations, System }

internal enum InboxTab { All, Approvals, Mentions, Automations, System }

internal enum InboxSeverity { Neutral, Warning, Error }

internal enum InboxBadgeTone { None, Accent, Attention }

internal enum InboxAgeUnit { Now, Minutes, Hours, Days, Date }

internal readonly record struct InboxAge(InboxAgeUnit Unit, int Value);

/// <summary>Notification sources and categories that select a specific Inbox kind.</summary>
internal static class InboxSources
{
    public const string ExecApproval = "exec-approval";
    public const string Outbox = "outbox";
    public const string Update = "update";
    public const string Cron = "cron";
    public const string Pairing = "pairing";
    public const string ScopeUpgrade = "scope-upgrade";
}

internal static class InboxRoutes
{
    /// <summary>Settings route of the notification preferences opened by the Inbox gear.</summary>
    public const string NotificationSettings = "notification-preferences";

    /// <summary>Settings route of the Automations (cron) page linked from automation failures.</summary>
    public const string Automations = "cron";

    /// <summary>Settings route of the Models page, where provider accounts are signed in again.</summary>
    public const string Models = "model";
}

/// <summary>
/// One Inbox row. Exactly one of <see cref="Notification"/> (app notifications) and
/// <see cref="GatewayItem"/> (live Gateway facts) is set.
/// </summary>
internal sealed record InboxEntry(
    AppNotification? Notification,
    InboxEntryKind Kind,
    InboxCategory Category,
    InboxSeverity Severity)
{
    public InboxGatewayItem? GatewayItem { get; init; }

    public string Id => GatewayItem?.Id ?? Notification!.Id;

    public DateTimeOffset CreatedAt => GatewayItem?.ArrivedAt ?? Notification!.CreatedAt;

    /// <summary>Approvals leave the Inbox only when they are decided or expire.</summary>
    public bool CanDismiss => Kind != InboxEntryKind.Approval;

    public bool MatchesTab(InboxTab tab) => tab switch
    {
        InboxTab.All => true,
        InboxTab.Approvals => Category == InboxCategory.Approvals,
        InboxTab.Mentions => Category == InboxCategory.Mentions,
        InboxTab.Automations => Category == InboxCategory.Automations,
        InboxTab.System => Category == InboxCategory.System,
        _ => false,
    };
}

/// <summary>Bell badge: hidden at zero, capped at "9+", amber while an approval waits.</summary>
internal sealed record InboxBadge(int Count, string Text, InboxBadgeTone Tone)
{
    public const int MaxDisplayedCount = 9;

    public static InboxBadge Empty { get; } = new(0, "", InboxBadgeTone.None);

    public bool IsVisible => Count > 0;
}

internal sealed record InboxSnapshot(IReadOnlyList<InboxEntry> Entries, InboxBadge Badge)
{
    public static InboxSnapshot Empty { get; } = new([], InboxBadge.Empty);

    public IReadOnlyList<InboxEntry> For(InboxTab tab) =>
        tab == InboxTab.All ? Entries : Entries.Where(entry => entry.MatchesTab(tab)).ToList();

    public int Count(InboxTab tab) =>
        tab == InboxTab.All ? Entries.Count : Entries.Count(entry => entry.MatchesTab(tab));
}

internal static class InboxProjection
{
    public static InboxSnapshot Build(
        IEnumerable<AppNotification> notifications,
        IEnumerable<InboxGatewayItem>? gatewayItems = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        var entries = notifications
            .Select(notification => Classify(notification))
            .OfType<InboxEntry>()
            .Concat((gatewayItems ?? []).Select(item => Classify(item)))
            .OrderBy(entry => entry.Kind)
            .ThenByDescending(entry => entry.CreatedAt)
            // The service coalesces by dedupe key, not id; one row per id keeps rows and badge consistent.
            .DistinctBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
        return new InboxSnapshot(entries, BuildBadge(entries));
    }

    /// <summary>Maps a Gateway fact to its Inbox kind, tab and severity.</summary>
    public static InboxEntry Classify(InboxGatewayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (kind, category, severity) = item.Kind switch
        {
            InboxGatewayItemKind.Approval => (InboxEntryKind.Approval, InboxCategory.Approvals, InboxSeverity.Warning),
            InboxGatewayItemKind.AutomationFailed => (InboxEntryKind.Error, InboxCategory.Automations, InboxSeverity.Error),
            InboxGatewayItemKind.AutomationOverdue => (InboxEntryKind.Warning, InboxCategory.Automations, InboxSeverity.Warning),
            _ => (InboxEntryKind.Error, InboxCategory.System, InboxSeverity.Error),
        };
        return new InboxEntry(null, kind, category, severity) { GatewayItem = item };
    }

    /// <summary>
    /// Returns the Inbox entry for a notification, or null when it does not need
    /// the user (informational and success results stay toasts and banners only).
    /// </summary>
    public static InboxEntry? Classify(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var severity = notification.Severity switch
        {
            AppNotificationSeverity.Error => InboxSeverity.Error,
            AppNotificationSeverity.Warning => InboxSeverity.Warning,
            _ => InboxSeverity.Neutral,
        };

        if (Is(notification.Source, InboxSources.ExecApproval) || Is(notification.Category, InboxSources.ExecApproval))
            return new(notification, InboxEntryKind.Approval, InboxCategory.Approvals, InboxSeverity.Warning);

        if (Is(notification.Source, InboxSources.Outbox))
            return new(notification, InboxEntryKind.Outbox, InboxCategory.System,
                severity == InboxSeverity.Error ? InboxSeverity.Error : InboxSeverity.Warning);

        if (Is(notification.Source, InboxSources.Update))
            return severity == InboxSeverity.Error
                ? new(notification, InboxEntryKind.UpdateError, InboxCategory.System, InboxSeverity.Error)
                : new(notification, InboxEntryKind.UpdateWarning, InboxCategory.System, InboxSeverity.Warning);

        if (severity == InboxSeverity.Warning &&
            (Is(notification.Category, InboxSources.Pairing) || Is(notification.Category, InboxSources.ScopeUpgrade)))
            return new(notification, InboxEntryKind.AccessRequest, InboxCategory.System, InboxSeverity.Warning);

        var category = Is(notification.Source, InboxSources.Cron) ? InboxCategory.Automations : InboxCategory.System;
        if (severity == InboxSeverity.Error)
            return new(notification, InboxEntryKind.Error, category, InboxSeverity.Error);
        if (severity == InboxSeverity.Warning)
            return new(notification, InboxEntryKind.Warning, category, InboxSeverity.Warning);

        return IsChatRoute(notification.ActionRoute)
            ? new(notification, InboxEntryKind.Mention, InboxCategory.Mentions, InboxSeverity.Neutral)
            : null;
    }

    public static InboxBadge BuildBadge(IReadOnlyCollection<InboxEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var count = entries.Count;
        if (count == 0)
            return InboxBadge.Empty;

        var text = count > InboxBadge.MaxDisplayedCount
            ? $"{InboxBadge.MaxDisplayedCount}+"
            : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tone = entries.Any(entry => entry.Kind == InboxEntryKind.Approval)
            ? InboxBadgeTone.Attention
            : InboxBadgeTone.Accent;
        return new InboxBadge(count, text, tone);
    }

    /// <summary>Ids of the dismissible entries shown on <paramref name="tab"/>, for "Dismiss shown".</summary>
    public static IReadOnlyList<string> DismissibleIds(InboxSnapshot snapshot, InboxTab tab) =>
        snapshot.For(tab).Where(entry => entry.CanDismiss).Select(entry => entry.Id).ToList();

    public static InboxAge Age(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var elapsed = now - createdAt;
        if (elapsed < TimeSpan.FromMinutes(1))
            return new(InboxAgeUnit.Now, 0);
        if (elapsed < TimeSpan.FromHours(1))
            return new(InboxAgeUnit.Minutes, (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1))
            return new(InboxAgeUnit.Hours, (int)elapsed.TotalHours);
        if (elapsed < TimeSpan.FromDays(7))
            return new(InboxAgeUnit.Days, (int)elapsed.TotalDays);
        return new(InboxAgeUnit.Date, 0);
    }

    private static bool IsChatRoute(string? route) =>
        string.Equals(route, "chat", StringComparison.OrdinalIgnoreCase) ||
        AppNotificationActionRoutes.TryGetChatSessionKey(route, out _);

    private static bool Is(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
