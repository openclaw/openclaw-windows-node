using Microsoft.UI.Dispatching;
using OpenClaw.TestSupport;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Contract of TestSupport.WaitForRenderedConditionAsync itself. Its deadline is a thread-pool timer that
// can pass while the UI thread is busy producing the condition; these tests drive that deadline with a
// manual clock from inside UI work items, so the ordering is exact rather than timed.
[Collection(UICollection.Name)]
public sealed class RenderedConditionWaitTests(UIThreadFixture ui)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    [Fact]
    public Task DeadlinePassingDuringStalledUiWorkStillObservesTheQueuedResult() =>
        ui.RunOnUIAsync(async () =>
        {
            var clock = new ManualTimeProvider();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var ready = false;
            // Like a cold template load: one UI work item outlives the deadline, then queues the
            // work that produces the result. Only a drain after the deadline can observe it.
            Assert.True(dispatcher.TryEnqueue(() =>
            {
                clock.Advance(Budget);
                Assert.True(dispatcher.TryEnqueue(() => ready = true));
            }));

            await WaitForRenderedConditionAsync(() => ready, "result queued by stalled UI work",
                timeout: Budget, timeProvider: clock);
        });

    [Fact]
    public Task ConditionThatNeverHoldsStillTimesOut() =>
        ui.RunOnUIAsync(async () =>
        {
            var clock = new ManualTimeProvider();
            Assert.True(DispatcherQueue.GetForCurrentThread().TryEnqueue(() => clock.Advance(Budget)));

            var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() =>
                WaitForRenderedConditionAsync(() => false, "an unreachable condition",
                    timeout: Budget, timeProvider: clock));
            Assert.Equal("Timed out waiting for an unreachable condition.", failure.Message);
        });

    [Fact]
    public Task UiQueueThatNeverDrainsStillTimesOut() =>
        ui.RunOnUIAsync(async () =>
        {
            var clock = new ManualTimeProvider();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var advances = 0;
            // Normal-priority work that requeues itself starves the Low-priority drain. Two advances
            // pass the deadline and then the drain bound; stopping there lets a regressed, unbounded
            // drain fail on the message instead of hanging the UI collection.
            void Requeue()
            {
                if (++advances > 2) return;
                clock.Advance(Budget);
                dispatcher.TryEnqueue(Requeue);
            }
            Assert.True(dispatcher.TryEnqueue(Requeue));

            var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() =>
                WaitForRenderedConditionAsync(() => false, "a condition behind a busy queue",
                    timeout: Budget, timeProvider: clock));
            Assert.Equal("Timed out waiting for a condition behind a busy queue. " +
                "The UI dispatcher queue did not drain.", failure.Message);
        });
}
