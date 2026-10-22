using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.TestSupport;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class SettingsPersistenceNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictFlagCanBeReadWhileBackgroundWriterHoldsSaveLock(bool conflict)
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path);
        settings.SaveOrThrow();
        if (conflict)
        {
            var other = new SettingsManager(temp.Path) { NotificationSound = "external" };
            other.SaveOrThrow();
            Assert.Throws<SettingsPersistenceConflictException>(settings.SaveOrThrow);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writer = Task.Factory.StartNew(() =>
        {
            void HoldWriter()
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Test writer was not released.");
            }
            if (conflict)
                Assert.Throws<SettingsPersistenceConflictException>(() => settings.UpdateAndSave(HoldWriter));
            else
                settings.UpdateAndSave(HoldWriter);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? reader = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            reader = Task.Factory.StartNew(() => settings.HasPersistenceConflict,
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Equal(conflict, await reader.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(writer, reader is null ? Task.CompletedTask : reader)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static string Localize(string key) =>
        XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Descendants("data").Single(item => (string?)item.Attribute("name") == key).Element("value")!.Value;

    [Fact]
    public void LegacySaveConflictIsPersistentObservableAndDeduplicatedWithoutSavedSuccess()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path);
        settings.SaveOrThrow();
        var notifications = new AppNotificationService();
        using var dispatcher = new RecordingUiDispatcher();
        using var presentation = new SettingsPersistenceNotification(settings, dispatcher, notifications, Localize);
        var other = new SettingsManager(temp.Path) { NotificationSound = "external-content-not-for-notifications" };
        other.SaveOrThrow();
        var path = Path.Combine(temp.Path, "settings.json");
        var expectedDisk = File.ReadAllBytes(path);
        var saves = 0;
        var signals = 0;
        var notificationChanges = 0;
        settings.Saved += (_, _) => saves++;
        settings.PersistenceStateChanged += (_, _) => signals++;
        notifications.Changed += (_, _) => notificationChanges++;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            settings.GlobalHotkeyEnabled = false;
            settings.Save();
            Assert.True(settings.HasPersistenceConflict);
            Assert.Equal(expectedDisk, File.ReadAllBytes(path));
        }

        Assert.Equal(0, saves);
        Assert.Equal(3, signals);
        Assert.Equal(1, notificationChanges);
        var notification = Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.Equal(AppNotificationSeverity.Error, notification.Severity);
        Assert.Equal(AppNotificationPersistence.Persistent, notification.Persistence);
        Assert.Equal(1, notification.OccurrenceCount);
        Assert.Contains("reopen", notification.Message);
        Assert.Contains("tray menu", notification.Message);
        Assert.DoesNotContain("external-content-not-for-notifications", JsonSerializer.Serialize(notification));
        Assert.DoesNotContain(temp.Path, JsonSerializer.Serialize(notification));

        notifications.Dismiss(notification.Id);
        settings.Save();
        Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.Equal(0, saves);
        Assert.Equal(expectedDisk, File.ReadAllBytes(path));
    }

    [Fact]
    public void TypedStoreRollbackAndExplicitReloadPreserveExternalFieldsAndProtectedSecrets()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """{"FutureField":{"keep":true}}""");
        var settings = new SettingsManager(temp.Path);
        using var dispatcher = new RecordingUiDispatcher();
        using var store = new SettingsStore(settings, dispatcher);
        var notifications = new AppNotificationService();
        using var presentation = new SettingsPersistenceNotification(settings, dispatcher, notifications, Localize);
        var other = new SettingsManager(temp.Path)
        {
            NotificationSound = "external",
            TtsElevenLabsApiKey = "synthetic-secret"
        };
        other.SaveOrThrow();
        var disk = File.ReadAllBytes(path);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.Throws<SettingsPersistenceConflictException>(() =>
            store.Update(null, editor => editor.GlobalHotkeyEnabled = false));
        Assert.True(settings.GlobalHotkeyEnabled);
        Assert.Equal(0, changes);
        Assert.Equal(disk, File.ReadAllBytes(path));
        Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(notifications.Snapshot));

        settings.Load();
        Assert.False(settings.HasPersistenceConflict);
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
        store.Update(null, editor => editor.GlobalHotkeyEnabled = false);
        Assert.Equal(1, changes);
        var reloaded = new SettingsManager(temp.Path);
        Assert.False(reloaded.GlobalHotkeyEnabled);
        Assert.Equal("external", reloaded.NotificationSound);
        Assert.Equal("synthetic-secret", reloaded.TtsElevenLabsApiKey);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(json.RootElement.GetProperty("FutureField").GetProperty("keep").GetBoolean());
        Assert.StartsWith("dpapi:", json.RootElement.GetProperty("TtsElevenLabsApiKey").GetString());
    }

    [Fact]
    public void DispatchUsesCurrentStateAndDisposalDropsQueuedOrLaterNotifications()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path);
        settings.SaveOrThrow();
        using var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var notifications = new AppNotificationService();
        using var presentation = new SettingsPersistenceNotification(settings, dispatcher, notifications, Localize);
        var other = new SettingsManager(temp.Path) { NotificationSound = "external" };
        other.SaveOrThrow();
        Assert.Throws<SettingsPersistenceConflictException>(settings.SaveOrThrow);
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
        settings.Load();
        dispatcher.FlushPending();
        Assert.Empty(notifications.Snapshot.ActiveNotifications);

        other.NotificationSound = "changed again";
        other.SaveOrThrow();
        settings.Save();
        presentation.Dispose();
        dispatcher.FlushPending();
        var queued = dispatcher.EnqueuedCount;
        settings.Save();
        Assert.Equal(queued, dispatcher.EnqueuedCount);
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
    }

    [Fact]
    public void NormalWritesRaiseSavedButDoNotPublishConflict()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path);
        using var dispatcher = new RecordingUiDispatcher();
        var notifications = new AppNotificationService();
        using var presentation = new SettingsPersistenceNotification(settings, dispatcher, notifications, Localize);
        var saved = 0;
        settings.Saved += (_, _) => saved++;
        settings.SaveOrThrow();
        settings.Save();
        Assert.Equal(2, saved);
        Assert.False(settings.HasPersistenceConflict);
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
    }

    [Fact]
    public void ConflictNotificationIsLocalizedAndAppOwnsItsSubscriptionLifetime()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var paths = Directory.GetFiles(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings"),
            "Resources.resw", SearchOption.AllDirectories);
        Assert.Equal(6, paths.Length);
        foreach (var path in paths)
        {
            var entries = XDocument.Load(path).Descendants("data");
            foreach (var key in new[] { "Settings_PersistenceConflict_Title", "Settings_PersistenceConflict_Message" })
                Assert.False(string.IsNullOrWhiteSpace(Assert.Single(entries,
                    item => (string?)item.Attribute("name") == key).Element("value")?.Value));
        }
        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("_settingsPersistenceNotification = new SettingsPersistenceNotification(", app);
        var shutdown = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.AppShutdownCoordinator.cs"));
        Assert.Contains("_settingsPersistenceNotification?.Dispose()", shutdown);
        Assert.DoesNotContain("SettingsPersistenceConflictException", app);
    }
}
