using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

// The SetupAndConnectTests prefix includes these fixture-free guards in the existing CI filter.
public sealed class SetupAndConnectTestsUninstallIsolation
{
    [Fact]
    public void BuildUninstallArguments_KeepsFullCleanupWithFixtureOwnedStartupNames()
    {
        const string distroName = "OpenClawE2E-01234567";
        var configPath = Path.Combine(Path.GetTempPath(), "fixture data", "e2e-config.json");
        var logPath = Path.Combine(Path.GetTempPath(), "fixture artifacts", "uninstall-engine.jsonl");

        var arguments = E2ESetupFixture.BuildUninstallArguments(configPath, distroName, logPath);

        Assert.Equal(
        [
            "--config", configPath,
            "--uninstall",
            "--confirm-destructive",
            "--autostart-name", "OpenClawE2E-01234567-Tray",
            "--startup-task-name", "OpenClawE2E-01234567-Startup",
            "--log-path", logPath
        ], arguments);
        Assert.DoesNotContain("OpenClawTray", arguments);
        Assert.DoesNotContain(WindowsStartupTaskRegistration.TaskName, arguments);
    }

    [Theory]
    [InlineData("--autostart-name")]
    [InlineData("--startup-task-name")]
    public void BuildUninstallArguments_StartupNamesAreStableAndDistinctAcrossFixtures(string option)
    {
        var first = E2ESetupFixture.BuildUninstallArguments("first.json", "OpenClawE2E-01234567", "first.jsonl");
        var repeated = E2ESetupFixture.BuildUninstallArguments("first.json", "OpenClawE2E-01234567", "retry.jsonl");
        var second = E2ESetupFixture.BuildUninstallArguments("second.json", "OpenClawE2E-89abcdef", "second.jsonl");

        var index = Array.IndexOf(first, option);
        Assert.True(index >= 0);
        Assert.Equal(index, Array.IndexOf(repeated, option));
        Assert.Equal(index, Array.IndexOf(second, option));
        Assert.Equal(first[index + 1], repeated[index + 1]);
        Assert.NotEqual(first[index + 1], second[index + 1]);
        Assert.StartsWith("OpenClawE2E-01234567-", first[index + 1], StringComparison.Ordinal);
        Assert.StartsWith("OpenClawE2E-89abcdef-", second[index + 1], StringComparison.Ordinal);
    }
}
