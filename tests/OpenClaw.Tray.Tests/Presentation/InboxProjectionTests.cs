using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ExcludesInformationalAndSuccessResultsWithoutAChatRoute()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("paired", AppNotificationSeverity.Success, source: "node", category: "pairing", route: "connection"),
            Notification("info", AppNotificationSeverity.Informational, source: "session"),
        ]);

        Assert.Empty(inbox.Entries);
        Assert.False(inbox.Badge.IsVisible);
        Assert.Equal(InboxBadgeTone.None, inbox.Badge.Tone);
    }

    [Fact]
    public void Build_OrdersEntriesActionFirstThenNewestFirst()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("mention", AppNotificationSeverity.Informational, source: "gateway", route: AppNotificationActionRoutes.Chat("agent:main:main"), minutesAgo: 0),
            Notification("warning", AppNotificationSeverity.Warning, source: "channels", minutesAgo: 1),
            Notification("old-error", AppNotificationSeverity.Error, source: "connection", minutesAgo: 30),
            Notification("new-error", AppNotificationSeverity.Error, source: "sandbox", minutesAgo: 2),
            Notification("update-warning", AppNotificationSeverity.Warning, source: InboxSources.Update),
            Notification("access", AppNotificationSeverity.Warning, source: "node", category: InboxSources.Pairing),
            Notification("update-error", AppNotificationSeverity.Error, source: InboxSources.Update),
            Notification("outbox", AppNotificationSeverity.Error, source: InboxSources.Outbox),
            Notification("approval", AppNotificationSeverity.Informational, source: InboxSources.ExecApproval, minutesAgo: 60),
        ]);

        Assert.Equal(
            ["approval", "outbox", "update-error", "access", "new-error", "old-error", "update-warning", "warning", "mention"],
            inbox.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void Build_KeepsOneEntryPerNotificationId()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("same", AppNotificationSeverity.Error, source: "connection", minutesAgo: 1),
            Notification("same", AppNotificationSeverity.Error, source: "connection", minutesAgo: 9),
        ]);

        var entry = Assert.Single(inbox.Entries);
        Assert.Equal(Now.AddMinutes(-1), entry.CreatedAt);
        Assert.Equal(1, inbox.Badge.Count);
    }

    [Theory]
    [InlineData(AppNotificationSeverity.Error, "cron", null, (int)InboxEntryKind.Error, (int)InboxCategory.Automations)]
    [InlineData(AppNotificationSeverity.Warning, "cron", null, (int)InboxEntryKind.Warning, (int)InboxCategory.Automations)]
    [InlineData(AppNotificationSeverity.Error, "connection", "lifecycle", (int)InboxEntryKind.Error, (int)InboxCategory.System)]
    [InlineData(AppNotificationSeverity.Error, "node", "pairing", (int)InboxEntryKind.Error, (int)InboxCategory.System)]
    [InlineData(AppNotificationSeverity.Warning, "node", "pairing", (int)InboxEntryKind.AccessRequest, (int)InboxCategory.System)]
    [InlineData(AppNotificationSeverity.Warning, "gateway", "scope-upgrade", (int)InboxEntryKind.AccessRequest, (int)InboxCategory.System)]
    [InlineData(AppNotificationSeverity.Informational, "gateway", "exec-approval", (int)InboxEntryKind.Approval, (int)InboxCategory.Approvals)]
    public void Classify_MapsSourceCategoryAndSeverity(
        AppNotificationSeverity severity,
        string source,
        string? category,
        int expectedKind,
        int expectedCategory)
    {
        var entry = InboxProjection.Classify(Notification("n", severity, source, category));

        Assert.NotNull(entry);
        Assert.Equal((InboxEntryKind)expectedKind, entry.Kind);
        Assert.Equal((InboxCategory)expectedCategory, entry.Category);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat:agent%3Amain%3Amain")]
    public void Classify_TreatsChatRoutedInformationAsMention(string route)
    {
        var entry = InboxProjection.Classify(
            Notification("reply", AppNotificationSeverity.Informational, source: "gateway", route: route));

        Assert.NotNull(entry);
        Assert.Equal(InboxEntryKind.Mention, entry.Kind);
        Assert.Equal(InboxSeverity.Neutral, entry.Severity);
        Assert.True(entry.CanDismiss);
    }

    [Fact]
    public void Tabs_FilterAndCountEachCategoryWhileAllCountsEverything()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("approval", AppNotificationSeverity.Warning, source: InboxSources.ExecApproval),
            Notification("cron", AppNotificationSeverity.Error, source: InboxSources.Cron),
            Notification("cron-2", AppNotificationSeverity.Warning, source: InboxSources.Cron),
            Notification("system", AppNotificationSeverity.Error, source: "connection"),
            Notification("mention", AppNotificationSeverity.Informational, source: "gateway", route: "chat"),
        ]);

        Assert.Equal(5, inbox.Count(InboxTab.All));
        Assert.Equal(1, inbox.Count(InboxTab.Approvals));
        Assert.Equal(1, inbox.Count(InboxTab.Mentions));
        Assert.Equal(2, inbox.Count(InboxTab.Automations));
        Assert.Equal(1, inbox.Count(InboxTab.System));
        Assert.Equal(["cron", "cron-2"], inbox.For(InboxTab.Automations).Select(entry => entry.Id));
        Assert.Equal(["system"], inbox.For(InboxTab.System).Select(entry => entry.Id));
    }

    [Fact]
    public void DismissibleIds_ExcludeApprovalsAndOtherTabs()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("approval", AppNotificationSeverity.Warning, source: InboxSources.ExecApproval),
            Notification("error", AppNotificationSeverity.Error, source: "connection"),
            Notification("cron", AppNotificationSeverity.Error, source: InboxSources.Cron),
        ]);

        Assert.Equal(["error", "cron"], InboxProjection.DismissibleIds(inbox, InboxTab.All));
        Assert.Equal(["error"], InboxProjection.DismissibleIds(inbox, InboxTab.System));
        Assert.Empty(InboxProjection.DismissibleIds(inbox, InboxTab.Approvals));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "9+")]
    [InlineData(42, "9+")]
    public void Badge_CapsDisplayedCountAtNinePlus(int count, string expected)
    {
        var inbox = InboxProjection.Build(Enumerable.Range(0, count)
            .Select(i => Notification($"e{i}", AppNotificationSeverity.Error, source: "connection")));

        Assert.Equal(count, inbox.Badge.Count);
        Assert.Equal(expected, inbox.Badge.Text);
        Assert.Equal(InboxBadgeTone.Accent, inbox.Badge.Tone);
    }

    [Fact]
    public void Badge_UsesAttentionToneWhileAnApprovalWaits()
    {
        var inbox = InboxProjection.Build(
        [
            Notification("error", AppNotificationSeverity.Error, source: "connection"),
            Notification("approval", AppNotificationSeverity.Warning, source: InboxSources.ExecApproval),
        ]);

        Assert.Equal(InboxBadgeTone.Attention, inbox.Badge.Tone);
        Assert.Equal("2", inbox.Badge.Text);
    }

    [Theory]
    [InlineData(0, (int)InboxAgeUnit.Now, 0)]
    [InlineData(59, (int)InboxAgeUnit.Now, 0)]
    [InlineData(60, (int)InboxAgeUnit.Minutes, 1)]
    [InlineData(59 * 60, (int)InboxAgeUnit.Minutes, 59)]
    [InlineData(3 * 3600, (int)InboxAgeUnit.Hours, 3)]
    [InlineData(2 * 86400, (int)InboxAgeUnit.Days, 2)]
    [InlineData(8 * 86400, (int)InboxAgeUnit.Date, 0)]
    [InlineData(-30, (int)InboxAgeUnit.Now, 0)]
    public void Age_BucketsElapsedTime(int secondsAgo, int unit, int value)
    {
        Assert.Equal(
            new InboxAge((InboxAgeUnit)unit, value),
            InboxProjection.Age(Now.AddSeconds(-secondsAgo), Now));
    }

    private static AppNotification Notification(
        string id,
        AppNotificationSeverity severity,
        string source,
        string? category = null,
        string? route = null,
        int minutesAgo = 5) => new()
    {
        Id = id,
        Title = id,
        Message = id,
        Severity = severity,
        Source = source,
        Category = category,
        ActionLabel = route is null ? null : "Open",
        ActionRoute = route,
        CreatedAt = Now.AddMinutes(-minutesAgo),
    };
}
