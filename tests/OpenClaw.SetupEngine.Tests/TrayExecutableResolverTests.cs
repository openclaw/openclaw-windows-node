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
}
