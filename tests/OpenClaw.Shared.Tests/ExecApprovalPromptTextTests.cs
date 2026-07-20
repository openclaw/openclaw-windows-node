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

        Assert.Contains("Worum es geht (von Otti beschrieben)", text);
        Assert.Contains("Zweck: Systemzustand lesen.", text);
        Assert.Contains("Technische Details", text);
        Assert.Contains("powershell.exe -NoProfile -Command Get-ComputerInfo", text);
        Assert.Contains("Policy und Sandbox bleiben aktiv", text);
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

        Assert.Equal(-1, text.IndexOf('\u202E'));
        Assert.Equal(-1, text.IndexOf('\u2066'));
        Assert.Contains("safe.exetxt.exe", text);
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
}
