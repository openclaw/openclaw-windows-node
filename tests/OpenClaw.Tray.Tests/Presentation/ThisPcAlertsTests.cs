using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// "This PC" alerts: Windows privacy blocks on a device an enabled node capability needs,
/// filed as System-tab Inbox warnings that open the matching privacy settings page and
/// clear when the block clears.
/// </summary>
public sealed class ThisPcAlertsTests
{
    private static readonly ThisPcCapabilities AllOn = new(NodeActive: true, Camera: true, Microphone: true, Location: true);

    [Fact]
    public void Build_AlertsOnlyBlockedDevicesThatAnEnabledCapabilityNeeds()
    {
        var access = new Dictionary<ThisPcDevice, ThisPcDeviceAccess>
        {
            [ThisPcDevice.Camera] = ThisPcDeviceAccess.DeniedByUser,
            [ThisPcDevice.Microphone] = ThisPcDeviceAccess.Allowed,
            [ThisPcDevice.Location] = ThisPcDeviceAccess.DeniedBySystem,
        };

        var alerts = ThisPcAlerts.Build(AllOn, device => access[device]);

        Assert.Equal(
            [new ThisPcAlert(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedByUser),
             new ThisPcAlert(ThisPcDevice.Location, ThisPcDeviceAccess.DeniedBySystem)],
            alerts);
    }

    [Fact]
    public void Build_NeverReadsDevicesNoEnabledCapabilityNeeds()
    {
        var read = new List<ThisPcDevice>();
        ThisPcDeviceAccess Denied(ThisPcDevice device)
        {
            read.Add(device);
            return ThisPcDeviceAccess.DeniedByUser;
        }

        Assert.Empty(ThisPcAlerts.Build(AllOn with { NodeActive = false }, Denied));
        Assert.Empty(read);

        var alerts = ThisPcAlerts.Build(AllOn with { Camera = false, Location = false }, Denied);
        Assert.Equal([ThisPcDevice.Microphone], read);
        Assert.Equal(ThisPcDevice.Microphone, Assert.Single(alerts).Device);
    }

    [Theory]
    [InlineData((int)ThisPcDeviceAccess.Unknown)]
    [InlineData((int)ThisPcDeviceAccess.Allowed)]
    public void Build_UnknownOrAllowedAccessIsNotABlock(int access) =>
        Assert.Empty(ThisPcAlerts.Build(AllOn, _ => (ThisPcDeviceAccess)access));

    [Fact]
    public void Capabilities_FollowNodeOrMcpModeAndTheCapabilityToggles()
    {
        var snapshot = new SettingsSnapshot
        {
            EnableNodeMode = false,
            EnableMcpServer = true,
            NodeCameraEnabled = true,
            NodeSttEnabled = false,
            NodeLocationEnabled = true,
        };

        // Camera clips record sound by default, so the camera alone needs the microphone.
        Assert.Equal(new ThisPcCapabilities(true, true, true, true), ThisPcCapabilities.From(snapshot));
        Assert.False(ThisPcCapabilities.From(snapshot with { NodeCameraEnabled = false }).Microphone);
        Assert.True(ThisPcCapabilities.From(snapshot with { NodeCameraEnabled = false, NodeSttEnabled = true }).Microphone);
        Assert.False(ThisPcCapabilities.From(snapshot with { EnableMcpServer = false }).NodeActive);
    }

    [Theory]
    [InlineData((int)ThisPcDevice.Camera, "ms-settings:privacy-webcam", "ThisPc_CameraBlockedTitle")]
    [InlineData((int)ThisPcDevice.Microphone, "ms-settings:privacy-microphone", "ThisPc_MicrophoneBlockedTitle")]
    [InlineData((int)ThisPcDevice.Location, "ms-settings:privacy-location", "ThisPc_LocationBlockedTitle")]
    public void Notification_IsASystemWarningThatOpensTheDevicePrivacyPage(int device, string route, string titleKey)
    {
        var notification = ThisPcAlerts.ToNotification(new((ThisPcDevice)device, ThisPcDeviceAccess.DeniedByUser), key => key);

        Assert.Equal(titleKey, notification.Title);
        Assert.Equal(route, notification.ActionRoute);
        Assert.Equal("ThisPc_OpenPrivacySettings", notification.ActionLabel);
        Assert.Equal(AppNotificationSeverity.Warning, notification.Severity);
        var entry = InboxProjection.Classify(notification);
        Assert.NotNull(entry);
        Assert.Equal(InboxCategory.System, entry.Category);
        Assert.True(entry.CanDismiss);
        Assert.True(ThisPcAlerts.IsPrivacySettingsRoute(route));
    }

