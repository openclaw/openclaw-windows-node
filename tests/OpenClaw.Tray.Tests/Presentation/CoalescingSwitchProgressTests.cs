using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class CoalescingSwitchProgressTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void BufferedReportsFromAHashLoopQueueAtMostOneUiDeliveryCarryingTheLatestBytes()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var delivered = new List<LocalAiModelSwitchProgress>();
        long now = 0;
        var progress = new CoalescingSwitchProgress(dispatcher, delivered.Add, () => now);

        // One report per 1 MiB buffer while verifying a 16 GiB model, all inside one interval.
        const int reports = 16 * 1024;
        for (long mib = 1; mib <= reports; mib++)
            progress.Report(new(LocalAiModelSwitchPhase.Verifying, mib << 20, 16 * GiB));

        Assert.Equal(1, dispatcher.EnqueuedCount);
        dispatcher.FlushPending();
        Assert.Equal(16 * GiB, Assert.Single(delivered).CompletedBytes);
    }

    [Fact]
    public void ByteUpdatesAreRateLimitedButPhaseChangesAreDeliveredImmediately()
    {
        var dispatcher = new RecordingUiDispatcher();
        var delivered = new List<LocalAiModelSwitchProgress>();
        long now = 1_000;
        var progress = new CoalescingSwitchProgress(dispatcher, delivered.Add, () => now);

        progress.Report(new(LocalAiModelSwitchPhase.Verifying, 1, 10));
        progress.Report(new(LocalAiModelSwitchPhase.Verifying, 2, 10));
        now += CoalescingSwitchProgress.MinimumInterval - 1;
        progress.Report(new(LocalAiModelSwitchPhase.Verifying, 3, 10));
        progress.Report(new(LocalAiModelSwitchPhase.StoppingRuntime));
        now += CoalescingSwitchProgress.MinimumInterval;
        progress.Report(new(LocalAiModelSwitchPhase.StoppingRuntime));

        Assert.Equal(
            [(LocalAiModelSwitchPhase.Verifying, 1L), (LocalAiModelSwitchPhase.StoppingRuntime, 0L), (LocalAiModelSwitchPhase.StoppingRuntime, 0L)],
            delivered.Select(p => (p.Phase, p.CompletedBytes)));
    }

    [Fact]
    public void RejectedEnqueueDoesNotBlockLaterDeliveries()
    {
        var dispatcher = new RecordingUiDispatcher { RejectEnqueue = true };
        var delivered = new List<LocalAiModelSwitchProgress>();
        var progress = new CoalescingSwitchProgress(dispatcher, delivered.Add, () => 0);

        progress.Report(new(LocalAiModelSwitchPhase.Downloading, 1, 10));
        dispatcher.RejectEnqueue = false;
        progress.Report(new(LocalAiModelSwitchPhase.Downloading, 2, 10));

        Assert.Equal(2, Assert.Single(delivered).CompletedBytes);
    }
}
