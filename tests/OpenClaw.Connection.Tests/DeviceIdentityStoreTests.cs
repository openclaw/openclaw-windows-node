using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;

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
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string WriteIdentityFile(object data)
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return _tempDir;
    }

    private DeviceIdentity CreateIdentity()
    {
        var identity = new DeviceIdentity(_tempDir);
        identity.Initialize();
        return identity;
    }

    private void AddCustomProperty(string name, string value)
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root[name] = value;
        DeviceIdentity.AtomicWriteKeyFileRaw(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RemoveTokenProperties()
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root.Remove("DeviceToken");
        root.Remove("DeviceTokenScopes");
        root.Remove("NodeDeviceToken");
        root.Remove("NodeDeviceTokenScopes");
        DeviceIdentity.AtomicWriteKeyFileRaw(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private JsonElement ReadIdentityFile()
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void ClearStoredTokens_RemovesDeviceToken()
    {
        var identity = CreateIdentity();
        identity.StoreDeviceTokenForRole("operator", "tok-operator");
        AddCustomProperty("CustomField", "abc");

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.Equal(identity.DeviceId, doc.GetProperty("DeviceId").GetString());
        Assert.Equal("abc", doc.GetProperty("CustomField").GetString());
    }

    [Fact]
    public void ClearStoredTokens_RemovesNodeDeviceToken()
    {
        var identity = CreateIdentity();
        identity.StoreDeviceTokenForRole("node", "tok-node", ["node.connect"]);

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("NodeDeviceToken", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceTokenScopes", out _));
        Assert.Equal(identity.DeviceId, doc.GetProperty("DeviceId").GetString());
    }

    [Fact]
    public void ClearStoredTokens_RemovesAllFourTokenFields()
    {
        var identity = CreateIdentity();
        identity.StoreDeviceTokenForRole("operator", "operator-tok", ["operator.connect"]);
        identity.StoreDeviceTokenForRole("node", "node-tok", ["node.connect"]);
        AddCustomProperty("CustomField", "preserved");

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.False(doc.TryGetProperty("DeviceTokenScopes", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceToken", out _));
        Assert.False(doc.TryGetProperty("NodeDeviceTokenScopes", out _));

        // Non-token fields are preserved.
        Assert.Equal(identity.DeviceId, doc.GetProperty("DeviceId").GetString());
        Assert.Equal("Ed25519", doc.GetProperty("Algorithm").GetString());
        Assert.Equal("preserved", doc.GetProperty("CustomField").GetString());
    }

    [Fact]
    public void ClearStoredTokens_WhenFileAbsent_DoesNotThrow()
    {
        // No identity file written — the method should be a no-op.
        var ex = Record.Exception(() => DeviceIdentityStore.ClearStoredTokens(_tempDir));
        Assert.Null(ex);
    }

    [Fact]
    public void ClearStoredTokens_WhenJsonRootIsNotObject_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        File.WriteAllText(path, "[]");

        var ex = Record.Exception(() => DeviceIdentityStore.ClearStoredTokens(_tempDir));

        Assert.Null(ex);
        Assert.Equal("[]", File.ReadAllText(path));
    }

    [Fact]
    public void ClearStoredTokens_WhenNoTokenFields_PreservesAllProperties()
    {
        var identity = CreateIdentity();
        AddCustomProperty("CustomField", "clean");
        RemoveTokenProperties();
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        var originalBytes = File.ReadAllBytes(path);

        DeviceIdentityStore.ClearStoredTokens(_tempDir);

        var doc = ReadIdentityFile();
        Assert.Equal(identity.DeviceId, doc.GetProperty("DeviceId").GetString());
        Assert.Equal("Ed25519", doc.GetProperty("Algorithm").GetString());
        Assert.Equal("clean", doc.GetProperty("CustomField").GetString());
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ClearStoredTokens_IsIdempotent()
    {
        var identity = CreateIdentity();
        identity.StoreDeviceTokenForRole("operator", "tok");

        DeviceIdentityStore.ClearStoredTokens(_tempDir);
        DeviceIdentityStore.ClearStoredTokens(_tempDir); // second call must not throw or corrupt

        var doc = ReadIdentityFile();
        Assert.False(doc.TryGetProperty("DeviceToken", out _));
        Assert.Equal(identity.DeviceId, doc.GetProperty("DeviceId").GetString());
    }

    [Fact]
    public void StoreToken_WhenExistingIdentityIsCorrupt_PropagatesTypedFailureWithoutMutation()
    {
        var path = Path.Combine(_tempDir, "device-key-ed25519.json");
        File.WriteAllText(path, "{");
        var originalBytes = File.ReadAllBytes(path);
        var store = new DeviceIdentityStore();

        Assert.Throws<DeviceIdentityLoadException>(
            () => store.StoreToken(_tempDir, "device-token", null, "operator"));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_tempDir, ".device-key-ed25519.json.*.tmp"));
    }
}
