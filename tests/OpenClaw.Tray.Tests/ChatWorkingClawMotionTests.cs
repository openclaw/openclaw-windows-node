using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatWorkingClawMotionTests
{
    // Golden percentages from upstream components.css, not a second animation implementation.
    [Theory]
    [InlineData(0, 0, -10)]
    [InlineData(.144, 0, -10)]
    [InlineData(.24, -4, -26)]
    [InlineData(.384, 3, 4)]
    [InlineData(.528, -4, -24)]
    [InlineData(.624, -4, -24)]
    [InlineData(.768, 3, 4)]
    [InlineData(1.008, 0, -10)]
    [InlineData(2.4, 0, -10)]
    public void DefaultLoop_HasTwoSnipsAndTheUpstreamRest(double seconds, double body, double jaw)
    {
        var sample = ChatWorkingClawMotion.At(seconds, true, false);
        Assert.Equal(body, sample.Body.Rotation, 6);
        Assert.Equal(jaw, sample.Jaw, 6);
        Assert.Equal(0, sample.Body.X);
        Assert.Equal(0, sample.Body.Y);
        Assert.Equal(1, sample.Body.Scale);
    }

    [Fact]
    public void BetweenKeyframes_UsesCssEaseOutNotLinearOrSine()
    {
        Assert.Equal(.684643, ChatWorkingClawMotion.Bezier(.5, 0, 0, .58, 1), 5);
        Assert.Equal(-20.9543, ChatWorkingClawMotion.At(.192, true, false).Jaw, 4);
    }

    [Theory]
    [InlineData(0, 5.04, -4, 0, 0, 1, -1, null)]
    [InlineData(1, 4.904, 3, 0, 0, 1, 1, 4d)]
    [InlineData(2, 5.25, 0, 0, 0, 1, 1, null)]
    [InlineData(3, 5.424, 4, 8, 0, 1, 1, 6d)]
    [InlineData(4, 5.43, -240, 0, -3, 1, 1, null)]
    [InlineData(5, 5.52, 0, 0, 0, 1.08, 1, null)]
    [InlineData(6, 5.46, 8, 0, 0, 1, 1, 2d)]
    [InlineData(7, 5.664, 0, 0, 5, .72, 1, -2d)]
    [InlineData(8, 6.48, 15, 0, 1.5, 1, 1, -21d)]
    [InlineData(9, 5.52, -14, 0, 0, 1, 1, -16d)]
    [InlineData(10, 5.448, 0, 2.5, 0, 1, 1, 8d)]
    [InlineData(11, 6.192, 3, 0, 0, 1, 1, 4d)]
    public void EverySurprise_MatchesItsCssPeakAndResumesTheUninterruptedLoop(
        int surprise, double seconds, double rotation, double x, double y, double scale, double flip, double? jaw)
    {
        var sample = ChatWorkingClawMotion.At(seconds, true, false, surprise);
        Assert.Equal(rotation, sample.Body.Rotation, 6);
        Assert.Equal(x, sample.Body.X, 6);
        Assert.Equal(y, sample.Body.Y, 6);
        Assert.Equal(scale, sample.Body.Scale, 6);
        Assert.Equal(flip, sample.Flip, 6);
        if (jaw is not null) Assert.Equal(jaw.Value, sample.Jaw, 6);
        if (surprise == 2) Assert.Equal(180, sample.Body.RotationY, 6);
        if (surprise == 3) Assert.Equal(1, sample.SparkOpacity, 6);
        Assert.Equal(ChatWorkingClawMotion.At(4.79, true, false), ChatWorkingClawMotion.At(4.79, true, false, surprise));
        Assert.Equal(ChatWorkingClawMotion.At(8.1, true, false), ChatWorkingClawMotion.At(8.1, true, false, surprise));
    }

    [Fact]
    public void SurpriseSelection_UsesFivePercentThresholdAndUpstreamOrder()
    {
        const uint emptyFnvHash = 2166136261;
        for (var index = 0; index < 12; index++)
        {
            var salt = emptyFnvHash ^ (uint)(index * 1000 + 49);
            Assert.Equal(index, ChatWorkingClawMotion.SelectSurprise("", salt));
            Assert.Equal(-1, ChatWorkingClawMotion.SelectSurprise("", salt, eligible: false));
            Assert.Equal(-1, ChatWorkingClawMotion.SelectSurprise("", emptyFnvHash ^ (uint)(index * 1000 + 50)));
        }
    }

    [Fact]
    public void ReducedMotionAndSettledRecap_DoNotRunAnySurprise()
    {
        for (var surprise = 0; surprise < 12; surprise++)
        {
            Assert.Equal(new(new(Scale: 1), -10), ChatWorkingClawMotion.At(5.5, true, true, surprise));
            Assert.Equal(new(new(Rotation: 8, Scale: 1), -4), ChatWorkingClawMotion.At(5.5, false, false, surprise));
        }
    }
}
