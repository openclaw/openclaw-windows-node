using Microsoft.Toolkit.Uwp.Notifications;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Do not disturb silences toasts and banners for everything except approvals, while the
/// Inbox keeps collecting items and the bell keeps counting them.
/// </summary>
public sealed class DoNotDisturbTests
{
    private static AppNotification Approval(string id = "approval") => new()
    {
        Id = id,
        Title = "Approve command",
        Message = "echo hi",
        Source = "gateway",
        Category = "exec-approval",
        Severity = AppNotificationSeverity.Warning,
    };

    private static AppNotification Warning(string id = "warning") => new()
    {
        Id = id,
        Title = "Channel needs attention",
        Message = "Telegram is not linked.",
        Source = "channels",
        Severity = AppNotificationSeverity.Warning,
    };

    private static AppNotification PairingRequest(string category) => new()
    {
        Id = "pairing-request",
        Title = "Node pairing pending",
        Message = "A device wants to pair.",
        Source = "node",
        Category = category,
        Severity = AppNotificationSeverity.Warning,
    };

    [Theory]
    [InlineData(false, (int)AlertKind.Standard, true)]
    [InlineData(false, (int)AlertKind.Approval, true)]
    [InlineData(true, (int)AlertKind.Standard, false)]
    [InlineData(true, (int)AlertKind.Approval, true)]
    public void AllowsAlert_SilencesOnlyNonApprovalsWhileOn(bool doNotDisturb, int kind, bool expected) =>
        Assert.Equal(expected, DoNotDisturbPolicy.AllowsAlert(doNotDisturb, (AlertKind)kind));

    [Fact]
    public void KindOf_TreatsInboxApprovalsAsApprovals()
    {
        Assert.Equal(AlertKind.Approval, DoNotDisturbPolicy.KindOf(Approval()));
        Assert.Equal(AlertKind.Approval, DoNotDisturbPolicy.KindOf(Warning() with { Source = InboxSources.ExecApproval }));
        Assert.Equal(AlertKind.Standard, DoNotDisturbPolicy.KindOf(Warning()));
        Assert.Equal(AlertKind.Standard, DoNotDisturbPolicy.KindOf(null));
    }

    [Theory]
    [InlineData(InboxSources.Pairing)]
    [InlineData(InboxSources.ScopeUpgrade)]
    public void PairingAndScopeUpgradeRequests_ComeThroughWhileStandardAlertsStaySilent(string category)
    {
        var request = PairingRequest(category);

        Assert.Equal(AlertKind.Approval, DoNotDisturbPolicy.KindOf(request));
        Assert.True(DoNotDisturbPolicy.AllowsAlert(true, DoNotDisturbPolicy.KindOf(request)));
        Assert.False(DoNotDisturbPolicy.AllowsAlert(true, DoNotDisturbPolicy.KindOf(Warning())));

        var toasts = new RecordingToastPublisher();
        AppNotificationPublisher.Publish(null, toasts, new AppNotificationPublishRequest(request, new ToastContentBuilder()));
        Assert.Equal([AlertKind.Approval], toasts.Kinds);

        var banner = new AppNotificationInfoBarPresenter()
            .Present(Snapshot(Warning(), request), null, false, null, "Show more", doNotDisturb: true);
        Assert.Equal("pairing-request", banner.Notification?.Id);
    }

    [Theory]
    [InlineData(AppNotificationSeverity.Success)]
    [InlineData(AppNotificationSeverity.Error)]
    public void PairingResults_StayStandard(AppNotificationSeverity severity) =>
        Assert.Equal(AlertKind.Standard, DoNotDisturbPolicy.KindOf(PairingRequest(InboxSources.Pairing) with { Severity = severity }));

