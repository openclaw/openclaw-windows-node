namespace OpenClaw.SetupEngine.Tests;

public sealed class OnboardingMascotPlaybackTests
{
    [Fact]
    public void PausingFreezesSleepAndClearsTapBurst()
    {
        var clock = new Clock();
        var playback = New(clock);
        playback.Tap();
        playback.Tap();
        clock.Advance(10);
        playback.SetActive(false);
        clock.Advance(1000);
        Assert.Equal(10, playback.Elapsed);
        Assert.False(playback.Tap());
        Assert.False(playback.WakeUp());
        Assert.False(playback.PointerActivity());
        playback.Complete();
        Assert.Equal(OnboardingMascotClip.Idle, playback.Clip);
        playback.SetActive(true);
        Assert.False(playback.IsDozing);
        playback.Tap();
        Assert.Contains(playback.Clip, new[] { OnboardingMascotClip.Hop, OnboardingMascotClip.Wave, OnboardingMascotClip.Wink });
    }

    [Fact]
    public void TapLadderDoesNotRepeatSinglesAndReturnsFromDizzyThroughRecovery()
    {
        var clock = new Clock();
        var playback = New(clock);
        playback.Tap();
        var first = playback.Clip;
        playback.Tap();
        Assert.NotEqual(first, playback.Clip);
        playback.Tap();
        Assert.Equal(OnboardingMascotClip.HeartBurst, playback.Clip);
        for (var i = 0; i < 3; i++) playback.Tap();
        Assert.Equal(OnboardingMascotClip.Dizzy, playback.Clip);
        clock.Advance(4);
        playback.Tap();
        Assert.Equal(OnboardingMascotClip.Dizzy, playback.Clip);
        playback.Complete();
        Assert.Equal(OnboardingMascotClip.Shake, playback.Clip);
        playback.Complete();
        Assert.Equal(OnboardingMascotClip.Idle, playback.Clip);
        clock.Advance(4);
        playback.Tap();
        Assert.Contains(playback.Clip, new[] { OnboardingMascotClip.Hop, OnboardingMascotClip.Wave, OnboardingMascotClip.Wink });
    }

    [Theory]
    [InlineData(12, 45, 80)]
    [InlineData(23, 24.75, 44)]
    [InlineData(4, 24.75, 44)]
    [InlineData(5, 45, 80)]
    public void SleepRequiresAnInteractiveIdleHostAndPointerDoesNotWake(int hour, double minimum, double maximum)
    {
        var clock = new Clock();
        var playback = new OnboardingMascotPlayback(clock, new Random(123), hour);
        playback.SetActive(true);
        clock.Advance(minimum);
        Assert.False(playback.IsDozing);
        clock.Advance(maximum - minimum + .01);
        playback.Complete();
        Assert.True(playback.IsDozing);
        Assert.Equal(OnboardingMascotAccessory.Nightcap, playback.Accessory);
        Assert.False(playback.PointerActivity());
        Assert.True(playback.WakeUp());
        Assert.Equal(OnboardingMascotClip.Startle, playback.Clip);
        Assert.False(playback.IsDozing);
        Assert.Equal(OnboardingMascotAccessory.None, playback.Accessory);
        // Drag wake-ups are not taps, even when called repeatedly.
        for (var i = 0; i < 100; i++)
        {
            playback.WakeUp();
            clock.Advance(1);
        }
        Assert.False(playback.IsDozing);
        playback.Tap();
        Assert.NotEqual(OnboardingMascotClip.HeartBurst, playback.Clip);
        playback.Interactive = false;
        clock.Advance(100);
        playback.Complete();
        Assert.False(playback.IsDozing);
        Assert.False(playback.Tap());
    }

    [Fact]
    public void HidingAndReshowingASleepingMascotDoesNotWakeIt()
    {
        var clock = new Clock();
        var playback = New(clock);
        clock.Advance(81);
        playback.Complete();
        Assert.True(playback.IsDozing);
        playback.SetActive(false);
        clock.Advance(1000);
        playback.SetActive(true);
        Assert.Equal(OnboardingMascotClip.Sleepy, playback.Clip);
        Assert.Equal(OnboardingMascotAccessory.Nightcap, playback.Accessory);
        Assert.Equal(81, playback.Elapsed);
    }

