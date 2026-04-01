using System;
using System.IO;
using System.Text;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Integration tests for DeviceIdentity — requires file system access.
/// Gated by OPENCLAW_RUN_INTEGRATION=1.
/// </summary>
public class DeviceIdentityIntegrationTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [IntegrationFact]
    public void Initialize_GeneratesNewKeypair()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            Assert.NotEmpty(identity.DeviceId);
            Assert.Equal(64, identity.DeviceId.Length); // SHA256 hex = 64 chars
            Assert.NotEmpty(identity.PublicKeyBase64Url);
            Assert.Null(identity.DeviceToken);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void Initialize_LoadsExistingKeypair()
    {
        var dir = CreateTempDir();
        try
        {
            var id1 = new DeviceIdentity(dir);
            id1.Initialize();
            var deviceId = id1.DeviceId;
            var pubKey = id1.PublicKeyBase64Url;

            // Reload from same dir
            var id2 = new DeviceIdentity(dir);
            id2.Initialize();

            Assert.Equal(deviceId, id2.DeviceId);
            Assert.Equal(pubKey, id2.PublicKeyBase64Url);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void SignPayload_ProducesDeterministicSignature()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var sig1 = identity.SignPayload("nonce1", 1000, "node-host", "tok");
            var sig2 = identity.SignPayload("nonce1", 1000, "node-host", "tok");

            Assert.Equal(sig1, sig2);
            Assert.NotEmpty(sig1);
            // Ed25519 signature is 64 bytes → base64url is 86 chars (no padding)
            Assert.Equal(86, sig1.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void SignPayload_DiffersForDifferentNonces()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var sig1 = identity.SignPayload("nonce-a", 1000, "node-host", "tok");
            var sig2 = identity.SignPayload("nonce-b", 1000, "node-host", "tok");

            Assert.NotEqual(sig1, sig2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void BuildDebugPayload_HasCorrectFormat()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var payload = identity.BuildDebugPayload("my-nonce", 1234567890, "node-host", "my-token");

            Assert.StartsWith("v2|", payload);
            Assert.Contains(identity.DeviceId, payload);
            Assert.Contains("|node-host|", payload);
            Assert.Contains("|node|node|", payload);
            Assert.Contains("|1234567890|", payload);
            Assert.Contains("|my-token|", payload);
            Assert.EndsWith("|my-nonce", payload);

            // Full format: v2|{deviceId}|{clientId}|node|node||{signedAtMs}|{authToken}|{nonce}
            var parts = payload.Split('|');
            Assert.Equal(9, parts.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void BuildConnectPayloadV3_HasCorrectFormat()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var payload = identity.BuildConnectPayloadV3(
                nonce: "challenge-nonce",
                signedAtMs: 1711648000000,
                clientId: "cli",
                clientMode: "cli",
                role: "operator",
                scopes: new[] { "operator.admin", "operator.read", "operator.write" },
                authToken: "mytoken123",
                platform: "windows",
                deviceFamily: "desktop");

            Assert.StartsWith("v3|", payload);
            Assert.Contains(identity.DeviceId, payload);
            Assert.Contains("|cli|cli|operator|operator.admin,operator.read,operator.write|", payload);
            Assert.Contains("|1711648000000|mytoken123|challenge-nonce|windows|desktop", payload);

            var parts = payload.Split('|');
            Assert.Equal(11, parts.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void BuildConnectPayloadV2_HasCorrectFormat()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var payload = identity.BuildConnectPayloadV2(
                nonce: "challenge-nonce",
                signedAtMs: 1711648000000,
                clientId: "cli",
                clientMode: "cli",
                role: "operator",
                scopes: new[] { "operator.admin", "operator.read", "operator.write" },
                authToken: "mytoken123");

            Assert.StartsWith("v2|", payload);
            Assert.Contains(identity.DeviceId, payload);
            Assert.Contains("|cli|cli|operator|operator.admin,operator.read,operator.write|", payload);
            Assert.Contains("|1711648000000|mytoken123|challenge-nonce", payload);

            var parts = payload.Split('|');
            Assert.Equal(9, parts.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void StoreDeviceToken_PersistsAcrossReload()
    {
        var dir = CreateTempDir();
        try
        {
            var id1 = new DeviceIdentity(dir);
            id1.Initialize();
            Assert.Null(id1.DeviceToken);

            id1.StoreDeviceToken("secret-device-token");
            Assert.Equal("secret-device-token", id1.DeviceToken);

            // Reload
            var id2 = new DeviceIdentity(dir);
            id2.Initialize();
            Assert.Equal("secret-device-token", id2.DeviceToken);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void DifferentDirs_ProduceDifferentIdentities()
    {
        var dir1 = CreateTempDir();
        var dir2 = CreateTempDir();
        try
        {
            var id1 = new DeviceIdentity(dir1);
            id1.Initialize();

            var id2 = new DeviceIdentity(dir2);
            id2.Initialize();

            Assert.NotEqual(id1.DeviceId, id2.DeviceId);
            Assert.NotEqual(id1.PublicKeyBase64Url, id2.PublicKeyBase64Url);
        }
        finally
        {
            Directory.Delete(dir1, true);
            Directory.Delete(dir2, true);
        }
    }

    [IntegrationFact]
    public void SignPayload_ThrowsBeforeInitialize()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            // Don't call Initialize()
            Assert.Throws<InvalidOperationException>(() =>
                identity.SignPayload("nonce", 1000, "client", "token"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public void PublicKeyBase64Url_IsValidBase64Url()
    {
        var dir = CreateTempDir();
        try
        {
            var identity = new DeviceIdentity(dir);
            identity.Initialize();

            var pubKey = identity.PublicKeyBase64Url;
            // Base64url: no +, /, or = padding
            Assert.DoesNotContain("+", pubKey);
            Assert.DoesNotContain("/", pubKey);
            Assert.DoesNotContain("=", pubKey);
            
            // Decode and verify Ed25519 public key is exactly 32 bytes
            var padded = pubKey.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var bytes = Convert.FromBase64String(padded);
            Assert.Equal(32, bytes.Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>
/// Unit tests for DeviceIdentity that don't touch the file system.
/// These verify model defaults and types.
/// </summary>
public class DeviceIdentityUnitTests
{
    [Fact]
    public void PairingStatusEventArgs_HasCorrectProperties()
    {
        var args = new PairingStatusEventArgs(PairingStatus.Paired, "abc123", "Approved");
        Assert.Equal(PairingStatus.Paired, args.Status);
        Assert.Equal("abc123", args.DeviceId);
        Assert.Equal("Approved", args.Message);
    }

    [Fact]
    public void PairingStatusEventArgs_MessageCanBeNull()
    {
        var args = new PairingStatusEventArgs(PairingStatus.Pending, "def456");
        Assert.Null(args.Message);
    }

    [Fact]
    public void PairingStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)PairingStatus.Unknown);
        Assert.Equal(1, (int)PairingStatus.Pending);
        Assert.Equal(2, (int)PairingStatus.Paired);
        Assert.Equal(3, (int)PairingStatus.Rejected);
    }
}
