using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public class GatewayRegistryTests : IDisposable
{
    private readonly TempDirectory _temp;
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;

    public GatewayRegistryTests()
    {
        _temp = new TempDirectory();
        _tempDir = _temp.Path;
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        Assert.Empty(_registry.GetAll());
        Assert.Null(_registry.GetActive());
    }

    [Fact]
    public void AddOrUpdate_AddsNewRecord()
    {
        var record = MakeRecord("gw-1", "wss://test1");
        _registry.AddOrUpdate(record);

        Assert.Single(_registry.GetAll());
        Assert.Equal("gw-1", _registry.GetById("gw-1")!.Id);
    }

    [Fact]
    public void AddOrUpdate_UpdatesExistingRecord()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1-updated"));

        Assert.Single(_registry.GetAll());
        Assert.Equal("wss://test1-updated", _registry.GetById("gw-1")!.Url);
    }

    [Fact]
    public void Remove_DeletesRecord()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.Remove("gw-1");

        Assert.Empty(_registry.GetAll());
        Assert.Null(_registry.GetById("gw-1"));
    }

    [Fact]
    public void Remove_ClearsActiveIfRemoved()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.SetActive("gw-1");
        _registry.Remove("gw-1");

        Assert.Null(_registry.GetActive());
    }

    [Fact]
    public void SetActive_SetsActiveGateway()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.AddOrUpdate(MakeRecord("gw-2", "wss://test2"));
        _registry.SetActive("gw-2");

        Assert.Equal("gw-2", _registry.GetActive()!.Id);
    }

    [Fact]
    public void SetActive_FiresChangedWithActiveGatewayId()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.AddOrUpdate(MakeRecord("gw-2", "wss://test2"));
        GatewayRegistryChangedEventArgs? args = null;
        _registry.Changed += (_, e) => args = e;

        _registry.SetActive("gw-2");

        Assert.NotNull(args);
        Assert.Equal("gw-2", args!.ActiveGatewayId);
        Assert.Equal(2, args.Records.Count);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var r1 = MakeRecord("gw-1", "wss://test1") with { FriendlyName = "Home" };
        var r2 = MakeRecord("gw-2", "wss://test2") with { SharedGatewayToken = "tok-123" };
        _registry.AddOrUpdate(r1);
        _registry.AddOrUpdate(r2);
        _registry.SetActive("gw-1");
        _registry.Save();

        var registry2 = new GatewayRegistry(_tempDir);
        registry2.Load();

        Assert.Equal(2, registry2.GetAll().Count);
        Assert.Equal("Home", registry2.GetById("gw-1")!.FriendlyName);
        Assert.Equal("tok-123", registry2.GetById("gw-2")!.SharedGatewayToken);
        Assert.Equal("gw-1", registry2.GetActive()!.Id);
    }

    [Fact]
    public void Save_WhenMoveFails_RemovesTempFile()
    {
        var registryPath = Path.Combine(_tempDir, "gateways.json");
        File.WriteAllText(registryPath, "{}");
        using var lockFile = new FileStream(registryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));

        var ex = Assert.ThrowsAny<Exception>(() => _registry.Save());

        Assert.True(
            ex is IOException or UnauthorizedAccessException,
            $"Expected an IO/access failure, got {ex.GetType().FullName}: {ex.Message}");
        Assert.Empty(Directory.GetFiles(_tempDir, "gateways.json.*.tmp"));
    }

    [Fact]
    public void Save_WhenTempWriteFailsAfterCreatingFile_RemovesTempFile()
    {
        var fs = new FailingTempWriteFileSystem();
        var registry = new GatewayRegistry(_tempDir, fs);
        registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));

        var ex = Assert.Throws<IOException>(() => registry.Save());

        Assert.Equal("simulated partial write failure", ex.Message);
        Assert.Empty(Directory.GetFiles(_tempDir, "gateways.json.*.tmp"));
    }

    [Fact]
    public void Load_WithNoFile_DoesNotThrow()
    {
        var registry = new GatewayRegistry(Path.Combine(_tempDir, "nonexistent"));
        registry.Load();
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void Load_WithCorruptedJson_LogsWarningAndStartsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "gateways.json"), "{not json");
        var logger = new CapturingLogger();
        var registry = new GatewayRegistry(_tempDir, logger: logger);

        registry.Load();

        Assert.Empty(registry.GetAll());
        Assert.Contains(logger.Warnings, warning => warning.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void GetIdentityDirectory_ReturnsGatewayIdSubdir()
    {
        var path = _registry.GetIdentityDirectory("gw-1");
        Assert.EndsWith(Path.Combine("gateways", "gw-1"), path);
    }

    [Fact]
    public void FindByUrl_FindsMatchingRecord()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1.example.com"));
        _registry.AddOrUpdate(MakeRecord("gw-2", "wss://test2.example.com"));

        var found = _registry.FindByUrl("wss://test2.example.com");
        Assert.NotNull(found);
        Assert.Equal("gw-2", found.Id);
    }

    [Fact]
    public void FindByUrl_IsCaseInsensitive()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://Test.Example.COM"));
        var found = _registry.FindByUrl("wss://test.example.com");
        Assert.NotNull(found);
    }

    [Fact]
    public void FindByUrl_TreatsLoopbackLocalhostAnd127AsSameGateway()
    {
        _registry.AddOrUpdate(MakeRecord("gw-local", "ws://localhost:18789"));

        var found = _registry.FindByUrl("ws://127.0.0.1:18789");

        Assert.NotNull(found);
        Assert.Equal("gw-local", found.Id);
    }

    [Fact]
    public void FindByUrl_DoesNotMergeLoopbackUrlsWithDifferentQueryStrings()
    {
        _registry.AddOrUpdate(MakeRecord("gw-local", "ws://localhost:18789/ws?old"));

        var found = _registry.FindByUrl("ws://127.0.0.1:18789/ws?new");

        Assert.Null(found);
    }

    [Fact]
    public void FindByUrl_DoesNotMergeRemoteHostsWithSamePortAndPath()
    {
        _registry.AddOrUpdate(MakeRecord("gw-remote", "wss://gateway-one.example.com/ws?token=a"));

        var found = _registry.FindByUrl("wss://gateway-two.example.com/ws?token=a");

        Assert.Null(found);
    }

    [Fact]
    public void FindByUrl_NormalizesHttpAndHttpsSchemesForExactRemoteHostMatch()
    {
        _registry.AddOrUpdate(MakeRecord("gw-remote", "wss://gateway.example.com/ws?x=1"));

        var found = _registry.FindByUrl("https://gateway.example.com/ws?x=1");

        Assert.NotNull(found);
        Assert.Equal("gw-remote", found.Id);
    }

    [Fact]
    public void FindByUrl_ReturnsNullIfNotFound()
    {
        Assert.Null(_registry.FindByUrl("wss://unknown"));
    }

    [Fact]
    public void Changed_FiresOnAddOrUpdate()
    {
        _registry.AddOrUpdate(MakeRecord("active", "wss://active"));
        _registry.SetActive("active");
        GatewayRegistryChangedEventArgs? args = null;
        _registry.Changed += (s, e) => args = e;

        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));

        Assert.NotNull(args);
        Assert.Equal("active", args!.ActiveGatewayId);
        Assert.Equal(2, args.Records.Count);
    }

    [Fact]
    public void Changed_FiresOnRemove()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        GatewayRegistryChangedEventArgs? args = null;
        _registry.Changed += (s, e) => args = e;

        _registry.Remove("gw-1");

        Assert.NotNull(args);
        Assert.Empty(args.Records);
    }

    [Fact]
    public void SaveAndLoad_WithSshTunnelConfig()
    {
        var record = MakeRecord("gw-1", "wss://test1") with
        {
            SshTunnel = new SshTunnelConfig(
                "user",
                "host.example.com",
                RemotePort: 18789,
                LocalPort: 45678,
                IncludeBrowserProxyForward: true,
                SshPort: 2222)
        };
        _registry.AddOrUpdate(record);
        _registry.Save();

        var registry2 = new GatewayRegistry(_tempDir);
        registry2.Load();

        var loaded = registry2.GetById("gw-1")!;
        Assert.NotNull(loaded.SshTunnel);
        Assert.Equal("user", loaded.SshTunnel.User);
        Assert.Equal("host.example.com", loaded.SshTunnel.Host);
        Assert.Equal(2222, loaded.SshTunnel.SshPort);
        Assert.Equal(18789, loaded.SshTunnel.RemotePort);
        Assert.Equal(45678, loaded.SshTunnel.LocalPort);
        Assert.True(loaded.SshTunnel.IncludeBrowserProxyForward);
    }

    [Fact]
    public void Load_WithLegacySshTunnelConfig_DefaultsSshPort()
    {
        File.WriteAllText(Path.Combine(_tempDir, "gateways.json"), """
        {
          "activeId": "gw-1",
          "gateways": [
            {
              "id": "gw-1",
              "url": "wss://test1",
              "sshTunnel": {
                "user": "user",
                "host": "host.example.com",
                "remotePort": 18789,
                "localPort": 28789,
                "includeBrowserProxyForward": false
              }
            }
          ]
        }
        """);

        _registry.Load();

        var loaded = _registry.GetById("gw-1")!;
        Assert.NotNull(loaded.SshTunnel);
        Assert.Equal(22, loaded.SshTunnel.SshPort);
    }

    [Fact]
    public void SaveAndLoad_WithLastConnected_RoundTrips()
    {
        var stamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var record = MakeRecord("gw-1", "wss://test1") with { LastConnected = stamp };
        _registry.AddOrUpdate(record);
        _registry.Save();

        var registry2 = new GatewayRegistry(_tempDir);
        registry2.Load();

        var loaded = registry2.GetById("gw-1")!;
        Assert.Equal(stamp, loaded.LastConnected);
    }

    [Fact]
    public void Update_ModifiesRecordAtomically()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1") with { SharedGatewayToken = "tok" });

        var updated = _registry.Update("gw-1", r => r with { LastConnected = DateTime.UtcNow });

        Assert.NotNull(updated);
        Assert.True(updated.LastConnected.HasValue);
        Assert.Equal("tok", updated.SharedGatewayToken); // other fields preserved
    }

    [Fact]
    public void Update_ReturnsNullForMissingRecord()
    {
        var result = _registry.Update("nonexistent", r => r with { LastConnected = DateTime.UtcNow });
        Assert.Null(result);
    }

    [Fact]
    public void Update_FiresChangedEvent()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        GatewayRegistryChangedEventArgs? args = null;
        _registry.Changed += (s, e) => args = e;

        _registry.Update("gw-1", r => r with { FriendlyName = "Updated" });

        Assert.NotNull(args);
        Assert.Equal("Updated", args.Records[0].FriendlyName);
    }

    [Fact]
    public void BrowserControlPort_IsScopedToTheActiveGateway()
    {
        _registry.AddOrUpdate(MakeRecord("gw-a", "wss://a") with { BrowserControlPort = 19001 });
        _registry.AddOrUpdate(MakeRecord("gw-b", "wss://b") with { BrowserControlPort = 19002 });

        _registry.SetActive("gw-a");
        Assert.Equal(19001, _registry.GetActive()!.BrowserControlPort);

        // Switching the active gateway re-scopes the override — no sticky global, no misroute.
        _registry.SetActive("gw-b");
        Assert.Equal(19002, _registry.GetActive()!.BrowserControlPort);
    }

    [Fact]
    public void BrowserControlPort_DefaultsNull_AndPersistsAcrossReload()
    {
        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));
        _registry.SetActive("gw-1");
        Assert.Null(_registry.GetActive()!.BrowserControlPort);

        _registry.AddOrUpdate(_registry.GetActive()! with { BrowserControlPort = 19005 });
        _registry.Save();

        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Equal(19005, reloaded.GetActive()!.BrowserControlPort);
    }

    [Fact]
    public void PreserveAdvancedFields_KeepsBrowserControlPort_AcrossSavedGatewayEdit()
    {
        // Simulates the edit/connect flow: a saved gateway has a per-gateway override; the user
        // edits name / token / URL / SSH, which rebuilds a fresh record WITHOUT the advanced field.
        var existing = MakeRecord("gw-1", "wss://old") with { BrowserControlPort = 19000, FriendlyName = "Home" };

        var rebuilt = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://new",
            FriendlyName = "Home renamed",
            SharedGatewayToken = "rotated",
            // BrowserControlPort intentionally absent — the form doesn't expose it.
        }.PreserveAdvancedFields(existing);

        Assert.Equal(19000, rebuilt.BrowserControlPort); // carried forward, not silently dropped
        Assert.Equal("wss://new", rebuilt.Url);          // edited fields still applied
        Assert.Equal("rotated", rebuilt.SharedGatewayToken);
    }

    [Fact]
    public void PreserveAdvancedFields_FormValueWins_AndNullExistingIsNoOp()
    {
        var existing = MakeRecord("gw-1", "wss://old") with { BrowserControlPort = 19000 };

        // An explicit new value on the rebuilt record wins over the existing one.
        var changed = (new GatewayRecord { Id = "gw-1", Url = "wss://x", BrowserControlPort = 20500 })
            .PreserveAdvancedFields(existing);
        Assert.Equal(20500, changed.BrowserControlPort);

        // A brand-new record (no existing) is returned unchanged.
        var fresh = new GatewayRecord { Id = "gw-2", Url = "wss://y" };
        var preserved = fresh.PreserveAdvancedFields(null);
        Assert.Null(preserved.BrowserControlPort);
        Assert.Same(fresh, preserved);
    }

    private static GatewayRecord MakeRecord(string id, string url) => new()
    {
        Id = id,
        Url = url
    };

    private sealed class CapturingLogger : IOpenClawLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class FailingTempWriteFileSystem : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string content)
        {
            File.WriteAllText(path, content[..Math.Min(content.Length, 16)]);
            throw new IOException("simulated partial write failure");
        }
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CopyFile(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
    }
}
