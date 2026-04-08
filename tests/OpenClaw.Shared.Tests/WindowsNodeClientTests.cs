using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class WindowsNodeClientTests
{
    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("wss://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    public void Constructor_NormalizesGatewayUrl(string inputUrl, string expectedUrl)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient(inputUrl, "test-token", dataPath);
            var field = typeof(WindowsNodeClient).BaseType?.GetField(
                "_gatewayUrl",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var actualUrl = field?.GetValue(client) as string;

            Assert.Equal(expectedUrl, actualUrl);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// Regression test: when hello-ok includes auth.deviceToken, PairingStatusChanged must
    /// fire exactly once — not twice (once from the token block and again from the DeviceToken
    /// fallback check that follows it).
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkWithDeviceToken_FiresPairingChangedExactlyOnce()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // Put client into pending-approval state (simulates first-connect, no stored token)
            var isPendingField = typeof(WindowsNodeClient).GetField(
                "_isPendingApproval",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(isPendingField);
            isPendingField!.SetValue(client, true);

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            // Build a hello-ok payload that includes auth.deviceToken
            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id",
                        "auth": {
                            "deviceToken": "test-device-token-abc123"
                        }
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleResponseMethod);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Paired, pairingEvents[0].Status);
            Assert.Equal("Pairing approved!", pairingEvents[0].Message);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// When hello-ok has no token and no stored token, fires exactly one Pending event.
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkNoToken_FiresPendingExactlyOnce()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id"
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Pending, pairingEvents[0].Status);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// When hello-ok is received and a device token is already stored, fires exactly one
    /// Paired event (not Pending then Paired).
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkWithStoredToken_FiresPairedOnceNotPending()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // Pre-store a device token so the client is already paired
            var identityField = typeof(WindowsNodeClient).GetField(
                "_deviceIdentity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var identity = identityField!.GetValue(client)!;
            var storeMethod = identity.GetType().GetMethod("StoreDeviceToken");
            storeMethod!.Invoke(identity, ["stored-device-token-xyz"]);

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id"
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Paired, pairingEvents[0].Status);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    /// <summary>
    /// When the gateway returns ok: false, ConnectionStatus.Error is raised.
    /// </summary>
    [Fact]
    public void HandleResponse_FailedRegistration_RaisesConnectionError()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var statusChanges = new List<ConnectionStatus>();
            client.StatusChanged += (_, s) => statusChanges.Add(s);

            var json = """
                {
                    "type": "res",
                    "ok": false,
                    "error": {
                        "message": "Invalid token",
                        "code": "auth_failed"
                    },
                    "payload": {}
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Contains(ConnectionStatus.Error, statusChanges);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    /// <summary>
    /// HandleResponse with a payload that has no "type" key should not throw.
    /// </summary>
    [Fact]
    public void HandleResponse_MissingPayload_DoesNotThrow()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // A response with no "payload" property at all
            var json = """{"type":"res","ok":true}""";
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var ex = Record.Exception(() => handleResponseMethod!.Invoke(client, [root]));
            Assert.Null(ex);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void ShortDeviceId_LongId_TruncatesTo16Chars()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // DeviceId is a 64-char hex SHA-256 hash, always longer than 16.
            // ShortDeviceId is defined as the first 16 characters.
            var shortId = client.ShortDeviceId;
            Assert.True(shortId.Length <= 16,
                $"ShortDeviceId '{shortId}' should be at most 16 chars");
            if (client.FullDeviceId.Length > 16)
            {
                Assert.Equal(16, shortId.Length);
                Assert.True(client.FullDeviceId.StartsWith(shortId),
                    "ShortDeviceId should be a prefix of FullDeviceId");
            }
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void IsPaired_ReturnsFalse_WhenNoStoredToken()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            Assert.False(client.IsPaired);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void IsPaired_ReturnsTrue_AfterDeviceTokenStored()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var identityField = typeof(WindowsNodeClient).GetField(
                "_deviceIdentity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var identity = identityField!.GetValue(client)!;
            var storeMethod = identity.GetType().GetMethod("StoreDeviceToken");
            storeMethod!.Invoke(identity, ["my-device-token"]);

            Assert.True(client.IsPaired);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void RegisterCapability_AddsToCapabilitiesListAndRegistration()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            Assert.Empty(client.Capabilities);

            var cap = new SystemCapability(NullLogger.Instance);
            client.RegisterCapability(cap);

            Assert.Single(client.Capabilities);
            Assert.Same(cap, client.Capabilities[0]);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void RegisterCapability_DeduplicatesCommandsAndCategories()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var cap1 = new SystemCapability(NullLogger.Instance);
            var cap2 = new SystemCapability(NullLogger.Instance); // same category

            client.RegisterCapability(cap1);
            client.RegisterCapability(cap2);

            // Two capability instances
            Assert.Equal(2, client.Capabilities.Count);

            // Registration should deduplicate the "system" category
            var registrationField = typeof(WindowsNodeClient).GetField(
                "_registration",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var reg = (NodeRegistration)registrationField!.GetValue(client)!;
            Assert.Equal(1, reg.Capabilities.Count(c => c == "system"));
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void IsPendingApproval_FalseInitially()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            Assert.False(client.IsPendingApproval);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }

    [Fact]
    public void FullDeviceId_IsNonEmpty()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            Assert.NotEmpty(client.FullDeviceId);
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
        }
    }
}
