using OpenClaw.SetupEngine.UI;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class LogFileLauncherTests
{
    [Fact]
    public void ResolveRealPath_UsesExistingLogAndDirectoryWithoutInventingPackagePath()
    {
        using var temp = new TempDirectory("setup-log-path-");
        string file = temp.Combine("setup.jsonl");
        File.WriteAllText(file, "{}\n");

        Assert.True(File.Exists(LogFileLauncher.ResolveRealPath(file)));
        Assert.True(Directory.Exists(LogFileLauncher.ResolveRealPath(temp.Path)));
    }

    [Fact]
    public void ResolveRealPath_UnavailableLogKeepsOriginalPath()
    {
        using var temp = new TempDirectory("setup-log-missing-");
        string file = temp.Combine("missing.jsonl");
        Assert.Equal(file, LogFileLauncher.ResolveRealPath(file));
    }
}