    [Fact]
    public void PointerActivityPostponesSleepWithoutChangingMood()
    {
        var clock = new Clock();
        var playback = New(clock);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(playback.PointerActivity());
            clock.Advance(1);
        }
        Assert.False(playback.IsDozing);
        Assert.Equal(OnboardingMascotMood.Idle, playback.Mood);
    }

    [Fact]
    public void WorkingHatWinsOverAccessoriesUntilItsExitCompletes()
    {
        var clock = new Clock();
        var playback = New(clock);
        playback.Accessory = OnboardingMascotAccessory.GradCap;
        playback.SetMood(OnboardingMascotMood.Working);
        Assert.Equal(OnboardingMascotClip.DonHardHat, playback.Clip);
        Assert.True(playback.HardHat);
        Assert.Equal(OnboardingMascotAccessory.None, playback.Accessory);
        clock.Advance(.6);
        playback.SetMood(OnboardingMascotMood.Happy);
        Assert.Equal(OnboardingMascotClip.HatTip, playback.Clip);
        Assert.True(playback.HardHat);
        playback.Complete();
        Assert.False(playback.HardHat);
        Assert.Equal(OnboardingMascotAccessory.GradCap, playback.Accessory);
        playback.SetMood(OnboardingMascotMood.Working);
        clock.Advance(.1);
        playback.SetMood(OnboardingMascotMood.Sad);
        Assert.Equal(OnboardingMascotClip.Sigh, playback.Clip);
        Assert.False(playback.HardHat);
        playback.SetActive(false);
        playback.SetMood(OnboardingMascotMood.Working);
        Assert.Equal(OnboardingMascotClip.Working, playback.Clip);
        Assert.True(playback.HardHat);
        Assert.Equal(OnboardingMascotAccessory.None, playback.Accessory);
    }

    [Fact]
    public void ReducedMotionKeepsStaticExpressionsAndSuppressesEffectsForEveryClip()
    {
        foreach (var mood in Enum.GetValues<OnboardingMascotMood>())
        {
            foreach (var clip in Enum.GetValues<OnboardingMascotClip>())
            {
                var motion = OnboardingMascotMotion.Create(mood, clip, still: true);
                Assert.Equal(OnboardingMascotEffect.None, motion.Effect);
                Assert.All(motion.Channels.Values, expression => Assert.DoesNotContain("m.", expression));
                Assert.Equal("0", motion.Channels["Float"]);
                Assert.Equal("0", motion.Channels["Antenna"]);
                Assert.Equal("1", motion.Channels["Stretch"]);
                if (mood == OnboardingMascotMood.Working)
                    Assert.Equal("-28", motion.Channels["RightClaw"]);
                if (mood == OnboardingMascotMood.Sad)
                    Assert.Equal("-0.55", motion.Channels["Smile"]);
            }
        }
    }

    [Fact]
    public void MoodSelectedBeforeLoadStartsItsEntranceOnlyOnce()
    {
        var clock = new Clock();
        var playback = new OnboardingMascotPlayback(clock, new Random(123), 12);
        playback.SetMood(OnboardingMascotMood.Working);
        playback.SetActive(true);
        Assert.Equal(OnboardingMascotClip.DonHardHat, playback.Clip);
        clock.Advance(1);
        playback.Complete();
        Assert.Equal(OnboardingMascotClip.Working, playback.Clip);
        playback.SetActive(false);
        playback.SetActive(true);
        Assert.Equal(OnboardingMascotClip.Working, playback.Clip);
    }

    [Fact]
    public void AnimationExpressionsAreIndependentOfDisplayCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        var expected = OnboardingMascotMotion.Create(OnboardingMascotMood.Working, OnboardingMascotClip.WipeBrow, false).Channels;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var actual = OnboardingMascotMotion.Create(OnboardingMascotMood.Working, OnboardingMascotClip.WipeBrow, false).Channels;
            Assert.Equal(expected, actual);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(6.123233995736766E-17)]
    [InlineData(-1E-20)]
    [InlineData(1E20)]
    public void CompositionNumbersNeverRequireAnExponentParameter(double value)
    {
        var literal = OnboardingMascotMotion.Number(value);
        Assert.DoesNotContain("E", literal, StringComparison.OrdinalIgnoreCase);
        var parsed = double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(parsed - value), 0, Math.Max(1E-15, Math.Abs(value) * 1E-15));
    }

    [Fact]
    public void InvalidStateInputsFailExplicitly()
    {
        var playback = New(new Clock());
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.SetMood((OnboardingMascotMood)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Accessory = (OnboardingMascotAccessory)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => OnboardingMascotMotion.Duration((OnboardingMascotClip)999));
    }

    private static OnboardingMascotPlayback New(Clock clock)
    {
        var playback = new OnboardingMascotPlayback(clock, new Random(123), 12);
        playback.SetActive(true);
        return playback;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
