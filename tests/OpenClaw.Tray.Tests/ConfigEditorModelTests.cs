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
    public void FindUneditedRedactionSentinel_BlocksUntouchedRedactedSibling()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "signingSecret": "<redacted>", "enabled": true },
            "telegram": { "botToken": "old", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "new-token",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Equal("channels.slack.signingSecret", blocked);
        Assert.Equal("<redacted>", updated.GetProperty("channels").GetProperty("slack").GetProperty("signingSecret").GetString());
        Assert.Equal("new-token", updated.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsUserReplacedToken()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "telegram": { "botToken": "<redacted>", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "real-new-token",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Null(blocked);
        Assert.Equal("real-new-token", updated.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_IgnoresRedactedSubstring()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "label": "no redacted secrets here", "enabled": true }
          }
        }
        """);

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            Array.Empty<string>());

        Assert.Null(blocked);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksEditedValueThatIsStillTheSentinel()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "telegram": { "botToken": "<redacted>", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "<redacted>",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Equal("channels.telegram.botToken", blocked);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksArraySentinelWhenEditUsedTheUnindexedPath()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "accounts": [ { "token": "<redacted>" } ] }
          }
        }
        """);

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            new[] { "channels.slack.accounts.token" });

        Assert.Equal("channels.slack.accounts[0].token", blocked);
    }
}
