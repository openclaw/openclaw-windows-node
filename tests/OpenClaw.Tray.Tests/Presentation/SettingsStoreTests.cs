using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavior of the settings facade over the real settings manager: snapshot reads, batched
/// single save, self-originated echo suppression, external-change republishing, and UI-thread
/// affinity for the <see cref="ISettingsStore.Changed"/> notification.
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
    public void Current_ReflectsUnderlyingSettings()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            settings.GlobalHotkeyEnabled = true;
            settings.NotificationSound = "Subtle";

            var snapshot = store.Current;

            Assert.True(snapshot.GlobalHotkeyEnabled);
            Assert.Equal("Subtle", snapshot.NotificationSound);
        }
    }

    [Fact]
    public void Update_MutatesAndPersists_Once()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var saves = 0;
            settings.Saved += (_, _) => saves++;

            store.Update(e =>
            {
                e.GlobalHotkeyEnabled = true;
                e.NotifyHealth = true;
            });

            Assert.True(settings.GlobalHotkeyEnabled);
            Assert.True(settings.NotifyHealth);
            Assert.Equal(1, saves); // batched: one save for both edits
        }
    }

    [Fact]
    public void Update_DoesNotEchoChangedToSelf()
    {
        var store = NewStore(out _, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            store.Update(e => e.GlobalHotkeyEnabled = true);

            Assert.Equal(0, changed); // self-originated save is suppressed
        }
    }

    [Fact]
    public void ExternalSave_RaisesChanged()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            // A save that did not originate from this store's Update (e.g. another surface).
            settings.GlobalHotkeyEnabled = true;
            settings.Save();

            Assert.Equal(1, changed);
        }
    }

    [Fact]
    public void ExternalSave_OffUiThread_IsMarshaledThroughDispatcher()
    {
        var store = NewStore(out var settings, out var dispatcher, out var temp);
        using (temp)
        {
            dispatcher.HasThreadAccess = false;
            dispatcher.RunEnqueuedImmediately = false;
            var changed = 0;
            store.Changed += (_, _) => changed++;

            settings.Save();

            Assert.Equal(0, changed);          // not raised inline off-thread
            Assert.Equal(1, dispatcher.EnqueuedCount);

            dispatcher.FlushPending();
            Assert.Equal(1, changed);          // raised once marshaled to the UI thread
        }
    }

    [Fact]
    public void BeginSelfWrite_SuppressesChanged_ForDirectSave()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            using (store.BeginSelfWrite())
            {
                settings.GlobalHotkeyEnabled = true;
                settings.Save();
            }

            Assert.Equal(0, changed); // save inside a self-write scope is suppressed like Update

            settings.Save(); // outside the scope it is external again
            Assert.Equal(1, changed);
        }
    }

    [Fact]
    public void Update_ThrowingEdit_ResetsSelfWriteDepth_SoLaterExternalSaveRepublishes()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            Assert.Throws<InvalidOperationException>(() =>
                store.Update(_ => throw new InvalidOperationException("boom")));

            // The scope's Dispose ran in the finally path, so the depth is not stuck above zero:
            // a subsequent external save still republishes Changed exactly once.
            settings.Save();
            Assert.Equal(1, changed);
        }
    }
}
