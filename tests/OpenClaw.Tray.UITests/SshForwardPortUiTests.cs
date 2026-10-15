using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ConnectionStatus_RejectsInvalidForwardPortsWithoutChangingGateway()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "debug" });
            await WaitUiAsync(run, () => FindById(run, "DiagnosticsOpenEventTimeline") is not null,
                "connection timeline action");
            Invoke(FindById(run, "DiagnosticsOpenEventTimeline")!);
            await WaitUiAsync(run, () => FindById(run, "DiagSshToggle") is not null, "Connection Status");
            var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "DiagSshToggle")) is not null);
            var sshToggle = (TogglePattern)FindById(run, "DiagSshToggle")!.GetCurrentPattern(TogglePattern.Pattern);
            if (sshToggle.Current.ToggleState != ToggleState.On)
                sshToggle.Toggle();
            SetValue("DirectUrlBox", "ws://127.0.0.1:18790");
            SetValue("DiagSshUserBox", "fixture");
            SetValue("DiagSshHostBox", "127.0.0.1");
            var registryPath = Path.Combine(run.Profile.DataDirectory, "gateways.json");
            var registryBefore = await File.ReadAllBytesAsync(registryPath);
            var connect = window.FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Connect")))
                .Cast<AutomationElement>().Single(button => button.Current.AutomationId != "ConnectButton");

            foreach (var (remote, local, field, label) in new[]
            {
                ("", "18790", "remote", "blank-remote"),
                ("18789", "", "local", "blank-local"),
                ("70000", "18790", "remote", "large-remote"),
                ("18789", "70000", "local", "large-local"),
                ("abc", "18790", "remote", "text-remote"),
                ("18789", "abc", "local", "text-local"),
            })
            {
                SetValue("DiagSshRemotePortBox", remote);
                SetValue("DiagSshLocalPortBox", local);
                // Alternating fields requires a fresh error, not the preceding case's text.
                var previousResult = FindById(run, "DirectConnectResult")?.Current.Name;
                Invoke(connect);
                await WaitUiAsync(run, () =>
                    FindById(run, "DirectConnectResult") is { } current &&
                    current.Current.Name != previousResult &&
                    current.Current.Name.Contains($"{field} port", StringComparison.OrdinalIgnoreCase),
                    $"inline {field} port rejection");
                var result = FindById(run, "DirectConnectResult")!;
                Assert.False(result.Current.IsOffscreen);
                Assert.Equal(registryBefore, await File.ReadAllBytesAsync(registryPath));
                var status = await run.InvokeAsync("app.status");
                Assert.Equal("Connected", status.GetProperty("operatorState").GetString());
                output.WriteLine($"{label}: {result.Current.Name}; registry unchanged; original operator Connected.");
                if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                    OwnedWindowCapture.Save(run, window, $"ssh-{label}.png");
            }

            void SetValue(string id, string value) =>
                ((ValuePattern)FindById(run, id)!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
        });
    }
}
