using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClaw.Connection;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

public sealed class GatewayRemovalConsentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("signature")]
    [InlineData("browser-port")]
    [InlineData("label")]
    [InlineData("endpoint-only")]
    public async Task ActualTrayRejectsStaleParentConsentBeforeSetupEngineDispatch(string change)
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Combine("data"));
        var confirmed = new GatewayRecord
        {
            Id = "consent-fixture", Url = "ws://127.0.0.1:18789",
            IsLocal = true, SetupManagedDistroName = "ConsentFixtureNoSuchDistro",
        };
        var binding = change == "endpoint-only"
            ? GatewayDashboardBinding.Capture(confirmed)
            : LocalGatewaySettings.CaptureRemovalAuthority(confirmed);
        var changed = change switch
        {
            "signature" => confirmed with { RequiresV2Signature = true },
            "browser-port" => confirmed with { BrowserControlPort = 19999 },
            "label" => confirmed with { FriendlyName = "Changed" },
            "endpoint-only" => confirmed,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        registry.AddOrUpdate(changed);
        registry.SetActive(changed.Id);
        registry.Save();
        var saved = File.ReadAllBytes(temp.Combine("data", "gateways.json"));
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var root = Assert.IsType<string>(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT"));
        var executable = Path.Combine(root, "src", "OpenClaw.Tray.WinUI",
            "bin", "Debug", "net10.0-windows10.0.22621.0", rid, "OpenClaw.Tray.WinUI.exe");
        Assert.True(File.Exists(executable), $"Build the actual tray executable before this test: {executable}");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["OPENCLAW_TRAY_DATA_DIR"] = temp.Combine("data");
        start.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = temp.Combine("roaming");
        start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = temp.Combine("local");
        start.Environment.Remove("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        start.Environment.Remove("OPENCLAW_GATEWAY_FIXTURE");
        foreach (var argument in new[] { "--uninstall", "--dry-run",
                     "--gateway-id", confirmed.Id, "--gateway-binding", binding })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        output.WriteLine($"Owned CLI pid={process.Id}; startedUtc={process.StartTime.ToUniversalTime():O}; executable={executable}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, process.ExitCode);
            Assert.Contains("The selected WSL Gateway changed. No uninstall was started.", await stderr);
            Assert.DoesNotContain("running SetupEngine", await stdout);
            Assert.Equal(saved, File.ReadAllBytes(temp.Combine("data", "gateways.json")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
