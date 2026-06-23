namespace OpenClaw.SetupEngine.Tests;

public class WizardTimeoutsTests
{
    [Theory]
    [InlineData("Authorize device")]
    [InlineData("Please sign in to continue")]
    [InlineData("Complete the OAuth login")]
    [InlineData("Open your browser to authenticate")]
    [InlineData("Enter the verification code")]
    public void AuthSteps_GetExtendedTimeout(string text)
    {
        Assert.Equal(WizardTimeouts.AuthTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }

    [Fact]
    public void AuthHint_DetectedInMessage()
    {
        Assert.Equal(
            WizardTimeouts.AuthTimeoutMs,
            WizardTimeouts.ForStep("Setup", "Visit the device authorization page"));
    }

    [Theory]
    [InlineData("Choose a connector")]
    [InlineData("Enter a friendly name")]
    [InlineData("")]
    public void OrdinarySteps_GetDefaultTimeout(string text)
    {
        Assert.Equal(WizardTimeouts.DefaultTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }
}
