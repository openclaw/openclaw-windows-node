using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection("Gateway fixture UI")]
public sealed class StartupWindowUiTests(ITestOutputHelper output)
{
    [GatewayFixtureUiFact]
    public Task UnconfiguredLaunchAndReactivation_ShowOnlySetup() => VerifyStartupAsync(configured: false);

    [GatewayFixtureUiFact]
    public Task ConfiguredLaunchAndReactivation_ShowOnlyWorkspaceWithoutNodePairing() => VerifyStartupAsync(configured: true);

    [GatewayFixtureUiFact]
    public Task PostSetupRestart_DoesNotReopenSetup() => VerifyStartupAsync(configured: false, postSetupRestart: true);

    [GatewayFixtureUiFact]
    public Task UnreadableIdentity_KeepsWorkspaceRecoveryAvailable() => VerifyStartupAsync(configured: true, corruptIdentity: true);

    private async Task VerifyStartupAsync(bool configured, bool postSetupRestart = false, bool corruptIdentity = false)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var gateway = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var profile = new GatewayFixtureProfile(gateway.Endpoint, token);
        var settingsPath = Path.Combine(profile.DataDirectory, "settings.json");
        var settings = SettingsData.FromJson(File.ReadAllText(settingsPath))!;
        File.WriteAllText(settingsPath, (settings with
        {
            EnableMcpServer = false,
            EnableNodeMode = true
        }).ToJson());
        if (!configured)
        {
            var registry = new GatewayRegistry(profile.DataDirectory);
            registry.Load();
            registry.Remove(profile.GatewayId);
            registry.Save();
        }
        if (corruptIdentity)
        {
            var identityDirectory = Path.Combine(profile.DataDirectory, "gateways", profile.GatewayId);
            Directory.CreateDirectory(identityDirectory);
            File.WriteAllText(Path.Combine(identityDirectory, "device-key-ed25519.json"), "{");
        }

        var appPath = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe");
        // MCP is disabled; this distinct port satisfies the fixture's isolation contract.
        var start = profile.CreateStartInfo(appPath, gateway.Endpoint.Port == 19876 ? 19877 : 19876);
        if (postSetupRestart)
        {
            start.ArgumentList.Add("--post-setup-restart");
            start.ArgumentList.Add("--post-setup-launch");
            start.ArgumentList.Add("chat");
        }
        var expectWorkspace = configured || postSetupRestart;
        using var app = Process.Start(start) ?? throw new InvalidOperationException("App launch failed.");
        try
        {
            await WaitAsync(app, () => ReadLog(profile).Contains("Application started (WinUI 3)"),
                "startup completed");
            await WaitAsync(app, () => HasExpectedWindow(app.Id, expectWorkspace), "initial foreground window");
            AssertOnlyExpectedWindow(app.Id, expectWorkspace, "initial launch");

            foreach (var route in new string?[] { null, $"{OpenClawTray.AppIdentity.ProtocolScheme}://hub/chat" })
            {
                var before = ReadLog(profile).Split("Received deep link via IPC:").Length;
                var forward = profile.CreateStartInfo(appPath, gateway.Endpoint.Port == 19876 ? 19877 : 19876);
                if (route is not null)
                    forward.ArgumentList.Add(route);
                using var secondary = Process.Start(forward)
                    ?? throw new InvalidOperationException("Secondary launch failed.");
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await secondary.WaitForExitAsync(timeout.Token);
                    Assert.Equal(0, secondary.ExitCode);
                }
                finally
                {
                    if (!secondary.HasExited)
                    {
                        secondary.Kill(entireProcessTree: true);
                        await secondary.WaitForExitAsync();
                    }
                }
                await WaitAsync(app,
                    () => ReadLog(profile).Split("Received deep link via IPC:").Length > before,
                    "forwarded launch received");
                // Check the whole dispatch interval, not just the window that was already open.
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(200);
                    AssertOnlyExpectedWindow(app.Id, expectWorkspace, route ?? "repeat launch");
                }
            }
            if (corruptIdentity)
                Assert.Contains("Stored device identity load failed during setup detection", ReadLog(profile));
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync();
            }
        }
    }

    private static AutomationElement[] Windows(int processId) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId))
            .Cast<AutomationElement>().ToArray();

    private static bool IsWorkspace(AutomationElement window) =>
        window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNavigation")) is not null;

    private static bool IsSetup(AutomationElement window) =>
        window.Current.Name.Contains("OpenClaw Setup", StringComparison.Ordinal);

    private static bool HasExpectedWindow(int processId, bool configured) =>
        Windows(processId).Any(window => configured ? IsWorkspace(window) : IsSetup(window));

    private void AssertOnlyExpectedWindow(int processId, bool configured, string phase)
    {
        var windows = Windows(processId);
        Assert.Equal(configured ? 1 : 0, windows.Count(IsWorkspace));
        Assert.Equal(configured ? 0 : 1, windows.Count(IsSetup));
        Assert.DoesNotContain(windows, window =>
            window.Current.Name.Contains("OpenClaw Settings", StringComparison.Ordinal));
        if (!configured)
        {
            Assert.Contains(windows, window => IsSetup(window) &&
                window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Welcome to OpenClaw")) is not null);
        }
        output.WriteLine($"{phase}: setup={windows.Count(IsSetup)}, workspace={windows.Count(IsWorkspace)}");
    }

    private static string ReadLog(GatewayFixtureProfile profile)
    {
        var path = Path.Combine(profile.DataDirectory, "openclaw-tray.log");
        if (!File.Exists(path))
            return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitAsync(Process app, Func<bool> ready, string description)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            Assert.False(app.HasExited, $"App exited while waiting for {description}.");
            if (ready())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }
}
