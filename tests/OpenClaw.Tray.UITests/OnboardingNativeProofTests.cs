namespace OpenClaw.Tray.UITests;

public sealed class OnboardingNativeProofTests
{
    [Theory]
    [InlineData("Set up your AI", "", "", true)]
    [InlineData("Set up a local model", "", "", true)]
    [InlineData("Where should OpenClaw live?", "Where should OpenCIaw live?", "Where should OpenCIaw live?", true)]
    [InlineData("Where should OpenClaw live?", "OpenClaw setup Where should OpenCIaw live? Next", "Where should OpenCIaw live? Next", true)]
    [InlineData("Where should OpenClaw live?", "Where should OpenCIaw live?", "", false)]
    [InlineData("Where should OpenClaw live?", "", "Where should OpenCIaw live?", false)]
    [InlineData("Where should OpenClaw live?", "Where should OpenCIaw live?", "Where should OpenClaw live?", false)]
    [InlineData("Where should OpenClaw live?", "Where should OpenClaw live?", "Where should OpenCIaw live?", false)]
    [InlineData("Where should OpenClaw live?", "Where should OpenCIawful live?", "Where should OpenCIawful live?", false)]
    [InlineData("Where should OpenClaw live?", "Nowhere should OpenCIaw live?", "Nowhere should OpenCIaw live?", false)]
    [InlineData("Where should OpenClaw live?", "Where should Openclaw live?", "Where should Openclaw live?", false)]
    [InlineData("Where should OpenClaw live?", "Where should OpenClaw live?", "Where should OpenClaw live?", false)]
    [InlineData("Where should OpenCIaw live?", "Where should OpenCIaw live?", "Where should OpenCIaw live?", false)]
    [InlineData("Back", "Where should OpenCIaw live?", "Where should OpenCIaw live?", false)]
    public void ManualLabelException_RequiresExactExpectedTitleAndAmbiguityInBothPasses(
        string expected, string original, string scaled, bool allowed)
    {
        var error = Record.Exception(() =>
            OnboardingNativeProof.AssertManualLabelExceptions([expected], original, scaled));
        if (allowed)
            Assert.Null(error);
        else
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
    }

    [Theory]
    [InlineData("Where should OpenClaw live?")]
    [InlineData("Set up your AI")]
    public void ManualLabelException_RejectsAnyAdditionalUnapprovedMissingLabel(string allowed)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OnboardingNativeProof.AssertManualLabelExceptions(
                [allowed, "Next"], "Where should OpenCIaw live?", "Where should OpenCIaw live?"));
    }
}
