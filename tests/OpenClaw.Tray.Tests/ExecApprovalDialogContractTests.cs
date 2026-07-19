namespace OpenClaw.Tray.Tests;

public sealed class ExecApprovalDialogContractTests
{
    [Fact]
    public void ExactCommand_IsAddedBeforeAgentSuppliedContext()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Dialogs",
            "ExecApprovalDialog.cs"));

        var commandIndex = source.IndexOf("Text = view.CommandText", StringComparison.Ordinal);
        var previewIndex = source.IndexOf(
            "if (!string.IsNullOrWhiteSpace(view.CommandPreviewText))",
            StringComparison.Ordinal);

        Assert.True(commandIndex >= 0, "Exact command rendering must remain present.");
        Assert.True(previewIndex >= 0, "Agent-supplied context rendering must remain present.");
        Assert.True(
            commandIndex < previewIndex,
            "Exact command must be rendered before agent-supplied context.");
    }
}
