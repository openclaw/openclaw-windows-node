namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiRouterStartupBudgetTests
{
    private static readonly TimeSpan s_measuredColdStart = TimeSpan.FromSeconds(26);

    [Fact]
    public void BundledRouterHealthBudget_AllowsForAColdStartAntivirusScan()
    {
        // The bundled config is what every real install deserializes, so a C# property
        // default alone does not raise the shipped budget.
        SetupConfig config = SetupConfig.LoadFromFile(Path.Combine(
            RepositoryRoot(), "src", "OpenClaw.SetupEngine", "default-config.json"));

        Assert.True(
            TimeSpan.FromSeconds(config.LocalAi.HealthTimeoutSeconds) >= s_measuredColdStart * 2,
            $"the router health budget ({config.LocalAi.HealthTimeoutSeconds}s) leaves no margin " +
            $"over the measured {s_measuredColdStart.TotalSeconds}s cold start");
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "src", "OpenClaw.SetupEngine", "default-config.json")))
                return directory;

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("repository root not found");
    }
}