    [Fact]
    public void Notification_SaysWhenWindowsOrTheOrganizationManagesTheSetting()
    {
        string Text(string key) => key;

        Assert.Equal("ThisPc_CameraBlockedMessage",
            ThisPcAlerts.ToNotification(new(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedByUser), Text).Message);
        Assert.Equal("ThisPc_CameraBlockedMessage ThisPc_ManagedBySystem",
            ThisPcAlerts.ToNotification(new(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedBySystem), Text).Message);
    }

    [Theory]
    [InlineData("ms-settings:privacy-broadfilesystemaccess")]
    [InlineData("ms-settings:privacy-webcam-extra")]
    [InlineData("notifications")]
    [InlineData("https://example.com")]
    [InlineData(null)]
    public void OnlyTheLinkedPrivacyPagesAreRoutes(string? route) =>
        Assert.False(ThisPcAlerts.IsPrivacySettingsRoute(route));

    [Fact]
    public void FixtureAccess_ParsesKnownDevicesAndValuesOnly()
    {
        var access = ThisPcAlerts.ParseFixtureAccess(" camera = denied-by-user ; location=DENIED-BY-SYSTEM;1=denied-by-user;microphone=maybe;bogus");

        Assert.Equal(ThisPcDeviceAccess.DeniedByUser, access[ThisPcDevice.Camera]);
        Assert.Equal(ThisPcDeviceAccess.DeniedBySystem, access[ThisPcDevice.Location]);
        Assert.Equal(ThisPcDeviceAccess.Unknown, access[ThisPcDevice.Microphone]);
        Assert.Equal(3, access.Count);
        Assert.Empty(ThisPcAlerts.ParseFixtureAccess(null));
    }

    [Fact]
    public void Source_PublishesWithdrawsAndRespectsDismissal()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            EnableNodeMode = true,
            NodeCameraEnabled = true,
            NodeSttEnabled = false,
            NodeLocationEnabled = false,
        };
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var notifications = new AppNotificationService();
        var reader = new FakeReader();
        using var source = new ThisPcAlertSource(notifications, store, reader, key => key);

        reader.Set(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedByUser);
        var row = Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.Equal(ThisPcAlerts.Source, row.Source);

        // A dismissed row stays dismissed while the same block remains.
        notifications.Dismiss(row.Id);
        source.Refresh();
        Assert.Empty(notifications.Snapshot.ActiveNotifications);

        // The block clearing and returning is a new episode.
        reader.Set(ThisPcDevice.Camera, ThisPcDeviceAccess.Allowed);
        reader.Set(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedByUser);
        Assert.Single(notifications.Snapshot.ActiveNotifications);

        // A different kind of block replaces the row instead of counting a repeat.
        reader.Set(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedBySystem);
        var replaced = Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.Equal(1, replaced.OccurrenceCount);
        Assert.EndsWith("ThisPc_ManagedBySystem", replaced.Message);

        // Turning the capability off withdraws the row through the settings owner.
        store.Update(null, editor => editor.NodeCameraEnabled = false);
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
    }

    [Fact]
    public void Source_StopsListeningWhenDisposed()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true, NodeCameraEnabled = true };
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var notifications = new AppNotificationService();
        var reader = new FakeReader();
        var source = new ThisPcAlertSource(notifications, store, reader, key => key);

        source.Dispose();
        reader.Set(ThisPcDevice.Camera, ThisPcDeviceAccess.DeniedByUser);
        source.Refresh();

        Assert.Empty(notifications.Snapshot.ActiveNotifications);
        Assert.True(reader.Disposed);
    }

    private sealed class FakeReader : IDeviceAccessReader
    {
        private readonly Dictionary<ThisPcDevice, ThisPcDeviceAccess> _access = new();

        public event EventHandler? Changed;

        public bool Disposed { get; private set; }

        public void Set(ThisPcDevice device, ThisPcDeviceAccess access)
        {
            _access[device] = access;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public ThisPcDeviceAccess Read(ThisPcDevice device) =>
            _access.TryGetValue(device, out var access) ? access : ThisPcDeviceAccess.Unknown;

        public void Dispose() => Disposed = true;
    }
}
