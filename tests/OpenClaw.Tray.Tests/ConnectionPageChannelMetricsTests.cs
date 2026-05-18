using OpenClaw.Shared;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavior tests for the Connection page channel-count chip. Guards the
/// regression where one running channel rendered as "0/1" yellow because
/// the chip required <c>IsLinked == true</c> AND a narrower healthy-status
/// whitelist than the canonical <see cref="ChannelHealth.IsHealthyStatus"/>.
/// </summary>
public sealed class ConnectionPageChannelMetricsTests
{
    [Fact]
    public void Null_ReturnsZero()
    {
        Assert.Equal(0, ConnectionPageChannelMetrics.CountHealthyChannels(null));
    }

    [Fact]
    public void Empty_ReturnsZero()
    {
        Assert.Equal(0, ConnectionPageChannelMetrics.CountHealthyChannels(System.Array.Empty<ChannelHealth>()));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("connected")]
    [InlineData("running")]
    [InlineData("active")]
    [InlineData("ready")]
    public void HealthyStatuses_AreCounted_RegardlessOfIsLinked(string status)
    {
        // The chip used to require IsLinked=true. Several channel types are
        // functional without an OAuth-style "linked" flag, so excluding them
        // produced "0/1 channels" yellow on a perfectly happy gateway.
        var channels = new[]
        {
            new ChannelHealth { Name = "ch", Status = status, IsLinked = false },
        };

        Assert.Equal(1, ConnectionPageChannelMetrics.CountHealthyChannels(channels));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("disconnected")]
    [InlineData("stale")]
    [InlineData("stopped")]
    [InlineData("configured")]
    [InlineData("not configured")]
    [InlineData("connecting")]
    [InlineData("reconnecting")]
    [InlineData("pending")]
    [InlineData("idle")]
    [InlineData("paused")]
    public void NonHealthyStatuses_AreNotCounted(string status)
    {
        var channels = new[]
        {
            new ChannelHealth { Name = "ch", Status = status, IsLinked = true },
        };

        Assert.Equal(0, ConnectionPageChannelMetrics.CountHealthyChannels(channels));
    }

    [Fact]
    public void MixedSet_CountsOnlyHealthy()
    {
        // Real-world snapshot the chip used to mishandle: one running
        // channel (linked=false) + one stopped + one error → expect 1.
        var channels = new[]
        {
            new ChannelHealth { Name = "telegram", Status = "running", IsLinked = false },
            new ChannelHealth { Name = "discord",  Status = "stopped", IsLinked = true },
            new ChannelHealth { Name = "slack",    Status = "error",   IsLinked = true,  Error = "auth expired" },
        };

        Assert.Equal(1, ConnectionPageChannelMetrics.CountHealthyChannels(channels));
    }

    [Fact]
    public void StatusComparison_IsCaseInsensitive()
    {
        // Matches ChannelHealth.IsHealthyStatus semantics: status strings come
        // from gateway JSON and casing varies in the wild.
        var channels = new[]
        {
            new ChannelHealth { Name = "a", Status = "READY",   IsLinked = false },
            new ChannelHealth { Name = "b", Status = "Running", IsLinked = false },
            new ChannelHealth { Name = "c", Status = "OK",      IsLinked = true  },
        };

        Assert.Equal(3, ConnectionPageChannelMetrics.CountHealthyChannels(channels));
    }
}
