namespace OpenClaw.Shared.Tests;

public sealed class WizardAnswerGateTests
{
    [Fact]
    public void SecondBegin_IsIgnoredUntilTheFirstEnds()
    {
        var inFlight = 0;

        Assert.True(WizardAnswerGate.TryBegin(ref inFlight));
        Assert.False(WizardAnswerGate.AllowsContinue(inFlight));
        Assert.False(WizardAnswerGate.TryBegin(ref inFlight));

        WizardAnswerGate.End(ref inFlight);

        Assert.True(WizardAnswerGate.AllowsContinue(inFlight));
        Assert.True(WizardAnswerGate.TryBegin(ref inFlight));
    }
}
