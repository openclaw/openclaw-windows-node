namespace OpenClaw.SetupEngine.Tests;

public sealed class OnboardingMascotAnimatorTests
{
    public static TheoryData<OnboardingMascotMood> Moods => new(Enum.GetValues<OnboardingMascotMood>());
    public static TheoryData<int> Gestures => new(Enum.GetValues<OnboardingMascotGesture>().Select(gesture => (int)gesture));

    [Theory]
    [MemberData(nameof(Moods))]
    public void ReducedMotion_IsStaticAndIgnoresAllInteractions(OnboardingMascotMood mood)
    {
        var animator = New();
        animator.SetMood(mood);
        var expected = OnboardingMascotPose.Static(mood);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(expected, animator.Advance(TimeSpan.FromDays(1), false));
            animator.HandleTap();
            animator.SetPointer(new(1, -1));
        }
        Assert.Equal(0, animator.ElapsedSeconds);
        Assert.Null(animator.ActiveGesture);
        Assert.Equal(0, expected.FloatOffset);
        Assert.Equal(OnboardingMascotEffect.None, expected.Effect);
    }

    [Fact]
    public void StaticExpressions_MatchSourceIncludingThreeIdenticalNeutralMoods()
    {
        Assert.Equal(9, Enum.GetValues<OnboardingMascotMood>().Length);
        Assert.Equal(OnboardingMascotPose.Static(OnboardingMascotMood.Idle), OnboardingMascotPose.Static(OnboardingMascotMood.Curious));
        Assert.Equal(OnboardingMascotPose.Static(OnboardingMascotMood.Idle), OnboardingMascotPose.Static(OnboardingMascotMood.Attentive));
        Assert.Equal(7, Enum.GetValues<OnboardingMascotMood>().Select(OnboardingMascotPose.Static).Distinct().Count());
        Assert.Equal(1, OnboardingMascotPose.Static(OnboardingMascotMood.Working).HardHat);
        Assert.Equal(-0.55, OnboardingMascotPose.Static(OnboardingMascotMood.Sad).MouthCurve);
        Assert.Equal(0.6, OnboardingMascotPose.Static(OnboardingMascotMood.Happy).MouthCurve);
        Assert.Equal(OnboardingMascotAccessory.Nightcap, OnboardingMascotPose.Static(OnboardingMascotMood.Sleepy).Accessory);
        Assert.Equal(30, OnboardingMascotPose.Static(OnboardingMascotMood.Celebrating).LeftClawDegrees);
        Assert.Equal(-30, OnboardingMascotPose.Static(OnboardingMascotMood.Celebrating).RightClawDegrees);
    }

    [Theory]
    [MemberData(nameof(Moods))]
    public void SeededSchedules_AreDeterministicAndAllChannelsStayBounded(OnboardingMascotMood mood)
    {
        var first = New();
        var second = New();
        first.SetMood(mood);
        second.SetMood(mood);
        for (var i = 0; i < 6000; i++)
        {
            if (i % 191 == 0)
            {
                first.HandleTap();
                second.HandleTap();
            }
            if (i % 163 == 0)
            {
                first.SetPointer(new(1, -1));
                second.SetPointer(new(1, -1));
            }
            if (i % 163 == 40)
            {
                first.SetPointer(null);
                second.SetPointer(null);
            }
            var pose = first.Advance(TimeSpan.FromSeconds(1d / 12), true);
            Assert.Equal(pose, second.Advance(TimeSpan.FromSeconds(1d / 12), true));
            AssertBounds(pose);
            for (var index = 0; index < OnboardingMascotParticles.Capacity; index++)
            {
                var particle = OnboardingMascotParticles.Sample(pose, index);
                Assert.InRange(particle.Opacity, 0, 1);
                Assert.InRange(particle.X - particle.Size, -24, 144);
                Assert.InRange(particle.Y - particle.Size, -24, 144);
            }
        }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentMicrobehaviorSchedules()
    {
        var first = New(1);
        var second = New(2);
        var differences = 0;
        for (var i = 0; i < 600; i++)
            if (first.Advance(TimeSpan.FromSeconds(0.1), true) != second.Advance(TimeSpan.FromSeconds(0.1), true))
                differences++;
        Assert.True(differences > 100);
    }

    [Fact]
    public void Clock_ClampsLongFramesAndDoesNotAdvanceWhilePausedOrResetForMoodChanges()
    {
        var animator = New();
        Assert.Equal(12, OnboardingMascotAnimator.FramesPerSecond);
        animator.Advance(TimeSpan.FromHours(2), true);
        Assert.Equal(0.1, animator.ElapsedSeconds);
        animator.Advance(TimeSpan.FromDays(1), false);
        Assert.Equal(0.1, animator.ElapsedSeconds);
        animator.SetMood(OnboardingMascotMood.Celebrating);
        animator.SetMood(OnboardingMascotMood.Sad);
        Assert.Equal(0.1, animator.ElapsedSeconds);
        animator.Advance(TimeSpan.FromSeconds(1d / 12), true);
        Assert.InRange(animator.ElapsedSeconds, 0.1833, 0.1834);
    }

    [Fact]
    public void Working_HatDropAndHammerStrikeMatchPinnedSourceTiming()
    {
        var animator = New();
        animator.SetMood(OnboardingMascotMood.Working);
        Assert.Equal(0, animator.Advance(TimeSpan.Zero, true).HardHat);
        Assert.Equal(0.5, Advance(animator, 0.275).HardHat, 8);
        Assert.Equal(1, Advance(animator, 0.275).HardHat);
        Assert.Equal(-34, OnboardingMascotAnimator.BasePose(OnboardingMascotMood.Working, 0.95 * 2.60).RightClawDegrees, 8);
        Assert.Equal(12, OnboardingMascotAnimator.BasePose(OnboardingMascotMood.Working, 0.95 * 2.72).RightClawDegrees, 8);
        var sparks = OnboardingMascotAnimator.BasePose(OnboardingMascotMood.Working, 0.95 * 2.80);
        Assert.Equal(OnboardingMascotEffect.Sparks, sparks.Effect);
        Assert.True(OnboardingMascotParticles.Sample(sparks, 0).Opacity > 0.99);
    }

    [Fact]
    public void Working_BrowWipeIsScheduledBetweenSixAndTwelveSecondsAndLastsTwo()
    {
        var animator = New();
        animator.SetMood(OnboardingMascotMood.Working);
        while (animator.ActiveGesture != OnboardingMascotGesture.WipeBrow && animator.ElapsedSeconds < 13)
            animator.Advance(TimeSpan.FromSeconds(0.1), true);
        Assert.Equal(OnboardingMascotGesture.WipeBrow, animator.ActiveGesture);
        Assert.InRange(animator.ElapsedSeconds, 6, 12.1);
        var wiping = Advance(animator, 1);
        Assert.Equal(OnboardingMascotEffect.Sweat, wiping.Effect);
        Assert.True(wiping.HappyEyes >= 0.7);
        Assert.Equal(OnboardingMascotEffect.Sparks, Advance(animator, 1.1).Effect);
    }

    [Fact]
    public void WorkingExit_TipsOnlyASeatedHatAndSuppressesRequestedHeadwear()
    {
        var animator = New();
        animator.SetAccessory(OnboardingMascotAccessory.GradCap);
        animator.SetMood(OnboardingMascotMood.Working);
        var pose = Advance(animator, 0.8);
        Assert.Equal(1, pose.HardHat);
        Assert.Equal(0, pose.AccessoryAmount);
        animator.SetMood(OnboardingMascotMood.Happy);
        Assert.Equal(OnboardingMascotGesture.HatTip, animator.ActiveGesture);
        Assert.Equal(1, animator.Advance(TimeSpan.Zero, true).HardHat);
        Assert.Equal(0, Advance(animator, 0.5).AccessoryAmount);
        pose = Advance(animator, 0.5);
        Assert.Equal(0, pose.HardHat);
        Assert.Equal(1, pose.AccessoryAmount);
        animator.SetMood(OnboardingMascotMood.Working);
        Advance(animator, 0.1);
        animator.SetMood(OnboardingMascotMood.Sad);
        Assert.Equal(OnboardingMascotGesture.Sigh, animator.ActiveGesture);
        Assert.Equal(0, animator.Advance(TimeSpan.Zero, true).HardHat);
    }

    [Fact]
    public void Headwear_UsesHalfSecondCubicEntranceAndStaticWorkingWins()
    {
        var animator = New();
        animator.SetAccessory(OnboardingMascotAccessory.GradCap);
        Assert.Equal(0, animator.Advance(TimeSpan.Zero, true).AccessoryAmount);
        Assert.Equal(0.875, Advance(animator, 0.25).AccessoryAmount, 8);
        Assert.Equal(1, Advance(animator, 0.25).AccessoryAmount);
        animator.SetAccessory(OnboardingMascotAccessory.None);
        Assert.Equal(OnboardingMascotAccessory.None, animator.Advance(TimeSpan.Zero, true).Accessory);
        animator.SetAccessory(OnboardingMascotAccessory.Nightcap);
        animator.SetMood(OnboardingMascotMood.Working);
        var pose = animator.Advance(TimeSpan.Zero, false);
        Assert.Equal(1, pose.HardHat);
        Assert.Equal(0, pose.AccessoryAmount);
        animator.SetMood(OnboardingMascotMood.Celebrating);
        pose = animator.Advance(TimeSpan.Zero, false);
        Assert.Equal(0, pose.HardHat);
        Assert.Equal(OnboardingMascotAccessory.Nightcap, pose.Accessory);
        Assert.Equal(1, pose.AccessoryAmount);
        Assert.Equal(0, animator.Advance(TimeSpan.FromSeconds(0.1), true).HardHat);
    }

    [Fact]
    public void Pointer_UsesExponentialBlendingAndBoundedDirection()
    {
        var animator = New();
        animator.SetPointer(new(1000, -1000));
        var pose = animator.Advance(TimeSpan.FromSeconds(0.1), true);
        Assert.Equal(1 - Math.Exp(-0.9), pose.Gaze.X, 10);
        Assert.Equal(-(1 - Math.Exp(-0.9)), pose.Gaze.Y, 10);
        animator.SetPointer(null);
        var next = animator.Advance(TimeSpan.FromSeconds(0.1), true);
        Assert.Equal(pose.Gaze.X * Math.Exp(-0.9), next.Gaze.X, 10);
    }

    [Fact]
    public void EntranceWave_StartsAfterPointNineSeconds()
    {
        var animator = New();
        Advance(animator, 0.8);
        Assert.Null(animator.ActiveGesture);
        Advance(animator, 0.11);
        Assert.Equal(OnboardingMascotGesture.Wave, animator.ActiveGesture);
    }

    [Fact]
    public void ClickLadder_NonrepeatingSinglesHeartsDizzyExtensionThenRecovery()
    {
        var animator = New();
        animator.Advance(TimeSpan.Zero, true);
        animator.HandleTap();
        var first = animator.ActiveGesture;
        Assert.Contains(first!.Value, new[] { OnboardingMascotGesture.Hop, OnboardingMascotGesture.Wave, OnboardingMascotGesture.Wink });
        animator.HandleTap();
        Assert.NotEqual(first, animator.ActiveGesture);
        animator.HandleTap();
        Assert.Equal(OnboardingMascotGesture.HeartBurst, animator.ActiveGesture);
        var love = Advance(animator, 0.4);
        Assert.Equal(OnboardingMascotEffect.Hearts, love.Effect);
        Assert.True(love.Blush > 0.8);
        animator.HandleTap();
        animator.HandleTap();
        animator.HandleTap();
        Assert.Null(animator.ActiveGesture);
        Assert.Equal(1, Advance(animator, 0.3).Dizzy);
        Advance(animator, 1);
        animator.HandleTap();
        Assert.Equal(1, animator.Advance(TimeSpan.Zero, true).Dizzy);
        Assert.True(Advance(animator, 1.5).Dizzy > 0);
        Advance(animator, 1);
        Assert.Equal(OnboardingMascotGesture.Shake, animator.ActiveGesture);
    }

    [Theory]
    [InlineData(12, 45, 80)]
    [InlineData(23, 24.75, 44)]
    [InlineData(4, 24.75, 44)]
    [InlineData(5, 45, 80)]
    public void IdleSleepsOnlyWithWakePathAndPointerNeverWakesIt(int hour, double minimum, double maximum)
    {
        var animator = new OnboardingMascotAnimator(123, hour, true);
        while (!animator.IsDozing && animator.ElapsedSeconds <= maximum + 0.1)
            animator.Advance(TimeSpan.FromSeconds(0.1), true);
        Assert.True(animator.IsDozing);
        Assert.InRange(animator.ElapsedSeconds, minimum, maximum + 0.1);
        animator.SetPointer(new(1, 1));
        var pose = Advance(animator, 0.3);
        Assert.True(animator.IsDozing);
        Assert.Equal(OnboardingMascotAccessory.Nightcap, pose.Accessory);
        Assert.Equal(OnboardingMascotEffect.Zzz, pose.Effect);
        animator.HandleTap();
        Assert.False(animator.IsDozing);
        Assert.Equal(OnboardingMascotGesture.Startle, animator.ActiveGesture);
        Assert.Equal(OnboardingMascotAccessory.None, Advance(animator, 0.1).Accessory);
        var passive = new OnboardingMascotAnimator(123, hour, false);
        Advance(passive, 100);
        Assert.False(passive.IsDozing);
        passive.HandleTap();
        Assert.NotEqual(OnboardingMascotGesture.Startle, passive.ActiveGesture);
    }

    [Fact]
    public void PointerActivityDefersSleepWithoutChangingTheIdleMood()
    {
        var animator = New();
        for (var i = 0; i < 1000; i++)
        {
            animator.SetPointer(new(0.2, 0.1));
            animator.Advance(TimeSpan.FromSeconds(0.1), true);
        }
        Assert.False(animator.IsDozing);
        Assert.Equal(OnboardingMascotMood.Idle, animator.Mood);
    }

    [Theory]
    [MemberData(nameof(Gestures))]
    public void EveryClip_IsNontrivialAndClampsEveryChannel(int gestureValue)
    {
        var gesture = (OnboardingMascotGesture)gestureValue;
        var changes = 0;
        foreach (var mood in Enum.GetValues<OnboardingMascotMood>())
        {
            for (var i = 0; i <= 100; i++)
            {
                var baseline = OnboardingMascotAnimator.BasePose(mood, i * 0.1);
                var pose = baseline;
                gesture.Apply(ref pose, i / 100d);
                if (pose != baseline)
                    changes++;
                AssertBounds(pose.Clamp());
            }
        }
        Assert.True(changes > 0);
        Assert.InRange(gesture.Duration(), 0.6, 2.4);
    }

    [Fact]
    public void GestureLandmarks_MatchAllSixteenPinnedClips()
    {
        Assert.Equal(16, Enum.GetValues<OnboardingMascotGesture>().Length);
        Assert.Equal(-28, Clip(OnboardingMascotGesture.Wave, 0.5).RightClawDegrees, 8);
        Assert.Equal(-9, Clip(OnboardingMascotGesture.Hop, 0.5).FloatOffset, 8);
        Assert.Equal(0, Clip(OnboardingMascotGesture.Wink, 0.5).RightEyeOpenness);
        Assert.Equal(38, Clip(OnboardingMascotGesture.Celebrate, 0.5).LeftClawDegrees);
        Assert.Equal(0.9, Clip(OnboardingMascotGesture.HeartBurst, 0.5).Blush);
        Assert.Equal(-6, Clip(OnboardingMascotGesture.Peek, 0.25).BodyTilt);
        Assert.Equal(0.87, Clip(OnboardingMascotGesture.Sneeze, 0.5).BodyStretch, 8);
        Assert.Equal(1.55, Clip(OnboardingMascotGesture.AntennaZap, 0.5).GlowScale);
        Assert.Equal(0.945, Clip(OnboardingMascotGesture.Sigh, 0.75).BodyStretch, 8);
        Assert.Equal(0.9, Clip(OnboardingMascotGesture.Yawn, 0.5).MouthRound);
        Assert.Equal(-5, Clip(OnboardingMascotGesture.Startle, 0.2).FloatOffset);
        Assert.Equal(5 * (1 - 1d / 12), Clip(OnboardingMascotGesture.Shake, 1d / 12).BodyTilt, 8);
        Assert.Equal(-8, Clip(OnboardingMascotGesture.ClawSnap, 0.35).LeftClawDegrees);
        var don = new OnboardingMascotPose { HardHat = 1 };
        OnboardingMascotGesture.DonHardHat.Apply(ref don, 0.275);
        Assert.Equal(0.5, don.HardHat, 8);
        Assert.Equal(38, Clip(OnboardingMascotGesture.WipeBrow, 0.5).LeftClawDegrees, 8);
        Assert.Equal(0.5, Clip(OnboardingMascotGesture.HatTip, 0.775).HardHat, 8);
    }

    [Fact]
    public void InvalidInputs_FailExplicitly()
    {
        var animator = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.SetMood((OnboardingMascotMood)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.SetAccessory((OnboardingMascotAccessory)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.SetPointer(new(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.Advance(TimeSpan.FromSeconds(-1), true));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnboardingMascotPose.Static((OnboardingMascotMood)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnboardingMascotParticles.Sample(new(), 6));
    }

    private static OnboardingMascotAnimator New(ulong seed = 123) => new(seed, 12, true);
    private static OnboardingMascotPose Clip(OnboardingMascotGesture gesture, double progress)
    {
        var pose = new OnboardingMascotPose();
        gesture.Apply(ref pose, progress);
        return pose;
    }

    private static OnboardingMascotPose Advance(OnboardingMascotAnimator animator, double seconds)
    {
        var pose = animator.Advance(TimeSpan.Zero, true);
        while (seconds > 0.0000001)
        {
            var dt = Math.Min(seconds, 0.1);
            pose = animator.Advance(TimeSpan.FromTicks((long)Math.Round(dt * TimeSpan.TicksPerSecond)), true);
            seconds -= dt;
        }
        return pose;
    }

    private static void AssertBounds(OnboardingMascotPose pose)
    {
        Assert.InRange(pose.FloatOffset, -12, 2);
        Assert.InRange(pose.AntennaDegrees, -14, 14);
        Assert.InRange(pose.AntennaDroop, 0, 1);
        Assert.InRange(pose.LeftClawDegrees, -45, 45);
        Assert.InRange(pose.RightClawDegrees, -45, 45);
        Assert.InRange(pose.EyeGlowOpacity, 0, 1);
        Assert.InRange(pose.GlowScale, 0.5, 1.6);
        Assert.InRange(pose.LeftEyeOpenness, 0, 1);
        Assert.InRange(pose.RightEyeOpenness, 0, 1);
        Assert.InRange(pose.HappyEyes, 0, 1);
        Assert.InRange(pose.Gaze.X, -1.2, 1.2);
        Assert.InRange(pose.Gaze.Y, -1.2, 1.2);
        Assert.InRange(pose.MouthCurve, -1, 1);
        Assert.InRange(pose.MouthOpen, 0, 1);
        Assert.InRange(pose.MouthRound, 0, 1);
        Assert.InRange(pose.Blush, 0, 1);
        Assert.InRange(pose.HardHat, 0, 1);
        Assert.InRange(pose.AccessoryAmount, 0, 1);
        Assert.InRange(pose.BodyTilt, -8, 8);
        Assert.InRange(pose.BodyStretch, 0.86, 1.05);
        Assert.InRange(pose.Dizzy, 0, 1);
        Assert.InRange(pose.DizzyPhase, 0, 1);
        Assert.InRange(pose.EffectPhase, 0, 1);
    }
}
