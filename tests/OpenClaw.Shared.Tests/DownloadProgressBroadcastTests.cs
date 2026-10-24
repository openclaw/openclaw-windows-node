using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class DownloadProgressBroadcastTests
{
    private sealed class Recorder : IProgress<(long downloaded, long total)>
    {
        public List<(long downloaded, long total)> Reports { get; } = [];
        public void Report((long downloaded, long total) value) => Reports.Add(value);
    }

    [Fact]
    public void LateSubscriber_ReceivesLatestThenLiveReports()
    {
        var broadcast = new DownloadProgressBroadcast();
        var first = new Recorder();
        broadcast.Subscribe(first);
        broadcast.Report((10, 100));

        var joiner = new Recorder();
        broadcast.Subscribe(joiner);
        broadcast.Report((20, 100));

        Assert.Equal([(10L, 100L), (20L, 100L)], first.Reports);
        Assert.Equal([(10L, 100L), (20L, 100L)], joiner.Reports);
    }

    [Fact]
    public void Unsubscribed_StopsReceiving_AndResetDropsStaleProgress()
    {
        var broadcast = new DownloadProgressBroadcast();
        var left = new Recorder();
        broadcast.Subscribe(left);
        broadcast.Report((50, 100));
        broadcast.Unsubscribe(left);
        broadcast.Report((60, 100));
        Assert.Equal([(50L, 100L)], left.Reports);

        broadcast.Reset();
        var next = new Recorder();
        broadcast.Subscribe(next);
        Assert.Empty(next.Reports);
    }
}
