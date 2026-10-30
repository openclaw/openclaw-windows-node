using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PlaybackMuteStateTests
{
    [Fact]
    public void RequestedMuteBeforeCapture_IsRetainedAndAppliedToEveryNewCapture()
    {
        using var state = new PlaybackMuteState { IsMuted = true };
        Assert.True(state.IsMuted);
        var first = false;
        var second = false;
        using var firstCapture = state.RegisterCapture(value => first = value);
        using var secondCapture = state.RegisterCapture(value => second = value);
        Assert.True(first);
        Assert.True(second);

        state.IsMuted = false;
        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public void OverlappingPlayback_OnlyLastReleaseUnmutesCurrentAndFutureCaptures()
    {
        using var state = new PlaybackMuteState();
        using var first = state.Acquire();
        using var second = state.Acquire();
        var muted = false;
        using var capture = state.RegisterCapture(value => muted = value);
        Assert.True(muted);
        first.Dispose();
        first.Dispose();
        state.IsMuted = false;
        Assert.True(muted);
        Assert.True(state.IsMuted);

        second.Dispose();
        Assert.False(muted);
        Assert.False(state.IsMuted);
        var nextMuted = true;
        using var next = state.RegisterCapture(value => nextMuted = value);
        Assert.False(nextMuted);
    }

    [Fact]
    public void ManualMute_IsNotClearedByPlaybackLeaseRelease()
    {
        using var state = new PlaybackMuteState();
        var muted = false;
        using var capture = state.RegisterCapture(value => muted = value);
        using var playback = state.Acquire();
        state.IsMuted = true;
        playback.Dispose();
        Assert.True(muted);
        state.IsMuted = false;
        Assert.False(muted);
    }

    [Fact]
    public void EndingOneCapture_DoesNotUnmuteOrRetainIt_AndReplacementInheritsLease()
    {
        using var state = new PlaybackMuteState();
        var writes = new List<bool>();
        var activeMuted = false;
        using var ended = state.RegisterCapture(writes.Add);
        using var active = state.RegisterCapture(value => activeMuted = value);
        using var playback = state.Acquire();
        ended.Dispose();
        var replacementMuted = false;
        using var replacement = state.RegisterCapture(value => replacementMuted = value);
        Assert.True(activeMuted);
        Assert.True(replacementMuted);

        playback.Dispose();
        Assert.False(activeMuted);
        Assert.False(replacementMuted);
        Assert.Equal(new[] { false, true }, writes);
    }

    [Fact]
    public void SeparateVoiceServices_KeepTheirOwnOverlappingLeases()
    {
        using var node = new PlaybackMuteState();
        using var standalone = new PlaybackMuteState();
        using var nodePlayback = node.Acquire();
        using var standalonePlayback = standalone.Acquire();
        nodePlayback.Dispose();
        Assert.False(node.IsMuted);
        Assert.True(standalone.IsMuted);
        standalonePlayback.Dispose();
        Assert.False(standalone.IsMuted);
    }

    [Fact]
    public void Disposal_DropsRegistrationsAndMakesLateReleasesHarmless()
    {
        var state = new PlaybackMuteState();
        var writes = new List<bool>();
        using var capture = state.RegisterCapture(writes.Add);
        using var playback = state.Acquire();
        state.Dispose();
        state.Dispose();
        playback.Dispose();
        state.IsMuted = true;
        Assert.False(state.IsMuted);
        Assert.Equal(new[] { false, true }, writes);
        Assert.Throws<ObjectDisposedException>(() => state.Acquire());
        Assert.Throws<ObjectDisposedException>(() => state.RegisterCapture(_ => { }));
    }

    [Fact]
    public async Task RegistrationRacingLastRelease_CannotLeaveTheNewCaptureMuted()
    {
        using var state = new PlaybackMuteState();
        using var playback = state.Acquire();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var muted = false;
        var registration = Task.Run(() => state.RegisterCapture(value =>
        {
            muted = value;
            if (value)
            {
                entered.Set();
                Assert.True(proceed.Wait(TimeSpan.FromSeconds(5)));
            }
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var release = Task.Run(playback.Dispose);
            proceed.Set();
            using var capture = await registration;
            await release;
            Assert.False(muted);
        }
        finally { proceed.Set(); }
    }
}
