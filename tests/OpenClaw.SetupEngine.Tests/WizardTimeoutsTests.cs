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

    [Theory]
    [InlineData("Security", "Running agents on your computer is risky — harden your setup", "note-security")]
    [InlineData("Workspace backup", "Back up your agent workspace.", "note-backup")]
    [InlineData("Dashboard ready", "", "note-dashboard")]
    public void NoteSteps_GetExtendedTimeout(string title, string message, string stepId)
    {
        // Acking a note can trigger heavy post-ack backend work (e.g. Windows shell-
        // completion generation after the Security note) before the gateway returns the
        // next step, so note acks must use the slow ceiling rather than the 30s default.
        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForGatewayStep(title, message, stepId, "note", []));
    }

    [Fact]
    public void ConfirmStep_WithoutSlowHints_KeepsDefaultTimeout()
    {
        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForGatewayStep("Proceed?", "Continue setup", "confirm-continue", "confirm", []));
    }

    [Fact]
    public void TextStep_WithoutSlowHints_KeepsDefaultTimeout()
    {
        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForGatewayStep("Enter a friendly name", "", "text-name", "text", []));
    }
}
