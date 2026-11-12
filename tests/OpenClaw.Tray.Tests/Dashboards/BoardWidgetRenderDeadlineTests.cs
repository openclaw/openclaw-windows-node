using System.Net;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;

namespace OpenClaw.Tray.Tests.Dashboards;

public sealed class BoardWidgetRenderDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDocumentRecoveryWithoutAcknowledgementGetsAFullDeliveryDeadline(bool timeout)
    {
        var clock = new ManualClock();
        var expired = 0;
        using var deadline = new BoardWidgetRenderDeadline(clock.Schedule, () => expired++);
        using var loader = new BoardWidgetDocumentLoader(new RecoveringHandler(timeout));
        var url = new Uri("http://127.0.0.1:18789/__openclaw__/board/k/w/index.html?bt=synthetic");

        // The proxy is already ready. Two external fetch failures take longer than
        // startup's budget, but no document has entered the sandbox yet.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<BoardWidgetDocumentException>(() => loader.LoadAsync(url, CancellationToken.None));
            Assert.Equal(BoardWidgetDocumentFailure.Unavailable, error.Failure);
            clock.Advance(BoardWidgetDocumentLoader.DefaultTimeout);
        }
        Assert.Empty(clock.Timers);
        Assert.Equal(0, expired);
        Assert.Equal("<p>recovered</p>", await loader.LoadAsync(url, CancellationToken.None));

        deadline.Start("recovered-render");
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(BoardWidgetDocumentLoader.DefaultTimeout, timer.Due - clock.Now);
        clock.Advance(BoardWidgetDocumentLoader.DefaultTimeout - TimeSpan.FromTicks(1));
        Assert.Equal(0, expired);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, expired);
        Assert.False(deadline.TryComplete("recovered-render"));
        timer.FireEvenIfCancelled();
        Assert.Equal(1, expired);
    }

    [Fact]
    public void CurrentAcknowledgementCancelsDeadlineButUnknownOrDuplicateMessagesDoNotComplete()
    {
        var clock = new ManualClock();
        var expired = 0;
        using var deadline = new BoardWidgetRenderDeadline(clock.Schedule, () => expired++);
        deadline.Start("current");
        var timer = Assert.Single(clock.Timers);
        Assert.False(deadline.TryComplete("foreign"));
        Assert.True(deadline.TryComplete("current"));
        Assert.False(deadline.TryComplete("current"));
        Assert.True(timer.Cancelled);
        timer.FireEvenIfCancelled();
        clock.Advance(BoardWidgetDocumentLoader.DefaultTimeout);
        Assert.Equal(0, expired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelOrDisposeRejectsQueuedTimeoutAndLateAcknowledgement(bool dispose)
    {
        var clock = new ManualClock();
        var expired = 0;
        using var deadline = new BoardWidgetRenderDeadline(clock.Schedule, () => expired++);
        deadline.Start("cancelled");
        var timer = Assert.Single(clock.Timers);
        if (dispose) deadline.Dispose(); else deadline.Cancel();
        timer.FireEvenIfCancelled();
        Assert.False(deadline.TryComplete("cancelled"));
        Assert.True(timer.Cancelled);
        Assert.Equal(0, expired);
    }

    [Fact]
    public void ReplacementDeliveryRejectsOldTimeoutAndAcknowledgementWithoutCancellingNewWait()
    {
        var clock = new ManualClock();
        var expired = 0;
        using var deadline = new BoardWidgetRenderDeadline(clock.Schedule, () => expired++);
        deadline.Start("old");
        var old = Assert.Single(clock.Timers);
        clock.Advance(BoardWidgetDocumentLoader.DefaultTimeout / 2);
        deadline.Start("new");
        Assert.True(old.Cancelled);
        old.FireEvenIfCancelled();
        Assert.False(deadline.TryComplete("old"));
        Assert.Equal(0, expired);
        clock.Advance(BoardWidgetDocumentLoader.DefaultTimeout);
        Assert.Equal(1, expired);
        Assert.False(deadline.TryComplete("new"));
    }

    [Fact]
    public void NativeHostArmsAtDeliveryAndInvalidatesWaitsAtTeardown()
    {
        // Ownership guard until mandatory native late-fetch/ack tests cover the
        // host-to-deadline seam. The pure tests above exercise deadline behavior.
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Dashboards", "BoardWidgetWebHost.cs"));
        var delivery = source[source.IndexOf("private void DeliverDocument()", StringComparison.Ordinal)..
            source.IndexOf("private void StartTimers", StringComparison.Ordinal)];
        Assert.Contains("_renderDeadline.Start(renderId)", delivery);
        Assert.True(delivery.IndexOf("_renderDeadline.Start(renderId)", StringComparison.Ordinal) <
            delivery.IndexOf("PostJson(BoardWidgetHostPage.ResourceReadyMessage", StringComparison.Ordinal));
        Assert.Contains("when _renderDeadline.TryComplete(loaded.RenderId)", source);
        var stop = source[source.IndexOf("private void StopTimers()", StringComparison.Ordinal)..];
        Assert.Contains("_renderDeadline.Cancel()", stop);
        Assert.Contains("StopTimers()", stop[stop.IndexOf("private void Teardown", StringComparison.Ordinal)..]);
        var timeout = source[source.IndexOf("private void OnRenderTimedOut()", StringComparison.Ordinal)..
            source.IndexOf("private sealed class RenderTimerLease", StringComparison.Ordinal)];
        Assert.Contains("Teardown(closeWebView: true)", timeout);
        Assert.Contains("BoardWidgetWebHostStatus.Failed", timeout);
    }

    private sealed class RecoveringHandler(bool timeout) : HttpMessageHandler
    {
        private int _attempt;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_attempt > 2)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>recovered</p>") });
            return timeout
                ? Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    // Supplies only elapsed time and queued callbacks, never the owner's result.
    private sealed class ManualClock
    {
        public TimeSpan Now { get; private set; }
        public List<Timer> Timers { get; } = [];
        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var timer = new Timer(Now + delay, action);
            Timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Now += elapsed;
            foreach (var timer in Timers.Where(timer => !timer.Cancelled && timer.Due <= Now).ToArray())
                timer.FireEvenIfCancelled();
        }
        public sealed class Timer(TimeSpan due, Action action) : IDisposable
        {
            public TimeSpan Due { get; } = due;
            public bool Cancelled { get; private set; }
            public void FireEvenIfCancelled() => action();
            public void Dispose() => Cancelled = true;
        }
    }
}
