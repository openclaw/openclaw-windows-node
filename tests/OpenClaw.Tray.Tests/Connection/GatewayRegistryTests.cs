using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class GatewayRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;

    public GatewayRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
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
    public void Load_WithNoFile_DoesNotThrow()
    {
        var registry = new GatewayRegistry(Path.Combine(_tempDir, "nonexistent"));
        registry.Load();
        Assert.Empty(registry.GetAll());
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
    public void FindByUrl_ReturnsNullIfNotFound()
    {
        Assert.Null(_registry.FindByUrl("wss://unknown"));
    }

    [Fact]
    public void Changed_FiresOnAddOrUpdate()
    {
        GatewayRegistryChangedEventArgs? args = null;
        _registry.Changed += (s, e) => args = e;

        _registry.AddOrUpdate(MakeRecord("gw-1", "wss://test1"));

        Assert.NotNull(args);
        Assert.Single(args.Records);
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
            SshTunnel = new SshTunnelConfig("user", "host.example.com", 18789, 18789)
        };
        _registry.AddOrUpdate(record);
        _registry.Save();

        var registry2 = new GatewayRegistry(_tempDir);
        registry2.Load();

        var loaded = registry2.GetById("gw-1")!;
        Assert.NotNull(loaded.SshTunnel);
        Assert.Equal("user", loaded.SshTunnel.User);
        Assert.Equal("host.example.com", loaded.SshTunnel.Host);
        Assert.Equal(18789, loaded.SshTunnel.RemotePort);
    }

    private static GatewayRecord MakeRecord(string id, string url) => new()
    {
        Id = id,
        Url = url
    };
}
