using System.Text.RegularExpressions;
using OpenClaw.TestSupport;
using OpenClawTray;

namespace OpenClaw.Tray.Tests;

[CollectionDefinition("Windows registration isolation", DisableParallelization = true)]
public sealed class WindowsRegistrationIsolationCollection;

[Collection("Windows registration isolation")]
public sealed class IsolatedWindowsRegistrationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"C:\scenario\data", true)]
    [InlineData(" ", true)]
    public void IsolationMatchesTheTrayDataOverrideContract(string? dataDirectory, bool expected)
    {
        using var environment = new EnvironmentScope().Set("OPENCLAW_TRAY_DATA_DIR", dataDirectory);
        Assert.Equal(expected, AppIdentity.IsIsolated);
    }

    // These native entry points cannot be safely exercised against the developer's
    // HKCU. Retire these guards when a disposable-user registration suite covers them.
    [Theory]
    [InlineData("App.xaml.cs", @"if \(!GatewayFixtureIsolation\.IsEnabled && !AppIdentity\.IsIsolated\)\s+ToastNotificationManagerCompat.OnActivated \+=")]
    [InlineData("App.AppShutdownCoordinator.cs", @"if \(!GatewayFixtureIsolation\.IsEnabled && !AppIdentity\.IsIsolated\)\s+ToastNotificationManagerCompat.OnActivated -=")]
    [InlineData(@"Services\ToastService.cs", @"if \(AppIdentity.IsIsolated\)\s*\{\s*Logger.Info\([^;]+;\s*return;\s*\}")]
    [InlineData(@"Services\DeepLinkHandler.cs", @"if \(AppIdentity.IsIsolated\)\s*\{\s*Logger.Info\([^;]+;\s*return;\s*\}")]
    public void NativeRegistrationAndNotificationBoundaries_AreGuarded(string path, string guard)
    {
        Assert.Matches(new Regex(guard), ReadTraySource(path));
    }

    [Theory]
    [InlineData("void SetAutoStart")]
    [InlineData("Task SetAutoStartAsync")]
    public void StartupMutators_RejectBeforeAnyWindowsAccess(string signature)
    {
        var source = ReadTraySource(@"Services\AutoStartManager.cs");
        Assert.Contains($"public static {signature}(bool enable)\n    {{\n        ThrowIfFixtureMutation();\n        EnsureRegistrationAllowed();",
            source.Replace("\r\n", "\n"));
        Assert.Matches(@"if \(AppIdentity.IsIsolated\)\s+throw new AutoStartRefusedException", source);
        Assert.Matches(@"IsAutoStartEnabled\(\)\s*\{\s*if \(AppIdentity.IsIsolated\)\s+return false;", source);
    }

    [Fact]
    public void KeepaliveIsolationGate_PrecedesLegacyDiscoveryAndCleanup()
    {
        var source = ReadTraySource(@"Services\WslGatewayKeepAliveService.cs");
        Assert.Matches(@"if \(!WslKeepAlivePolicy.CanManageGateway\(activeRecord, AppIdentity.IsIsolated\)\)\s*\{[^}]+return;\s*\}", source);
        Assert.True(source.IndexOf("WslKeepAlivePolicy.CanManageGateway", StringComparison.Ordinal)
            < source.IndexOf("WslKeepAlivePolicy.ShouldStart", StringComparison.Ordinal));
    }

    private static string ReadTraySource(string path)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                directory = directory.Parent;
            root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate this test's source worktree.");
        }
        return File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", path));
    }
}
