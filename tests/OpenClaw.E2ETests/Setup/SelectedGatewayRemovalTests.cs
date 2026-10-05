using System.Diagnostics;
using OpenClaw.Connection;
using OpenClaw.E2ETests;

namespace OpenClaw.E2ETests.Setup;

[CollectionDefinition("Selected Gateway removal")]
public sealed class SelectedGatewayRemovalCollection : ICollectionFixture<E2ESetupFixture> { }

[Collection("Selected Gateway removal")]
public sealed class SelectedGatewayRemovalTests(E2ESetupFixture fixture)
{
    [E2EFact]
    public async Task SettingsCliRemovesRealOwnedWslDistroAndPreservesUnrelatedGatewayAndProfileData()
    {
        Assert.Null(fixture.SetupError);
        await fixture.StopTrayAsync();
        var registry = new GatewayRegistry(fixture.DataDir);
        registry.Load();
        var target = Assert.IsType<GatewayRecord>(registry.GetActive());
        Assert.Equal(fixture.DistroName, target.SetupManagedDistroName);
        var sentinel = new GatewayRecord
        {
            Id = "native-preservation-sentinel", IsLocal = true, Url = "ws://localhost:19377",
            NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd",
            NativeRuntimeContract = "isolated-session-v1",
        };
        registry.AddOrUpdate(sentinel);
        registry.Save();
        var sentinelDirectory = registry.GetIdentityDirectory(sentinel.Id);
        Directory.CreateDirectory(sentinelDirectory);
        var sentinelFile = Path.Combine(sentinelDirectory, "preserve.txt");
        File.WriteAllText(sentinelFile, "preserve-native-profile");
        var settings = File.ReadAllBytes(Path.Combine(fixture.DataDir, "settings.json"));
        var mcp = File.ReadAllBytes(Path.Combine(fixture.DataDir, "mcp-token.txt"));
        var app = Environment.GetEnvironmentVariable("OPENCLAW_E2E_TRAY_EXE")
            ?? throw new InvalidOperationException("Set OPENCLAW_E2E_TRAY_EXE to the exact current build.");
        var report = Path.Combine(fixture.ArtifactDir, "selected-removal.json");
        var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["OPENCLAW_TRAY_DATA_DIR"] = fixture.DataDir;
        start.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = fixture.RoamingAppDataRoot;
        start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = fixture.LocalAppDataRoot;
        foreach (var argument in new[] { "--uninstall", "--confirm-destructive", "--gateway-id", target.Id,
                     "--gateway-binding", GatewayDashboardBinding.Capture(target), "--json-output", report })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(report));
        registry.Load();
        Assert.Null(registry.GetById(target.Id));
        Assert.False(Directory.Exists(registry.GetIdentityDirectory(target.Id)));
        Assert.Equal(sentinel, registry.GetById(sentinel.Id));
        Assert.Equal("preserve-native-profile", File.ReadAllText(sentinelFile));
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(fixture.DataDir, "settings.json")));
        Assert.Equal(mcp, File.ReadAllBytes(Path.Combine(fixture.DataDir, "mcp-token.txt")));
        var probe = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.Unicode,
        };
        probe.ArgumentList.Add("--list");
        probe.ArgumentList.Add("--quiet");
        using var listing = Process.Start(probe)!;
        var names = await listing.StandardOutput.ReadToEndAsync();
        await listing.WaitForExitAsync();
        Assert.Equal(0, listing.ExitCode);
        Assert.DoesNotContain(fixture.DistroName, names, StringComparison.OrdinalIgnoreCase);
    }
}
