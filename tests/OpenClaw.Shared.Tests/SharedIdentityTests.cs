using System;
using System.IO;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SharedIdentityTests
{
    [Fact]
    public void TwoClients_SamePath_ShareDeviceId()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-shared-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            // Simulate operator and node sharing the same identity path
            var identity1 = new DeviceIdentity(dataPath);
            identity1.Initialize();

            var identity2 = new DeviceIdentity(dataPath);
            identity2.Initialize();

            Assert.Equal(identity1.DeviceId, identity2.DeviceId);
            Assert.Equal(identity1.PublicKeyBase64Url, identity2.PublicKeyBase64Url);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void StoredDeviceToken_IsVisibleToNewInstance()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-shared-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            // First instance (node) stores a device token after pairing
            var nodeIdentity = new DeviceIdentity(dataPath);
            nodeIdentity.Initialize();
            nodeIdentity.StoreDeviceToken("paired-device-token-123");

            // Second instance (operator) loads the same identity and should see the token
            var operatorIdentity = new DeviceIdentity(dataPath);
            operatorIdentity.Initialize();

            Assert.Equal("paired-device-token-123", operatorIdentity.DeviceToken);
            Assert.Equal(nodeIdentity.DeviceId, operatorIdentity.DeviceId);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void GatewayClient_PicksUpStoredDeviceToken()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-shared-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var previousAppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR");

        try
        {
            // Point the gateway client at our test data path
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", dataPath);

            // Simulate node pairing: create identity and store device token
            Directory.CreateDirectory(Path.Combine(dataPath, "OpenClawTray"));
            var nodeIdentity = new DeviceIdentity(Path.Combine(dataPath, "OpenClawTray"));
            nodeIdentity.Initialize();
            nodeIdentity.StoreDeviceToken("node-paired-token");

            // Operator client created AFTER node paired — should pick up the token
            using (var operatorClient = new OpenClawGatewayClient(
                "ws://localhost:18789",
                "manual-token"))
            {
                // The operator's effective auth should prefer the stored device token
                Assert.Equal("node-paired-token", operatorClient.ConnectAuthToken);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", previousAppData);
            try
            {
                if (Directory.Exists(dataPath))
                    Directory.Delete(dataPath, true);
            }
            catch (IOException) { /* file lock from NSec Key — non-critical in test cleanup */ }
        }
    }
}
