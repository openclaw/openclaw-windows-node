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
}
