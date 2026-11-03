using System.Security.Cryptography;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class DevicePairDecisionTests
{
    [Theory]
    [InlineData(true, "")]
    [InlineData(false, " ")]
    [InlineData(true, "fixture-device-request")]
    [InlineData(false, "fixture-device-request")]
    public async Task DeviceDecision_WithoutReadyGatewayFailsClosed(bool approve, string requestId)
    {
        using var identity = new TempDirectory();
        var logger = new TestLogger();
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:1", "synthetic-token", logger,
            identityPath: identity.Path, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var before = Snapshot(identity.Path);

        var accepted = approve
            ? await client.DevicePairApproveAsync(requestId)
            : await client.DevicePairRejectAsync(requestId);

        Assert.False(accepted);
        Assert.Contains(logger.Logs, entry => entry.Contains(
            string.IsNullOrWhiteSpace(requestId) ? "requestId" : "ready Gateway connection",
            StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(identity.Path));
    }

    private static string[] Snapshot(string path) => Directory.EnumerateFiles(path)
        .Order(StringComparer.Ordinal)
        .Select(file => $"{Path.GetFileName(file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}")
        .ToArray();
}
