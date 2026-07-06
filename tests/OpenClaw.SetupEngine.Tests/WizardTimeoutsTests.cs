using System.Text.Json;

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
        Assert.Equal(WizardTimeouts.SlowStepTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }

    [Fact]
    public void AuthHint_DetectedInMessage()
    {
        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep("Setup", "Visit the device authorization page"));
    }

    [Theory]
    [InlineData("Setup", "Downloading plugin package", "")]
    [InlineData("Setup", "Installing integration", "")]
    [InlineData("Setup", "Working", "install-channel-plugin")]
    public void SlowSetupSteps_GetExtendedTimeout(string title, string message, string stepId)
    {
        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep(title, message, stepId));
    }

    [Theory]
    [InlineData("opaque-value", "Microsoft Teams", "")]
    [InlineData("teams", "Collaboration", "")]
    [InlineData("opaque-value", "Collaboration", "Download and configure the plugin")]
    public void SelectedSlowOptionMetadata_GetsExtendedTimeout(string value, string label, string hint)
    {
        var selected = new WizardOptionValue(
            value,
            label,
            hint,
            JsonSerializer.SerializeToElement(value));

        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep("Choose an integration", "Pick one.", selectedOptions: [selected]));
    }

    [Theory]
    [InlineData("Choose a connector")]
    [InlineData("Enter a friendly name")]
    [InlineData("")]
    public void OrdinarySteps_GetDefaultTimeout(string text)
    {
        Assert.Equal(WizardTimeouts.DefaultTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }

    [Fact]
    public void OrdinarySelectedOption_KeepsDefaultTimeout()
    {
        var selected = new WizardOptionValue(
            "matrix",
            "Matrix",
            "Configure an existing connection",
            JsonSerializer.SerializeToElement("matrix"));

        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForStep("Choose an integration", "Pick one.", selectedOptions: [selected]));
    }

    [Theory]
    [InlineData("__skip__", "Skip for now", "")]
    [InlineData("matrix", "Matrix", "Existing connection")]
    [InlineData("browser", "Open in browser", "")]
    public void ChannelSelector_NonSlowOption_KeepsDefaultTimeout(string value, string label, string hint)
    {
        var selected = new WizardOptionValue(
            value,
            label,
            hint,
            JsonSerializer.SerializeToElement(value));

        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForStep(
                "Choose a channel",
                "Select where OpenClaw should send messages.",
                "select-channel-quickstart",
                [selected]));
    }

    [Fact]
    public void ProgressStep_WithIncidentalOptions_UsesStepMetadata()
    {
        var incidentalOption = new WizardOptionValue(
            "details",
            "Show details",
            "",
            JsonSerializer.SerializeToElement("details"));

        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForGatewayStep(
                "Setup",
                "Working",
                "install-channel-plugin",
                "progress",
                [incidentalOption]));
    }

    [Fact]
    public void ProgressPollBudget_AllowsSingleLongSetupStepToUseTotalBudget()
    {
        Assert.Equal(WizardTimeouts.MaxTotalProgressPolls, WizardTimeouts.MaxProgressPollsPerStep);
        var totalBudget = TimeSpan.FromTicks(
            WizardTimeouts.ProgressPollDelay.Ticks * WizardTimeouts.MaxTotalProgressPolls);
        Assert.True(
            totalBudget >= TimeSpan.FromMinutes(20));
    }
}
