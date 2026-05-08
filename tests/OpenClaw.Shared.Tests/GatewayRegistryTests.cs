using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class GatewayRegistryTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-gwreg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    // ── ID Generation ──

    [Theory]
    [InlineData("ws://localhost:18789", "localhost-18789")]
    [InlineData("wss://gw.example.com", "gw-example-com-443")]
    [InlineData("ws://192.168.1.100:18789", "192-168-1-100-18789")]
    [InlineData("wss://my-gateway.tailnet.ts.net:443", "my-gateway-tailnet-ts-net-443")]
    [InlineData("ws://HOST:9999", "host-9999")]
    public void GenerateId_ProducesExpectedSlug(string url, string expected)
    {
        Assert.Equal(expected, GatewayRecord.GenerateId(url));
    }

    [Fact]
    public void GenerateId_EmptyUrl_ReturnsUnknown()
    {
        Assert.Equal("unknown", GatewayRecord.GenerateId(""));
        Assert.Equal("unknown", GatewayRecord.GenerateId(null!));
    }

    [Fact]
    public void GenerateId_InvalidUrl_ReturnsSanitizedFallback()
    {
        var id = GatewayRecord.GenerateId("not-a-url");
        Assert.False(string.IsNullOrEmpty(id));
        Assert.DoesNotContain("://", id);
    }

    // ── CRUD Operations ──

    [Fact]
    public void AddOrUpdate_AddsNewRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            var record = new GatewayRecord { Id = "gw1", Url = "ws://localhost:18789" };
            registry.AddOrUpdate(record);

            var all = registry.GetAll();
            Assert.Single(all);
            Assert.Equal("gw1", all[0].Id);
            Assert.Equal("ws://localhost:18789", all[0].Url);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AddOrUpdate_UpdatesExistingRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://old:1234" });
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://new:5678", OperatorDeviceToken = "tok" });

            var all = registry.GetAll();
            Assert.Single(all);
            Assert.Equal("ws://new:5678", all[0].Url);
            Assert.Equal("tok", all[0].OperatorDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AddOrUpdate_PreservesExistingTokensOnUpdate()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = "gw1", Url = "ws://host:1234",
                OperatorDeviceToken = "op-tok", NodeDeviceToken = "node-tok"
            });

            // Update with null tokens — should not overwrite existing
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://host:1234" });

            var gw = registry.GetActive();
            Assert.NotNull(gw);
            Assert.Equal("op-tok", gw.OperatorDeviceToken);
            Assert.Equal("node-tok", gw.NodeDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AddOrUpdate_SetActiveFalse_DoesNotChangeActive()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });
            registry.AddOrUpdate(new GatewayRecord { Id = "gw2", Url = "ws://b:2" }, setActive: false);

            var active = registry.GetActive();
            Assert.NotNull(active);
            Assert.Equal("gw1", active.Id);
            Assert.Equal(2, registry.GetAll().Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Remove_RemovesRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });
            registry.AddOrUpdate(new GatewayRecord { Id = "gw2", Url = "ws://b:2" });

            Assert.True(registry.Remove("gw1"));
            Assert.Single(registry.GetAll());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Remove_ClearsActiveIfRemoved()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });

            registry.Remove("gw1");
            Assert.Null(registry.GetActive());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            Assert.False(registry.Remove("nope"));
        }
        finally { Cleanup(dir); }
    }

    // ── Active Gateway ──

    [Fact]
    public void SetActive_ValidId_SetsActive()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });
            registry.AddOrUpdate(new GatewayRecord { Id = "gw2", Url = "ws://b:2" });

            Assert.True(registry.SetActive("gw1"));
            Assert.Equal("gw1", registry.GetActive()!.Id);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SetActive_InvalidId_ReturnsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });

            Assert.False(registry.SetActive("nope"));
            Assert.Equal("gw1", registry.GetActive()!.Id);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetActive_EmptyRegistry_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            Assert.Null(registry.GetActive());
        }
        finally { Cleanup(dir); }
    }

    // ── JSON Persistence ──

    [Fact]
    public void Persistence_SurvivesReload()
    {
        var dir = CreateTempDir();
        try
        {
            var registry1 = new GatewayRegistry(dir);
            registry1.AddOrUpdate(new GatewayRecord
            {
                Id = "gw1", Url = "ws://host:1234",
                OperatorDeviceToken = "op", NodeDeviceToken = "node"
            });

            // Load a new instance from the same directory
            var registry2 = new GatewayRegistry(dir);
            var gw = registry2.GetActive();
            Assert.NotNull(gw);
            Assert.Equal("gw1", gw.Id);
            Assert.Equal("ws://host:1234", gw.Url);
            Assert.Equal("op", gw.OperatorDeviceToken);
            Assert.Equal("node", gw.NodeDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Persistence_MultipleGateways_RoundTrip()
    {
        var dir = CreateTempDir();
        try
        {
            var registry1 = new GatewayRegistry(dir);
            registry1.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" }, setActive: false);
            registry1.AddOrUpdate(new GatewayRecord { Id = "gw2", Url = "ws://b:2" });

            var registry2 = new GatewayRegistry(dir);
            Assert.Equal(2, registry2.GetAll().Count);
            Assert.Equal("gw2", registry2.GetActive()!.Id);
        }
        finally { Cleanup(dir); }
    }

    // ── Edge Cases ──

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            Assert.Empty(registry.GetAll());
            Assert.Null(registry.GetActive());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_CorruptJson_StartsEmpty()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gateways.json"), "{{not json}}");
            var registry = new GatewayRegistry(dir);
            Assert.Empty(registry.GetAll());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_EmptyFile_StartsEmpty()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gateways.json"), "");
            var registry = new GatewayRegistry(dir);
            Assert.Empty(registry.GetAll());
        }
        finally { Cleanup(dir); }
    }

    // ── Migration ──

    [Fact]
    public void TryMigrateFromSettings_EmptyRegistry_CreatesSingleRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            var migrated = registry.TryMigrateFromSettings("ws://localhost:18789", "op-token", "node-token");

            Assert.True(migrated);
            var gw = registry.GetActive();
            Assert.NotNull(gw);
            Assert.Equal("localhost-18789", gw.Id);
            Assert.Equal("ws://localhost:18789", gw.Url);
            Assert.Equal("op-token", gw.OperatorDeviceToken);
            Assert.Equal("node-token", gw.NodeDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TryMigrateFromSettings_NonEmptyRegistry_DoesNothing()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "existing", Url = "ws://x:1" });

            var migrated = registry.TryMigrateFromSettings("ws://localhost:18789", "tok", null);
            Assert.False(migrated);
            Assert.Single(registry.GetAll());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TryMigrateFromSettings_EmptyUrl_ReturnsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            Assert.False(registry.TryMigrateFromSettings("", "tok", null));
            Assert.False(registry.TryMigrateFromSettings(null, "tok", null));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TryMigrateFromSettings_NullTokens_StoresNullTokens()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.TryMigrateFromSettings("ws://host:1234", null, null);

            var gw = registry.GetActive();
            Assert.NotNull(gw);
            Assert.Null(gw.OperatorDeviceToken);
            Assert.Null(gw.NodeDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    // ── Immutability of returned records ──

    [Fact]
    public void GetActive_ReturnedRecordDoesNotMutateInternal()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1", OperatorDeviceToken = "original" });

            var returned = registry.GetActive()!;
            returned.OperatorDeviceToken = "mutated";

            Assert.Equal("original", registry.GetActive()!.OperatorDeviceToken);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetAll_ReturnedListDoesNotMutateInternal()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new GatewayRegistry(dir);
            registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "ws://a:1" });

            var all = registry.GetAll();
            all[0].Url = "mutated";

            Assert.Equal("ws://a:1", registry.GetAll()[0].Url);
        }
        finally { Cleanup(dir); }
    }

    // ── GatewayRegistryData JSON ──

    [Fact]
    public void GatewayRegistryData_JsonRoundTrip()
    {
        var data = new GatewayRegistryData
        {
            ActiveGatewayId = "gw1",
            Gateways = new()
            {
                new GatewayRecord { Id = "gw1", Url = "ws://a:1", OperatorDeviceToken = "op" },
                new GatewayRecord { Id = "gw2", Url = "ws://b:2" },
            }
        };

        var json = data.ToJson();
        var parsed = GatewayRegistryData.FromJson(json);

        Assert.NotNull(parsed);
        Assert.Equal("gw1", parsed.ActiveGatewayId);
        Assert.Equal(2, parsed.Gateways.Count);
        Assert.Equal("op", parsed.Gateways[0].OperatorDeviceToken);
        Assert.Null(parsed.Gateways[1].OperatorDeviceToken);
    }

    [Fact]
    public void GatewayRegistryData_FromJson_InvalidJson_ReturnsNull()
    {
        Assert.Null(GatewayRegistryData.FromJson("not json"));
        Assert.Null(GatewayRegistryData.FromJson(""));
        Assert.Null(GatewayRegistryData.FromJson(null));
    }
}
