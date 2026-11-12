namespace OpenClaw.SetupEngine.Tests;

public sealed class WizardAnswerGateTests
{
    [Fact]
    public void SecondBegin_IsIgnoredUntilTheFirstEnds()
    {
        var gate = new WizardAnswerGate();

        Assert.True(gate.TryBegin(out var firstLease));
        Assert.False(gate.AllowsContinue);
        Assert.False(gate.TryBegin(out _));

        Assert.True(gate.End(firstLease));

        Assert.True(gate.AllowsContinue);
        Assert.True(gate.TryBegin(out _));
    }

    [Fact]
    public void InvalidatedLease_CannotReleaseANewerAnswer()
    {
        var gate = new WizardAnswerGate();
        Assert.True(gate.TryBegin(out var staleLease));

        gate.Invalidate();
        Assert.True(gate.TryBegin(out var currentLease));

        Assert.False(gate.End(staleLease));
        Assert.False(gate.AllowsContinue);
        Assert.True(gate.End(currentLease));
        Assert.True(gate.AllowsContinue);
    }
}
