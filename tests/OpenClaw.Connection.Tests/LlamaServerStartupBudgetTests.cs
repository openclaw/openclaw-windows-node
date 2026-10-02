using OpenClaw.Connection.LocalAi;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerStartupBudgetTests
{
    /// <summary>
    /// The tray starts the router without going through SetupConfig, so raising the
    /// setup-side health budget does not help it. Its own startup budget has to clear
    /// the same cold-start Defender scan (measured 26.0 s).
    /// </summary>
    [Fact]
    public void RouterStartupBudget_AllowsForAColdStartAntivirusScan()
    {
        var options = new LlamaServerRuntimeOptions { Paths = new LocalAiPaths(Path.GetTempPath()) };

        Assert.True(
            options.StartupTimeout >= TimeSpan.FromSeconds(52),
            $"the tray router startup budget ({options.StartupTimeout.TotalSeconds}s) leaves no " +
            "margin over the measured 26.0s cold start");
    }
}
