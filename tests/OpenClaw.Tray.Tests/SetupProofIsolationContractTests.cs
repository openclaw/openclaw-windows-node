namespace OpenClaw.Tray.Tests;

public sealed class SetupProofIsolationContractTests
{
    [Fact]
    public void RealSetupProof_DoesNotUseProductionStartupCleanupIdentities()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "tests", "OpenClaw.E2ETests", "Setup", "E2ESetupFixture.cs"));
        Assert.Contains("BuildUninstallArguments(_configPath, _distroName, uninstallLogPath)", source);
        Assert.Contains("\"--autostart-name\", $\"{distroName}-Tray\"", source);
        Assert.Contains("\"--startup-task-name\", $\"{distroName}-Startup\"", source);
        Assert.Contains("\"OPENCLAW_TRAY_APPDATA_DIR\", RoamingAppDataRoot", source);
        Assert.Contains("psi.Environment[\"OPENCLAW_TRAY_APPDATA_DIR\"] = RoamingAppDataRoot", source);
        Assert.DoesNotContain("\"OPENCLAW_TRAY_APPDATA_DIR\", DataDir", source);
        Assert.DoesNotContain("psi.Environment[\"OPENCLAW_TRAY_APPDATA_DIR\"] = DataDir", source);
        Assert.Contains("Directory.Delete(RoamingAppDataRoot, recursive: true)", source);
    }
}