    // App.xaml.cs is not compiled by Tray.Tests, so its explicit pairing-request toasts are source guards.
    [Theory]
    [InlineData("\"node-pairing-pending\",")]
    [InlineData("\"pairing-request\",")]
    public void PairingRequestToasts_AreTaggedAsApprovals(string toastTag)
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var tag = source.IndexOf(toastTag, StringComparison.Ordinal);
        Assert.True(tag >= 0, $"Missing toast tag {toastTag}");
        var call = source[tag..source.IndexOf(");", tag, StringComparison.Ordinal)];
        Assert.Contains("AlertKind.Approval", call);
    }

    [Fact]
    public void Publish_TagsApprovalToastsSoDoNotDisturbCanLetThemThrough()
    {
        var toasts = new RecordingToastPublisher();

        AppNotificationPublisher.Publish(null, toasts,
            new AppNotificationPublishRequest(Approval(), new ToastContentBuilder()));
        AppNotificationPublisher.Publish(null, toasts,
            new AppNotificationPublishRequest(Warning(), new ToastContentBuilder()));
        AppNotificationPublisher.Publish(null, toasts,
            new AppNotificationPublishRequest(null, new ToastContentBuilder()));

        Assert.Equal([AlertKind.Approval, AlertKind.Standard, AlertKind.Standard], toasts.Kinds);
    }

    [Fact]
    public void Banner_HidesNonApprovalsWhileOn_AndRestoresThemWhenOff()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var snapshot = Snapshot(Warning());

        var quiet = presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: true);
        Assert.False(quiet.IsVisible);

        var loud = presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: false);
        Assert.Equal("warning", loud.Notification?.Id);
    }

    [Fact]
    public void Banner_ShowsApprovalsWhileOn()
    {
        var presenter = new AppNotificationInfoBarPresenter();

        var presentation = presenter.Present(Snapshot(Warning(), Approval()), null, false, null, "Show more", doNotDisturb: true);

        Assert.Equal("approval", presentation.Notification?.Id);
    }

    [Fact]
    public void Banner_KeepsAClosedBannerClosedAcrossDoNotDisturb()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var snapshot = Snapshot(Warning());
        presenter.HideActiveNotifications(snapshot);

        presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: true);
        var afterOff = presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: false);

        Assert.False(afterOff.IsVisible);
    }

    [Fact]
    public void Banner_ClosingAnApprovalWhileOn_DoesNotHideSuppressedBanners()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var snapshot = Snapshot(Warning(), Approval());
        var shown = presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: true);
        Assert.Equal("approval", shown.Notification?.Id);

        presenter.HideActiveNotifications(snapshot, doNotDisturb: true);
        var afterOff = presenter.Present(snapshot, null, false, null, "Show more", doNotDisturb: false);

        Assert.Equal("warning", afterOff.Notification?.Id);
    }

    [Fact]
    public void Feed_PersistsDoNotDisturb_AndKeepsCollectingAndCounting()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var notifications = new AppNotificationService();
        var feed = new InboxFeed(notifications, null, null, store);
        var changes = 0;
        var doNotDisturbChanges = 0;
        feed.Changed += (_, _) => changes++;
        feed.DoNotDisturbChanged += (_, _) => doNotDisturbChanges++;

        feed.SetDoNotDisturb(true);
        notifications.Show(Warning());

        Assert.True(settings.DoNotDisturb);
        Assert.True(new SettingsManager(temp.Path).DoNotDisturb);
        Assert.True(feed.DoNotDisturb);
        Assert.Equal(1, doNotDisturbChanges);
        Assert.Equal(2, changes);
        var snapshot = feed.Snapshot;
        Assert.True(snapshot.DoNotDisturb);
        Assert.Single(snapshot.Entries);
        Assert.Equal(1, snapshot.Badge.Count);
        Assert.Equal(InboxBadgeTone.Accent, snapshot.Badge.Tone);
    }

    [Fact]
    public void Feed_FollowsSavesFromOtherWriters_AndIgnoresRepeats()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var feed = new InboxFeed(new AppNotificationService(), null, null, store);
        var doNotDisturbChanges = 0;
        feed.DoNotDisturbChanged += (_, _) => doNotDisturbChanges++;

        store.Update(store.CreateOrigin(), editor => editor.DoNotDisturb = true);
        store.Update(store.CreateOrigin(), editor => editor.NotifyBuild = false);
        feed.SetDoNotDisturb(true);

        Assert.True(feed.DoNotDisturb);
        Assert.Equal(1, doNotDisturbChanges);

        feed.SetDoNotDisturb(false);
        Assert.False(feed.DoNotDisturb);
        Assert.False(settings.DoNotDisturb);
        Assert.Equal(2, doNotDisturbChanges);
    }

    [Fact]
    public void Feed_IgnoresAStaleChangeDeliveredAfterANewerSave()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        using var store = new SettingsStore(settings, dispatcher);
        var feed = new InboxFeed(new AppNotificationService(), null, null, store);

        store.Update(null, editor => editor.NotifyBuild = false);   // queued change: Do not disturb off
        feed.SetDoNotDisturb(true);                                // applied at once from Current
        dispatcher.FlushPending();

        Assert.True(feed.DoNotDisturb);
    }

    [Fact]
    public void Feed_AttachedLater_AdoptsThePersistedValue_AndSavesThroughIt()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { DoNotDisturb = true };
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var feed = new InboxFeed(new AppNotificationService(), null);
        var doNotDisturbChanges = 0;
        feed.DoNotDisturbChanged += (_, _) => doNotDisturbChanges++;

        feed.AttachSettings(store);
        feed.SetDoNotDisturb(false);

        Assert.Equal(2, doNotDisturbChanges);
        Assert.False(feed.DoNotDisturb);
        Assert.False(new SettingsManager(temp.Path).DoNotDisturb);
    }

    [Fact]
    public void Feed_WithoutSettings_IsNeverDoNotDisturb()
    {
        var feed = new InboxFeed(new AppNotificationService(), null);

        feed.SetDoNotDisturb(true);

        Assert.False(feed.DoNotDisturb);
        Assert.False(feed.Snapshot.DoNotDisturb);
    }

    // ToastService is a WinUI/OS adapter not compiled by Tray.Tests, so its use of the policy is a source guard.
    [Fact]
    public void ToastService_SilencesStandardToastsOnly_AndNeverApprovalToasts()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Services", "ToastService.cs"));
        var showStart = source.IndexOf("public void ShowToast(", StringComparison.Ordinal);
        var withdrawableStart = source.IndexOf("public bool ShowWithdrawableToast(", StringComparison.Ordinal);
        var removeStart = source.IndexOf("public void RemoveToast(", StringComparison.Ordinal);
        Assert.True(showStart >= 0 && withdrawableStart > showStart && removeStart > withdrawableStart);

        var show = source[showStart..withdrawableStart];
        var gate = show.IndexOf("DoNotDisturbPolicy.AllowsAlert(_getSettings()?.DoNotDisturb == true, alertKind)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < show.IndexOf("ShouldShowToast(", StringComparison.Ordinal));
        Assert.DoesNotContain("DoNotDisturb", source[withdrawableStart..removeStart]);
    }

    private static AppNotificationSnapshot Snapshot(params AppNotification[] active) =>
        new(active.FirstOrDefault(), active.Length, active.Skip(1).ToList(), active);

    private sealed class RecordingToastPublisher : IToastNotificationPublisher
    {
        public List<AlertKind> Kinds { get; } = new();

        public void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null,
            AlertKind alertKind = AlertKind.Standard) => Kinds.Add(alertKind);
    }
}
