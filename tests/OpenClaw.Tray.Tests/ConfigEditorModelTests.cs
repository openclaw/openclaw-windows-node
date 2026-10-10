using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ConfigEditorModelTests
{
    [Fact]
    public void CaptureSnapshot_UsesParsedRootAndBaseHash()
    {
        using var document = JsonDocument.Parse("""
        {
          "path": "/tmp/openclaw.json",
          "baseHash": "abc123",
          "parsed": {
            "gateway": {
              "reload": {
                "mode": "hybrid"
              }
            }
          }
        }
        """);

        var snapshot = ConfigEditorModel.CaptureSnapshot(document.RootElement);

        Assert.True(snapshot.HasRoot);
        Assert.Equal("abc123", snapshot.BaseHash);
        Assert.Equal("hybrid", snapshot.Root.GetProperty("gateway").GetProperty("reload").GetProperty("mode").GetString());
    }

    [Fact]
    public void ApplyChanges_UpdatesNestedValuesAndPreservesUnrelatedConfig()
    {
        using var document = JsonDocument.Parse("""
        {
          "gateway": {
            "reload": {
              "mode": "hybrid",
              "interval": 5
            }
          },
          "channels": {
            "slack": {
              "enabled": false
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["gateway.reload.mode"] = "manual",
                ["gateway.reload.interval"] = 10L,
                ["channels.slack.enabled"] = true,
            });

        Assert.Equal("manual", updated.GetProperty("gateway").GetProperty("reload").GetProperty("mode").GetString());
        Assert.Equal(10, updated.GetProperty("gateway").GetProperty("reload").GetProperty("interval").GetInt64());
        Assert.True(updated.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ApplyRelativeChanges_OverlaysOnlySelectedSectionDrafts()
    {
        using var document = JsonDocument.Parse("""
        {
          "mode": "hybrid",
          "interval": 5
        }
        """);

        var updated = ConfigEditorModel.ApplyRelativeChanges(
            document.RootElement,
            "gateway.reload",
            new Dictionary<string, object?>
            {
                ["gateway.reload.mode"] = "manual",
                ["gateway.other.value"] = "ignored"
            });

        Assert.Equal("manual", updated.GetProperty("mode").GetString());
        Assert.Equal(5, updated.GetProperty("interval").GetInt32());
        Assert.False(updated.TryGetProperty("other", out _));
    }

    [Fact]
    public void ApplyChanges_AcceptsJsonElementArrayValues()
    {
        using var document = JsonDocument.Parse("""
        {
          "routes": []
        }
        """);
        using var routes = JsonDocument.Parse("""
        [
          { "name": "primary", "enabled": true }
        ]
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["routes"] = routes.RootElement.Clone(),
            });

        var route = updated.GetProperty("routes")[0];
        Assert.Equal("primary", route.GetProperty("name").GetString());
        Assert.True(route.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ApplyChanges_IgnoresUnsupportedPlainObjectValues()
    {
        using var document = JsonDocument.Parse("""
        {
          "secret": "existing",
          "mode": "hybrid"
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["secret"] = new object(),
                ["mode"] = "manual",
            });

        Assert.Equal("existing", updated.GetProperty("secret").GetString());
        Assert.Equal("manual", updated.GetProperty("mode").GetString());
    }

    [Fact]
    public void ReadBindingRoutes_UsesTheParsedEnvelope()
    {
        using var document = JsonDocument.Parse("""
        {
          "parsed": {
            "bindings": [
              {
                "channel": "telegram",
                "accountId": "work",
                "agentId": "ops",
                "peer": "dm",
                "priority": 2
              }
            ]
          },
          "config": {
            "bindings": [
              { "channel": "wrong" }
            ]
          }
        }
        """);

        var route = Assert.Single(ConfigEditorModel.ReadBindingRoutes(document.RootElement));

        Assert.Equal("telegram", route.Channel);
        Assert.Equal("work", route.AccountId);
        Assert.Equal("ops", route.AgentId);
        Assert.Equal("dm", route.Peer);
        Assert.Equal(2, route.Priority);
    }

    [Fact]
    public void ReadBindingRoutes_UsesConfigWhenParsedIsAbsent()
    {
        using var document = JsonDocument.Parse("""
        {
          "config": {
            "bindings": [
              { "channel": "discord", "agentId": "main" }
            ]
          }
        }
        """);

        var route = Assert.Single(ConfigEditorModel.ReadBindingRoutes(document.RootElement));

        Assert.Equal("discord", route.Channel);
        Assert.Equal("*", route.AccountId);
        Assert.Equal("main", route.AgentId);
        Assert.Null(route.Peer);
        Assert.Null(route.Priority);
    }

    [Fact]
    public void ReadBindingRoutes_ReadsTheGatewayMatchObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "parsed": {
            "bindings": [
              {
                "agentId": "ops",
                "match": {
                  "channel": "telegram",
                  "accountId": "work",
                  "peer": { "kind": "direct", "id": "chat-1" }
                }
              }
            ]
          }
        }
        """);

        var route = Assert.Single(ConfigEditorModel.ReadBindingRoutes(document.RootElement));

        Assert.Equal("telegram", route.Channel);
        Assert.Equal("work", route.AccountId);
        Assert.Equal("ops", route.AgentId);
        Assert.Equal("direct:chat-1", route.Peer);
    }

    [Fact]
    public void ReadBindingRoutes_TopLevelEnvelopeWithoutBindings_IsEmpty()
    {
        using var document = JsonDocument.Parse("""
        {
          "path": "/tmp/openclaw.json",
          "parsed": { "gateway": { "mode": "local" } }
        }
        """);

        Assert.Empty(ConfigEditorModel.ReadBindingRoutes(document.RootElement));
    }
}
