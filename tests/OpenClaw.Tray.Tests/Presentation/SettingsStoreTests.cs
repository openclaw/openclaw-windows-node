using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavior of the settings facade over the real settings manager: snapshot reads, batched
/// single save, typed origin-aware notifications, UI-thread affinity, and disposal.
/// </summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void HostedSetupInitializesBalancedOnceAndPreservesCustomEditsAcrossResume()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var setup = new SetupSettingsWriter(store);
            setup.Apply(new Dictionary<string, bool>(), OpenClaw.Shared.SystemRunAccessPreset.Balanced, initializationOnly: true);
            Assert.Equal(OpenClaw.Shared.SystemRunFilesystemScope.UserFilesReadOnly, settings.SystemRunFilesystemScope);
            Assert.True(settings.SystemRunAllowOutbound);
            Assert.Empty(settings.SandboxCustomFolders);
            settings.SystemRunFilesystemScope = OpenClaw.Shared.SystemRunFilesystemScope.SelectedFolders;
            settings.SandboxCustomFolders =
                [new() { Path = @"C:\selected", Access = OpenClaw.Shared.SandboxFolderAccess.ReadOnly }];
            settings.SandboxClipboard = OpenClaw.Shared.SandboxClipboardMode.Both;
            settings.SaveOrThrow();
            var fingerprint = settings.SystemRunPolicyFingerprint;
            setup.Apply(new Dictionary<string, bool>(), OpenClaw.Shared.SystemRunAccessPreset.Balanced, initializationOnly: true);
            Assert.Equal(fingerprint, settings.SystemRunPolicyFingerprint);
            var resumed = new SetupSettingsWriter(store);
            resumed.Apply(new Dictionary<string, bool>(), OpenClaw.Shared.SystemRunAccessPreset.Balanced, initializationOnly: true);
            Assert.Equal(fingerprint, settings.SystemRunPolicyFingerprint);
        }
    }

    [Fact]
    public void HostedSetupExplicitPresetRejectsStalePermissionIntentBeforeOtherWrites()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var setup = new SetupSettingsWriter(store);
            settings.SandboxCustomFolders =
                [new() { Path = @"C:\selected", Access = OpenClaw.Shared.SandboxFolderAccess.ReadOnly }];
            settings.SaveOrThrow();
            var fingerprint = settings.SystemRunPolicyFingerprint;
            var nodeMode = settings.EnableNodeMode;
            Assert.Throws<InvalidOperationException>(() => setup.Apply(
                new Dictionary<string, bool> { ["EnableNodeMode"] = !nodeMode },
                OpenClaw.Shared.SystemRunAccessPreset.Open));
            Assert.Equal(nodeMode, settings.EnableNodeMode);
            Assert.Equal(fingerprint, settings.SystemRunPolicyFingerprint);
        }
    }

    [Fact]
    public async Task HostedSetupSerializesWithBackgroundSettingsMutationWithoutLosingUnrelatedFields()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        using (var release = new ManualResetEventSlim())
        {
            var timeout = TimeSpan.FromSeconds(5);
            // The holder must outlast the writer-start wait plus the 150 ms window, so only the
            // test's release can end it. A self-release would complete it during that window.
            var holderTimeout = timeout * 2;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var path = Path.Combine(temp.Path, "settings.json");
            File.WriteAllText(path, """{"UnrelatedSentinel":"keep"}""");
            settings.Load();
            var setup = new SetupSettingsWriter(store);
            // Both writers block in production code, so they must not depend on available pool workers.
            var background = Task.Factory.StartNew(() => store.Update(null, editor =>
            {
                editor.NotificationSound = "background";
                entered.SetResult();
                if (!release.Wait(holderTimeout))
                    throw new TimeoutException("The background settings writer was not released.");
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task? writing = null;
            Exception? failure = null;
            try
            {
                var ready = await Task.WhenAny(entered.Task, background).WaitAsync(timeout);
                await ready;
                writing = Task.Factory.StartNew(() =>
                {
                    writingStarted.SetResult();
                    setup.Apply(new Dictionary<string, bool> { ["NodeCameraEnabled"] = false });
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var started = await Task.WhenAny(writingStarted.Task, writing).WaitAsync(timeout);
                await started;
                Assert.False(background.IsCompleted);
                Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(150)));
                Assert.False(background.IsCompleted);
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                release.Set();
                // Join both workers before the fixtures are disposed. After an earlier failure, a
                // worker fault is secondary and must not replace that failure.
                var workers = Task.WhenAll(background, writing ?? Task.CompletedTask);
                if (failure is null)
                {
                    await workers;
                }
                else
                {
                    await Task.WhenAny(workers);
                    _ = workers.Exception;
                }
            }
            var saved = new SettingsManager(temp.Path);
            Assert.Equal("background", saved.NotificationSound);
            Assert.False(saved.NodeCameraEnabled);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("keep", json.RootElement.GetProperty("UnrelatedSentinel").GetString());
        }
    }

    [Fact]
    public void HostedSetupRejectsSameFieldConflictBeforeMutatingOtherFields()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var setup = new SetupSettingsWriter(store);
            store.Update(null, editor => editor.NodeCameraEnabled = false);
            var before = File.ReadAllBytes(Path.Combine(temp.Path, "settings.json"));
            Assert.Throws<InvalidOperationException>(() => setup.Apply(new Dictionary<string, bool>
            { ["EnableNodeMode"] = true, ["NodeCameraEnabled"] = true }));
            Assert.False(settings.EnableNodeMode);
            Assert.False(settings.NodeCameraEnabled);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, "settings.json")));
        }
    }

    [Fact]
    public void StaleSettingsInstanceCannotOverwriteAnotherWriterAndFailedUpdateRollsBack()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            store.Update(null, editor => editor.GlobalHotkeyEnabled = true);
            var other = new SettingsManager(temp.Path);
            other.NotificationSound = "other-writer";
            other.SaveOrThrow();
            var before = File.ReadAllBytes(Path.Combine(temp.Path, "settings.json"));
            Assert.Throws<SettingsPersistenceConflictException>(() => store.Update(null, editor => editor.GlobalHotkeyEnabled = false));
            Assert.True(settings.GlobalHotkeyEnabled);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, "settings.json")));
            settings.Load();
            store.Update(null, editor => editor.GlobalHotkeyEnabled = false);
            Assert.Equal("other-writer", settings.NotificationSound);
            Assert.False(settings.GlobalHotkeyEnabled);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void UnchangedInvalidSettingsJsonCanBeReplaced(string invalidJson)
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, invalidJson);
        var settings = new SettingsManager(temp.Path)
        {
            NotificationSound = "recovered"
        };

        settings.SaveOrThrow();

        var reloaded = new SettingsManager(temp.Path);
        Assert.Equal("recovered", reloaded.NotificationSound);
    }

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
