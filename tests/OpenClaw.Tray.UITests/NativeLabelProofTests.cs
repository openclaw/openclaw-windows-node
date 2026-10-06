namespace OpenClaw.Tray.UITests;

public sealed class NativeLabelProofTests
{
    [Theory]
    [InlineData("Connect your AI", "Connect your Al", true)]
    [InlineData("Local AI on this PC", "Local Al on this PC", true)]
    [InlineData("Review Local AI", "Review Local Al", true)]
    [InlineData("Local AI", "Local Al", true)]
    [InlineData("Review Local AI", "Review Local AX", false)]
    [InlineData("Review Local AI", "Connect your Al", false)]
    [InlineData("Back", "Bad", false)]
    [InlineData("Review Local AI", "", false)]
    public void CapitalIAmbiguity_RequiresTheOtherwiseExactPhrase(string expected, string observed, bool admitted)
    {
        Assert.Equal(admitted, OnboardingNativeProof.IsCapitalIAmbiguity(expected, observed));
    }

    [Theory]
    [InlineData("Where should OpenClaw live?", "Your PC supports Local AI", false, false)]
    [InlineData("Where should OpenClaw live?", "Where should OpenCIaw live? Your PC supports Local Al", false, true)]
    [InlineData("", "Where should OpenClaw live? Your PC supports Local AI", false, false)]
    [InlineData("Where should OpenCIaw live?", "Your PC supports Local Al", true, true)]
    [InlineData("", "", true, true)]
    public void ExactLabelEvidence_RetainsMatchesFromEitherAnalysisOfTheSameCapture(
        string originalText, string scaledText, bool missingHeading, bool missingAi)
    {
        const string heading = "Where should OpenClaw live?";
        const string ai = "Your PC supports Local AI";
        var missing = OnboardingNativeProof.MissingOcrLabels([heading, ai], originalText, scaledText);
        Assert.Equal(missingHeading, missing.Contains(heading));
        Assert.Equal(missingAi, missing.Contains(ai));
    }
}
