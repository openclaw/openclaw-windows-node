namespace OpenClaw.SetupEngine.Tests;

public sealed class ProgressPageContractTests
{
    [Fact]
    public void LocalAiReconciliation_HasAVisibleRunningGroup()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "ProgressPage.xaml.cs"));

        Assert.Contains(
            "[\"reconcile-local-ai-installation\", \"acquire-local-ai-runtime\"]",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the OpenClaw repository root.");
    }
}
