using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class ExecApprovalPromptTextTests
{
    [Fact]
    public void GermanPrompt_LeadsWithPreviewAndKeepsExactCommandVisible()
    {
        var text = ExecApprovalPromptText.Build(new ExecApprovalPromptRequest
        {
            Command = "powershell.exe -NoProfile -Command Get-ComputerInfo",
            CommandPreview = "Zweck: Systemzustand lesen.\nRisiko: niedrig.\nEmpfehlung: Einmal erlauben.",
            Shell = "powershell",
            Reason = "No matching rule; default policy applied"
        }, german: true, displayName: "OpenClaw Companion");

        Assert.Contains("Worum es geht (von OpenClaw Companion beschrieben)", text);
        Assert.DoesNotContain("Otti", text);
        Assert.Contains("Zweck: Systemzustand lesen.", text);
        Assert.Contains("Technische Details", text);
        Assert.Contains("powershell.exe -NoProfile -Command Get-ComputerInfo", text);
        Assert.Contains("Policy und konfigurierte Sandbox-Regeln bleiben maßgeblich", text);
        Assert.True(
            text.IndexOf("powershell.exe -NoProfile -Command Get-ComputerInfo", StringComparison.Ordinal) <
            text.IndexOf("Zweck: Systemzustand lesen.", StringComparison.Ordinal),
            "The exact command must be visible before agent-supplied context.");
    }

    [Fact]
    public void Prompt_StripsBidirectionalOverridesFromUntrustedText()
    {
        var text = ExecApprovalPromptText.Build(new ExecApprovalPromptRequest
        {
            Command = "safe.exe\u202Etxt.exe",
            CommandPreview = "Read only\u2066spoof",
            Reason = "approval"
        }, german: false, displayName: "OpenClaw Companion");

        Assert.DoesNotContain('\u202E', text);
        Assert.DoesNotContain('\u2066', text);
        Assert.Contains(@"safe.exe\u{202E}txt.exe", text);
        Assert.Contains(@"Read only\u{2066}spoof", text);
    }

    [Fact]
    public void PromptWithoutPreview_TellsUserToDenyAndRetry()
    {
        var text = ExecApprovalPromptText.Build(new ExecApprovalPromptRequest
        {
            Command = "hostname",
            Reason = "approval"
        }, german: true, displayName: "OpenClaw Companion");

        Assert.Contains("Wenn du unsicher bist, lehne ab", text);
        Assert.Contains("hostname", text);
    }

    [Fact]
    public void GermanPrompt_UsesSuppliedDisplayName()
    {
        var text = ExecApprovalPromptText.Build(new ExecApprovalPromptRequest
        {
            Command = "hostname",
            CommandPreview = "Zweck: Rechnernamen lesen.",
            Reason = "approval"
        }, german: true, displayName: "OpenClaw Companion");

        Assert.Contains("OpenClaw Companion möchte", text);
        Assert.Contains("von OpenClaw Companion beschrieben", text);
        Assert.DoesNotContain("Otti", text);
    }
}
