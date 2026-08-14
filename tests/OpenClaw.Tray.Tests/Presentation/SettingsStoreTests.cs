using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavior of the settings facade over the real settings manager: snapshot reads, batched
/// single save, typed origin-aware notifications, UI-thread affinity, and disposal.
/// </summary>
public sealed class SettingsStoreTests
{
    private static SettingsStore NewStore(out SettingsManager settings, out RecordingUiDispatcher dispatcher, out TempDir temp)
    {
        temp = new TempDir();
        settings = new SettingsManager(temp.Path);
        dispatcher = new RecordingUiDispatcher();
        return new SettingsStore(settings, dispatcher);
    }

    [Fact]
    public void Current_ReflectsUnderlyingSettings_AndVersion()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            settings.GlobalHotkeyEnabled = true;
            settings.NotificationSound = "Subtle";

            var snapshot = store.Current;

            Assert.True(snapshot.GlobalHotkeyEnabled);
            Assert.Equal("Subtle", snapshot.NotificationSound);
            Assert.Equal(0, snapshot.Version);
        }
    }

    [Fact]
    public void Update_MutatesAndPersists_Once()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var saves = 0;
            settings.Saved += (_, _) => saves++;

            store.Update(store.CreateOrigin(), e =>
            {
                e.GlobalHotkeyEnabled = true;
                e.NotifyHealth = true;
            });

            Assert.True(settings.GlobalHotkeyEnabled);
            Assert.True(settings.NotifyHealth);
            Assert.Equal(1, saves);
        }
    }

    [Fact]
    public void Update_RaisesChangedOnce_WithOriginAndUpdatedSnapshot()
    {
        using var store = NewStore(out _, out _, out var temp);
        using (temp)
        {
            var origin = store.CreateOrigin();
            SettingsChangedEventArgs? args = null;
            store.Changed += (_, changed) => args = changed;

            store.Update(origin, e => e.GlobalHotkeyEnabled = true);

            Assert.NotNull(args);
            Assert.Same(origin, args!.Origin);
            Assert.True(args.Snapshot.GlobalHotkeyEnabled);
            Assert.Equal(1, args.Version);
        }
    }

    [Fact]
    public void Update_DoesNotEchoChangedToSelf()
    {
        using var store = NewStore(out _, out _, out var temp);
        using (temp)
        {
            var origin = store.CreateOrigin();
            var visibleChanges = 0;
            store.Changed += (_, args) =>
            {
                if (!ReferenceEquals(args.Origin, origin))
                {
                    visibleChanges++;
                }
            };

            store.Update(origin, e => e.GlobalHotkeyEnabled = true);

            Assert.Equal(0, visibleChanges);
        }
    }

    [Fact]
    public void ExternalSave_RaisesChangedWithNullOrigin()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            SettingsChangedEventArgs? args = null;
            store.Changed += (_, changed) => args = changed;

            settings.GlobalHotkeyEnabled = true;
            settings.Save();

            Assert.NotNull(args);
            Assert.Null(args!.Origin);
            Assert.True(args.Snapshot.GlobalHotkeyEnabled);
            Assert.Equal(1, args.Version);
        }
    }

    [Fact]
    public void ExternalSave_OffUiThread_IsMarshaledThroughDispatcher()
    {
        using var store = NewStore(out var settings, out var dispatcher, out var temp);
        using (temp)
        {
            dispatcher.HasThreadAccess = false;
            dispatcher.RunEnqueuedImmediately = false;
            SettingsChangedEventArgs? args = null;
            store.Changed += (_, changed) => args = changed;

            settings.Save();

            Assert.Null(args);
            Assert.Equal(1, dispatcher.EnqueuedCount);

            dispatcher.FlushPending();
            Assert.NotNull(args);
            Assert.Equal(1, args!.Version);
        }
    }

    [Fact]
    public void Update_ThrowingEdit_DoesNotLeakOrigin_ToLaterExternalSave()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            SettingsChangedEventArgs? args = null;
            store.Changed += (_, changed) => args = changed;

            Assert.Throws<InvalidOperationException>(() =>
                store.Update(store.CreateOrigin(), _ => throw new InvalidOperationException("boom")));

            settings.Save();

            Assert.NotNull(args);
            Assert.Null(args!.Origin);
            Assert.Equal(1, args.Version);
        }
    }

    [Fact]
    public void Dispose_Unsubscribes_FromSettingsManager()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            store.Dispose();
            settings.Save();

            Assert.Equal(0, changed);
        }
    }
}
