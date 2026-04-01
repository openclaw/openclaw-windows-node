using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
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
}
