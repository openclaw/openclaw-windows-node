using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Setup must use the identity selected for the active URL.
/// A stamped identity for a previous URL stays on disk and is not sent.
/// </summary>
public sealed class SetupGatewaySessionIdentityTests
{
    private const string FixtureToken = "setup-reassigned-url-token";

    private readonly ITestOutputHelper _output;

    public SetupGatewaySessionIdentityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Connect_ReassignedUrl_UsesThatUrlsIdentityAndWithholdsThePreviousToken()
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateNativeSetup((_, _) => new { ok = true }),
            FixtureToken);
        var registry = new GatewayRegistry(temp.Path);
        var record = registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gateway-b",
            Url = server.Endpoint.ToString(),
            SharedGatewayToken = "shared-token-not-for-this-connect",
        });
        registry.SetActive(record.Id);
        registry.Save();

        var root = registry.GetIdentityDirectory(record.Id);
        var previous = new DeviceIdentity(root);
        previous.Initialize();
        previous.StoreDeviceTokenForRole("operator", "previous-url-device-token", ["operator.read"]);
        LegacyStartupDeviceToken.StampBoundUrl(root, "wss://gateway-a.example/socket");

        var selected = LegacyStartupDeviceToken.SelectIdentityDirectory(root, record.Url);
        var current = new DeviceIdentity(selected);
        current.Initialize();
        current.StoreDeviceTokenForRole("operator", FixtureToken, ["operator.read"]);

        await using var session = await SetupGatewaySession.ConnectAsync(temp.Path);

        Assert.Equal(current.DeviceId, session.Client.AuthenticatedSigningDeviceId);
        Assert.NotEqual(previous.DeviceId, session.Client.AuthenticatedSigningDeviceId);
        Assert.Equal(
            "previous-url-device-token",
            DeviceIdentity.TryReadStoredDeviceToken(root));
        Assert.Equal(FixtureToken, DeviceIdentity.TryReadStoredDeviceToken(selected));
        Assert.Contains(server.Requests, request => request.Method == "connect" && request.Outcome == "ok");

        _output.WriteLine(
            "setup connect auth=deviceToken device=" + current.DeviceId[..16]
            + " previous device withheld=" + previous.DeviceId[..16]
            + " connect outcome=ok");
    }
}
