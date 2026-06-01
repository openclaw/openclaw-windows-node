using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

[SupportedOSPlatform("windows")]
[Collection(EnvironmentVariableCollection.Name)]
public sealed class TrayExecutableResolverTests : IDisposable
{
    private readonly string _tempDir;

    public TrayExecutableResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tray-resolver-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_UsesInstalledParentLayout()
    {
        var setupEngineDir = Path.Combine(_tempDir, "OpenClawTray", "SetupEngine");
        Directory.CreateDirectory(setupEngineDir);

        var expectedTrayPath = Path.Combine(_tempDir, "OpenClawTray", "OpenClaw.Tray.WinUI.exe");
        File.WriteAllText(expectedTrayPath, string.Empty);

        var resolved = TrayExecutableResolver.Resolve(setupEngineDir);

        Assert.Equal(expectedTrayPath, resolved);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTrayExecutableIsMissing()
    {
        var setupEngineDir = Path.Combine(_tempDir, "OpenClawTray", "SetupEngine");
        Directory.CreateDirectory(setupEngineDir);

        var resolved = TrayExecutableResolver.Resolve(setupEngineDir);

        Assert.Null(resolved);
    }

    [Fact]
    public void StartupTaskRegistration_UsesLogonTrigger()
    {
        var trayPath = Path.Combine(_tempDir, "OpenClaw Tray", "OpenClaw.Tray.WinUI.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(trayPath)!);
        File.WriteAllText(trayPath, string.Empty);

        var psi = StartupTaskRegistration.CreateRegisterProcessStartInfo(trayPath);

        Assert.Equal(StartupTaskRegistration.ResolveSchtasksPath(), psi.FileName);
        Assert.Contains("/SC", psi.ArgumentList);
        Assert.Contains("ONLOGON", psi.ArgumentList);
        Assert.Contains("OpenClaw Companion", psi.ArgumentList);
        Assert.Contains("\"" + trayPath + "\"", psi.ArgumentList);
    }

    [Fact]
    public void StartupTaskRegistration_UnregisterDeletesTask()
    {
        var psi = StartupTaskRegistration.CreateUnregisterProcessStartInfo();

        Assert.Equal(StartupTaskRegistration.ResolveSchtasksPath(), psi.FileName);
        Assert.Contains("/Delete", psi.ArgumentList);
        Assert.Contains("/TN", psi.ArgumentList);
        Assert.Contains("OpenClaw Companion", psi.ArgumentList);
        Assert.Contains("/F", psi.ArgumentList);
    }

    [Fact]
    public void StartupTaskRegistration_QueryChecksTask()
    {
        var psi = StartupTaskRegistration.CreateQueryProcessStartInfo();

        Assert.Equal(StartupTaskRegistration.ResolveSchtasksPath(), psi.FileName);
        Assert.Contains("/Query", psi.ArgumentList);
        Assert.Contains("/TN", psi.ArgumentList);
        Assert.Contains("OpenClaw Companion", psi.ArgumentList);
    }
}
