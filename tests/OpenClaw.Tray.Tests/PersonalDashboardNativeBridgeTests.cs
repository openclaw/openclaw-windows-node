using System.Text.Json;
using OpenClaw.Connection;
using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class PersonalDashboardNativeBridgeTests
{
    [Fact]
    public void NativeBridgeScriptSelectsPersonalAuthAdapterBeforeBridgeStartup()
    {
        const string gatewayUrl = "wss://node.example.ts.net:9443";
        var script = PersonalGatewayNativeBridgeScript.Create(gatewayUrl);
        const string assignment = "window.__OPENCLAW_NATIVE_CONTROL_AUTH__ = ";
        var assignmentStart = script.IndexOf(assignment, StringComparison.Ordinal);
        Assert.True(assignmentStart >= 0, "The script must select native personal authentication.");

        var jsonStart = assignmentStart + assignment.Length;
        var jsonEnd = script.IndexOf(';', jsonStart);
        using var config = JsonDocument.Parse(script[jsonStart..jsonEnd]);
        Assert.Equal(gatewayUrl, config.RootElement.GetProperty("gatewayUrl").GetString());
        Assert.True(config.RootElement.GetProperty("nativeConnectAuth").GetBoolean());
        Assert.True(script.IndexOf("window.OpenClawNativeGatewayAuth", StringComparison.Ordinal) > jsonEnd,
            "Native authentication selection must precede bridge startup.");
    }

    [Fact]
    public void ReusePolicyRequiresTheSameCurrentLeaseAndAnActiveWindow()
    {
        var lease = new PersonalOperatorLease(
            "gateway-1", "wss://node.example.ts.net", "person-42", "scope-9", "device-7", 3, 7);
        var nextLease = lease with { Generation = 4 };
        var identicalReplacement = lease with { };
        Assert.False(PersonalDashboardWindowReusePolicy.CanReuse(identicalReplacement, lease, lease, false, false));

        Assert.True(PersonalDashboardWindowReusePolicy.CanReuse(lease, lease, lease, false, false));
        Assert.False(PersonalDashboardWindowReusePolicy.CanReuse(lease, lease, lease, true, false));
        Assert.False(PersonalDashboardWindowReusePolicy.CanReuse(lease, lease, lease, false, true));
        Assert.False(PersonalDashboardWindowReusePolicy.CanReuse(nextLease, lease, nextLease, false, false));
        Assert.False(PersonalDashboardWindowReusePolicy.CanReuse(lease, lease, nextLease, false, false));
    }
}
