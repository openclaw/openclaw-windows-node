using System;
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

    [Fact]
    public void HandleResponse_HelloOkWithoutDeviceTokenAfterApproval_ClearsAwaitingReconnect()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            SetPrivateField(client, "_isPaired", true);
            SetPrivateField(client, "_pairingApprovedAwaitingReconnect", true);

            InvokeHandleResponse(client, """
                {
                  "type": "res",
                  "ok": true,
                  "payload": {
                    "type": "hello-ok",
                    "nodeId": "node-123"
                  }
                }
                """);

            Assert.False((bool)GetPrivateField(client, "_pairingApprovedAwaitingReconnect")!);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    [Fact]
    public void HandleResponse_HelloOkWithoutDeviceTokenWhenUnpaired_EmitsNeutralPairedMessage()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            PairingStatusEventArgs? pairingEvent = null;
            client.PairingStatusChanged += (_, args) => pairingEvent = args;

            InvokeHandleResponse(client, """
                {
                  "type": "res",
                  "ok": true,
                  "payload": {
                    "type": "hello-ok",
                    "nodeId": "node-123"
                  }
                }
                """);

            Assert.NotNull(pairingEvent);
            Assert.Equal(PairingStatus.Paired, pairingEvent!.Status);
            Assert.Equal("Node registration accepted", pairingEvent.Message);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    private static void InvokeHandleResponse(WindowsNodeClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(WindowsNodeClient).GetMethod(
            "HandleResponse",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method!.Invoke(client, new object[] { doc.RootElement.Clone() });
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(instance, value);
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return field!.GetValue(instance);
    }
}
