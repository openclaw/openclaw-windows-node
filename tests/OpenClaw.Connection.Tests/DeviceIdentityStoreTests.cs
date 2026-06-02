using System.Text.Json;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Tests for <see cref="DeviceIdentityStore.ClearStoredTokens"/>.
/// The method strips device token fields from an identity JSON file while
/// preserving all other properties (keypair, deviceId, algorithm, etc.).
/// </summary>
public class DeviceIdentityStoreTests : IDisposable
{
    private readonly string _tempDir;

    public DeviceIdentityStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-ids-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string WriteIdentityFile(object data)
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return _tempDir;
    }

    private JsonElement ReadIdentityFile()
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void ClearStoredTokens_RemovesDeviceToken()
    {
        WriteIdentityFile(new
        {
            DeviceId = "dev-123",
            PublicKey = "abc",
            DeviceToken = "tok-operator"
        });

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.Equal("dev-123", doc.GetProperty("DeviceId").GetString());
        Assert.Equal("abc", doc.GetProperty("PublicKey").GetString());
    }

    [Fact]
    public void ClearStoredTokens_RemovesNodeDeviceToken()
    {
        WriteIdentityFile(new
        {
            DeviceId = "dev-456",
            NodeDeviceToken = "tok-node",
            NodeDeviceTokenScopes = new[] { "node.connect" }
        });

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("NodeDeviceToken", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceTokenScopes", out _));
        Assert.Equal("dev-456", doc.GetProperty("DeviceId").GetString());
    }

    [Fact]
    public void ClearStoredTokens_RemovesAllFourTokenFields()
    {
        WriteIdentityFile(new
        {
            DeviceId = "dev-789",
            Algorithm = "Ed25519",
            PublicKey = "pubkey",
            PrivateKey = "privkey",
            DeviceToken = "operator-tok",
            DeviceTokenScopes = new[] { "operator.connect" },
            NodeDeviceToken = "node-tok",
            NodeDeviceTokenScopes = new[] { "node.connect" }
        });

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.False(doc.TryGetProperty("DeviceTokenScopes", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceToken", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceTokenScopes", out _));

        // Non-token fields are preserved.
        Assert.Equal("dev-789", doc.GetProperty("DeviceId").GetString());
        Assert.Equal("Ed25519", doc.GetProperty("Algorithm").GetString());
        Assert.Equal("pubkey", doc.GetProperty("PublicKey").GetString());
        Assert.Equal("privkey", doc.GetProperty("PrivateKey").GetString());
    }

    [Fact]
    public void ClearStoredTokens_WhenFileAbsent_DoesNotThrow()
    {
        // No identity file written — the method should be a no-op.
        var ex = Record.Exception(() => DeviceIdentityStore.ClearStoredTokens(_tempDir));
        Assert.Null(ex);
    }

    [Fact]
    public void ClearStoredTokens_WhenNoTokenFields_PreservesAllProperties()
    {
        WriteIdentityFile(new
        {
            DeviceId = "dev-clean",
            Algorithm = "Ed25519",
            PublicKey = "pubkey2"
        });

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.Equal("dev-clean", doc.GetProperty("DeviceId").GetString());
        Assert.Equal("Ed25519", doc.GetProperty("Algorithm").GetString());
        Assert.Equal("pubkey2", doc.GetProperty("PublicKey").GetString());
    }

    [Fact]
    public void ClearStoredTokens_IsIdempotent()
    {
        WriteIdentityFile(new
        {
            DeviceId = "dev-idem",
            DeviceToken = "tok",
            PublicKey = "pk"
        });

        DeviceIdentityStore.ClearStoredTokens(_tempDir);
        DeviceIdentityStore.ClearStoredTokens(_tempDir); // second call must not throw or corrupt

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.Equal("dev-idem", doc.GetProperty("DeviceId").GetString());
        Assert.Equal("pk", doc.GetProperty("PublicKey").GetString());
    }
}
