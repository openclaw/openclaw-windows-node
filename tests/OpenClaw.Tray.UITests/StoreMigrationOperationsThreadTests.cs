using System.Reflection;
using Microsoft.UI.Dispatching;
using OpenClaw.Connection.Migration;
using OpenClawTray.Helpers;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Proves the production migration facade keeps finalization off the dispatcher that the
/// migration window calls it from. Detection is stubbed to report a present source, so the
/// coordinator returns before touching records, inventory, startup tasks, or files.
/// </summary>
[Collection(UICollection.Name)]
public sealed class StoreMigrationOperationsThreadTests(UIThreadFixture ui)
{
    [Fact]
    public async Task FinalizeAsync_CalledFromDispatcher_RunsFinalizationOffTheDispatcher()
    {
        var detector = new ThreadRecordingDetector();
        var operations = new StoreMigrationOperations("openclaw-test-unused-pipe");
        SetField(operations, "_binding", new MigrationBinding
        {
            InstallDirectory = Path.Combine(Path.GetTempPath(), "openclaw-finalize-thread-test"),
            UserSid = "S-1-5-21-0-0-0-1000",
        });
        SetField(operations, "_detector", detector);

        var decision = await ui.RunOnUIAsync(async () =>
        {
            Assert.NotNull(DispatcherQueue.GetForCurrentThread());
            return await operations.FinalizeAsync();
        });

        Assert.Equal(StoreMigrationFinalizationState.AwaitingInnoRemoval, decision.State);
        Assert.False(detector.RanOnDispatcher, "Finalization started on the dispatcher thread.");
    }

    private static void SetField(StoreMigrationOperations operations, string name, object value) =>
        typeof(StoreMigrationOperations)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(operations, value);

    private sealed class ThreadRecordingDetector : IInnoInstallationDetector
    {
        public bool? RanOnDispatcher { get; private set; }

        public InnoInstallationDetection Detect()
        {
            RanOnDispatcher = DispatcherQueue.GetForCurrentThread() is not null;
            return new(InnoInstallationStatus.Detected);
        }
    }
}
