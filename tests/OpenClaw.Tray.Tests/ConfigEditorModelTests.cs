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
    public void FindUneditedRedactionSentinel_OmitsUntouchedRedactedSibling()
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
            new[] { "channels.telegram.botToken" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Null(blocked);
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("signingSecret", out _));
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        Assert.Equal("new-token", sent.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
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
            new[] { "channels.telegram.botToken" },
            document.RootElement);

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
            Array.Empty<string>(),
            document.RootElement);

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
            new[] { "channels.telegram.botToken" },
            document.RootElement);

        Assert.Equal("channels.telegram.botToken", blocked);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsNoIdArrayWhenEditUsedTheUnindexedPath()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "accounts": [ { "token": "<redacted>" } ] }
          }
        }
        """);

        var edited = new[] { "channels.slack.accounts.token" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            document.RootElement,
            edited);

        Assert.Null(blocked);
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("accounts", out _));
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsLiteralMaskInAnUntouchedArray()
    {
        using var document = JsonDocument.Parse("""
        {
          "labels": ["***", "public"],
          "channels": { "telegram": { "botToken": "old" } }
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
            new[] { "channels.telegram.botToken" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("labels")[0].GetString());
        Assert.Equal("new-token", sent.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_KeepsLiteralMaskStagedThroughParentObject()
    {
        using var stored = JsonDocument.Parse("""
        {
          "metadata": { "label": "before", "token": "<redacted>" }
        }
        """);
        using var updated = JsonDocument.Parse("""
        {
          "metadata": { "label": "***", "token": "<redacted>" }
        }
        """);
        var edited = new[] { "metadata" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated.RootElement,
            edited,
            stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated.RootElement,
            edited,
            stored.RootElement);

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("metadata").GetProperty("label").GetString());
        Assert.False(sent.GetProperty("metadata").TryGetProperty("token", out _));
    }

    [Fact]
    public void FindUneditedRedactionSentinel_RefusesParentEditInsideNoIdArray()
    {
        using var stored = JsonDocument.Parse("""
        {
          "profiles": {
            "accounts": [ { "label": "old", "token": "<redacted>" } ]
          }
        }
        """);
        using var updated = JsonDocument.Parse("""
        {
          "profiles": {
            "accounts": [ { "label": "new", "token": "<redacted>" } ]
          }
        }
        """);
        var edited = new[] { "profiles" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated.RootElement,
            edited,
            stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated.RootElement,
            edited,
            stored.RootElement);

        Assert.Equal("profiles.accounts[0].token", blocked);
        Assert.Equal("new", sent.GetProperty("profiles").GetProperty("accounts")[0].GetProperty("label").GetString());
        Assert.True(sent.GetProperty("profiles").GetProperty("accounts")[0].TryGetProperty("token", out _));
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_KeepsAccountRenameOrRefusesIt()
    {
        using var stored = JsonDocument.Parse("""
        {"profiles":{"accounts":[{"token":"<redacted>","label":"desk"}]}}
        """);
        using var staged = JsonDocument.Parse("""
        {"accounts":[{"token":"<redacted>","label":"renamed"}]}
        """);
        var updated = ConfigEditorModel.ApplyChanges(
            stored.RootElement,
            new Dictionary<string, object?> { ["profiles"] = staged.RootElement.Clone() });
        var edited = new[] { "profiles" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(updated, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited, stored.RootElement);

        Assert.Equal("profiles.accounts[0].token", blocked);
        Assert.True(sent.GetProperty("profiles").TryGetProperty("accounts", out var accounts));
        Assert.Equal("renamed", accounts[0].GetProperty("label").GetString());
        Assert.Equal("<redacted>", accounts[0].GetProperty("token").GetString());
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_KeepsNewlyEnteredOrdinaryMask()
    {
        using var stored = JsonDocument.Parse("""{"metadata":{"label":"before"}}""");
        using var staged = JsonDocument.Parse("""{"label":"***"}""");
        var updated = ConfigEditorModel.ApplyChanges(
            stored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = staged.RootElement.Clone() });
        var edited = new[] { "metadata" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(updated, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited, stored.RootElement);

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("metadata").GetProperty("label").GetString());
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_RefusesNewlyEnteredCredentialMask()
    {
        using var stored = JsonDocument.Parse("""{"metadata":{"token":"before"}}""");
        using var staged = JsonDocument.Parse("""{"token":"***"}""");
        var updated = ConfigEditorModel.ApplyChanges(
            stored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = staged.RootElement.Clone() });
        var edited = new[] { "metadata" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(updated, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited, stored.RootElement);

        Assert.Equal("metadata.token", blocked);
        Assert.Equal("***", sent.GetProperty("metadata").GetProperty("token").GetString());
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_KeepsChangedMaskLiteral()
    {
        using var stored = JsonDocument.Parse("""{"metadata":{"label":"***"}}""");
        using var staged = JsonDocument.Parse("""{"label":"*****"}""");
        var updated = ConfigEditorModel.ApplyChanges(
            stored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = staged.RootElement.Clone() });
        var edited = new[] { "metadata" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(updated, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited, stored.RootElement);

        Assert.Null(blocked);
        Assert.Equal("*****", sent.GetProperty("metadata").GetProperty("label").GetString());
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_KeepsSpacingAndCaseAroundALoadedMask()
    {
        using var spacedStored = JsonDocument.Parse("""{"metadata":{"label":"***"}}""");
        using var spacedStaged = JsonDocument.Parse("""{"label":" *** "}""");
        var spacedUpdated = ConfigEditorModel.ApplyChanges(
            spacedStored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = spacedStaged.RootElement.Clone() });
        var edited = new[] { "metadata" };
        var spacedBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            spacedUpdated,
            edited,
            spacedStored.RootElement);
        var spacedSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            spacedUpdated,
            edited,
            spacedStored.RootElement);
        Assert.Null(spacedBlocked);
        Assert.Equal(" *** ", spacedSent.GetProperty("metadata").GetProperty("label").GetString());

        using var caseStored = JsonDocument.Parse("""{"metadata":{"label":"[REDACTED]"}}""");
        using var caseStaged = JsonDocument.Parse("""{"label":"[redacted]"}""");
        var caseUpdated = ConfigEditorModel.ApplyChanges(
            caseStored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = caseStaged.RootElement.Clone() });
        var caseBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            caseUpdated,
            edited,
            caseStored.RootElement);
        var caseSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            caseUpdated,
            edited,
            caseStored.RootElement);
        Assert.Null(caseBlocked);
        Assert.Equal("[redacted]", caseSent.GetProperty("metadata").GetProperty("label").GetString());

        using var tokenStored = JsonDocument.Parse("""{"metadata":{"token":"***"}}""");
        using var tokenStaged = JsonDocument.Parse("""{"token":" *** "}""");
        var tokenUpdated = ConfigEditorModel.ApplyChanges(
            tokenStored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = tokenStaged.RootElement.Clone() });
        var tokenBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            tokenUpdated,
            edited,
            tokenStored.RootElement);
        var tokenSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            tokenUpdated,
            edited,
            tokenStored.RootElement);
        Assert.Equal("metadata.token", tokenBlocked);
        Assert.Equal(" *** ", tokenSent.GetProperty("metadata").GetProperty("token").GetString());
    }

    [Fact]
    public void ApplyChanges_ParentObjectEdit_RefusesChangedCredentialMask()
    {
        using var stored = JsonDocument.Parse("""{"metadata":{"token":"***"}}""");
        using var staged = JsonDocument.Parse("""{"token":"*****"}""");
        var updated = ConfigEditorModel.ApplyChanges(
            stored.RootElement,
            new Dictionary<string, object?> { ["metadata"] = staged.RootElement.Clone() });
        var edited = new[] { "metadata" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(updated, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited, stored.RootElement);

        Assert.Equal("metadata.token", blocked);
        Assert.Equal("*****", sent.GetProperty("metadata").GetProperty("token").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsLiteralMaskInANonSecretField()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "label": "hello", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.label"] = "***",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.slack.label" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.slack.label" });

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("channels").GetProperty("slack").GetProperty("label").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsUntouchedArrayApiKeyButKeepsSiblingMask()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "label": "<redacted>", "token": "<redacted>", "apiKey": "<redacted>", "api_key": "<redacted>" } ]
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.enabled"] = true,
            });
        var edited = new[] { "channels.slack.enabled" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited);

        Assert.Null(blocked);
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("accounts", out _));

        using var siblingOnly = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "label": "<redacted>" } ]
            }
          }
        }
        """);

        var siblingBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            siblingOnly.RootElement,
            Array.Empty<string>(),
            siblingOnly.RootElement);
        var siblingSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            siblingOnly.RootElement,
            Array.Empty<string>());

        Assert.Null(siblingBlocked);
        Assert.Equal(
            "<redacted>",
            siblingSent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0].GetProperty("label").GetString());

        using var snake = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "note": "<redacted>", "api_key": "<redacted>" } ]
            }
          }
        }
        """);

        var snakeBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            snake.RootElement,
            Array.Empty<string>(),
            snake.RootElement);
        var snakeSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            snake.RootElement,
            Array.Empty<string>());

        Assert.Null(snakeBlocked);
        Assert.False(snakeSent.GetProperty("channels").GetProperty("slack").TryGetProperty("accounts", out _));
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsUntouchedArrayPrivateKey()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "label": "<redacted>", "privateKey": "<redacted>" } ]
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.enabled"] = true,
            });
        var edited = new[] { "channels.slack.enabled" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited);

        Assert.Null(blocked);
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("accounts", out _));

        using var cased = JsonDocument.Parse("""
        {
          "accounts": [ { "PrivateKey": "<redacted>", "title": "<redacted>" } ]
        }
        """);

        var casedBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            cased.RootElement,
            Array.Empty<string>(),
            cased.RootElement);
        var casedSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            cased.RootElement,
            Array.Empty<string>());

        Assert.Null(casedBlocked);
        Assert.False(casedSent.TryGetProperty("accounts", out _));
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksEditedArrayCredentialThatIsStillTheSentinel()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "apiKey": "<redacted>", "privateKey": "<redacted>", "label": "desk" } ]
            }
          }
        }
        """);

        var edited = new[] { "channels.slack.accounts[0].apiKey", "channels.slack.accounts[0].privateKey" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(document.RootElement, edited);

        Assert.Equal("channels.slack.accounts[0].apiKey", blocked);
        var account = sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.Equal("<redacted>", account.GetProperty("apiKey").GetString());
        Assert.Equal("<redacted>", account.GetProperty("privateKey").GetString());
        Assert.Equal("desk", account.GetProperty("label").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksIndexedEditInsideNoIdArray()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "token": "<redacted>", "label": "renamed" } ]
            }
          }
        }
        """);

        var edited = new[] { "channels.slack.accounts[0].label" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(document.RootElement, edited);

        Assert.Equal("channels.slack.accounts[0].token", blocked);
        var account = sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.Equal("<redacted>", account.GetProperty("token").GetString());
        Assert.Equal("renamed", account.GetProperty("label").GetString());

        var cased = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            new[] { "Channels.Slack.Accounts[0].Label" },
            document.RootElement);
        Assert.Equal("channels.slack.accounts[0].token", cased);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsOutermostNoIdArrayAndKeepsIdKeyedFields()
    {
        using var outer = JsonDocument.Parse("""
        {
          "enabled": true,
          "groups": [ { "accounts": [ { "id": "acct-1", "token": "<redacted>" } ] } ]
        }
        """);
        var outerEdited = new[] { "enabled" };
        var outerBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            outer.RootElement,
            outerEdited,
            outer.RootElement);
        var outerSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(outer.RootElement, outerEdited);

        Assert.Null(outerBlocked);
        Assert.True(outerSent.GetProperty("enabled").GetBoolean());
        Assert.False(outerSent.TryGetProperty("groups", out _));

        using var inner = JsonDocument.Parse("""
        {
          "groups": [ {
            "id": "g1",
            "name": "new",
            "accounts": [ { "token": "<redacted>", "label": "desk" } ]
          } ]
        }
        """);
        var innerEdited = new[] { "groups[0].name" };
        var innerBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            inner.RootElement,
            innerEdited,
            inner.RootElement);
        var innerSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(inner.RootElement, innerEdited);

        Assert.Null(innerBlocked);
        var group = innerSent.GetProperty("groups")[0];
        Assert.Equal("g1", group.GetProperty("id").GetString());
        Assert.Equal("new", group.GetProperty("name").GetString());
        Assert.False(group.TryGetProperty("accounts", out _));

        using var mixed = JsonDocument.Parse("""
        {
          "enabled": true,
          "accounts": [
            { "id": "acct-1", "token": "<redacted>" },
            { "token": "<redacted>" }
          ]
        }
        """);
        var mixedSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            mixed.RootElement,
            new[] { "enabled" });
        Assert.False(mixedSent.TryGetProperty("accounts", out _));
    }

    [Fact]
    public void NoIdArray_RefusesIndexedEditAndAcceptsUnrelatedSave()
    {
        using var stored = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "token": "stored-secret", "label": "desk" } ]
            }
          }
        }
        """);
        using var unrelated = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": true,
              "accounts": [ { "token": "<redacted>", "label": "desk" } ]
            }
          }
        }
        """);
        var unrelatedEdited = new[] { "channels.slack.enabled" };
        var unrelatedBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            unrelated.RootElement,
            unrelatedEdited,
            stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            unrelated.RootElement,
            unrelatedEdited,
            stored.RootElement);

        Assert.Null(unrelatedBlocked);
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("accounts", out _));
        var accepted = GatewayArrayPatch.Apply(stored.RootElement, sent);
        Assert.Null(accepted.Error);
        Assert.Equal(
            "stored-secret",
            accepted.Merged["channels"]!["slack"]!["accounts"]![0]!["token"]!.GetValue<string>());
        Assert.True(accepted.Merged["channels"]!["slack"]!["enabled"]!.GetValue<bool>());

        using var indexed = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "token": "<redacted>", "label": "renamed" } ]
            }
          }
        }
        """);
        var indexedEdited = new[] { "channels.slack.accounts[0].label" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            indexed.RootElement,
            indexedEdited,
            stored.RootElement);
        Assert.Equal("channels.slack.accounts[0].token", blocked);

        using var stripped = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "label": "renamed" } ]
            }
          }
        }
        """);
        var rejected = GatewayArrayPatch.Apply(stored.RootElement, stripped.RootElement);
        Assert.NotNull(rejected.Error);
        Assert.Contains(
            "config.patch would remove entries from array path(s): channels.slack.accounts",
            rejected.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IdKeyedArray_OmitsCredentialFieldAndGatewayKeepsStoredSecret()
    {
        using var stored = JsonDocument.Parse("""
        {
          "enabled": false,
          "accounts": [ { "id": "acct-1", "token": "stored-secret", "label": "desk" } ]
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "enabled": true,
          "accounts": [ { "id": "acct-1", "token": "<redacted>", "label": "desk" } ]
        }
        """);
        var edited = new[] { "enabled" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement,
            edited,
            stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement,
            edited,
            stored.RootElement);

        Assert.Null(blocked);
        var sentAccount = sent.GetProperty("accounts")[0];
        Assert.Equal("acct-1", sentAccount.GetProperty("id").GetString());
        Assert.Equal("desk", sentAccount.GetProperty("label").GetString());
        Assert.False(sentAccount.TryGetProperty("token", out _));

        var accepted = GatewayArrayPatch.Apply(stored.RootElement, sent);
        Assert.Null(accepted.Error);
        Assert.Equal("stored-secret", accepted.Merged["accounts"]![0]!["token"]!.GetValue<string>());
        Assert.Equal("desk", accepted.Merged["accounts"]![0]!["label"]!.GetValue<string>());
        Assert.True(accepted.Merged["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void IdKeyedArray_ReorderUnderParentEdit_KeepsEachStoredSecret()
    {
        using var stored = JsonDocument.Parse("""
        {
          "accounts": [
            { "id": "acct-a", "token": "secret-a", "label": "one" },
            { "id": "acct-b", "token": "secret-b", "label": "two" }
          ]
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "accounts": [
            { "id": "acct-b", "token": "<redacted>", "label": "two" },
            { "id": "acct-a", "token": "<redacted>", "label": "renamed" }
          ]
        }
        """);
        var edited = new[] { "accounts" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);
        Assert.Equal("accounts[0].token", blocked);
        Assert.Equal("<redacted>", sent.GetProperty("accounts")[0].GetProperty("token").GetString());
        Assert.Equal("renamed", sent.GetProperty("accounts")[1].GetProperty("label").GetString());
    }

    [Fact]
    public void IdKeyedArray_ChangedTokenToMask_IsRefusedAndKeepsTheDraft()
    {
        using var stored = JsonDocument.Parse("""
        { "accounts": [ { "id": "desk", "token": "before" } ] }
        """);
        using var editor = JsonDocument.Parse("""
        { "accounts": [ { "id": "desk", "token": "***" } ] }
        """);
        var edited = new[] { "accounts" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Equal("accounts[0].token", blocked);
        Assert.Equal("***", sent.GetProperty("accounts")[0].GetProperty("token").GetString());
    }

    [Fact]
    public void DottedProviderKey_OmitsUnchangedMaskWithoutSplittingTheKey()
    {
        using var stored = JsonDocument.Parse("""
        { "models": { "providers": { "custom.openai": { "apiKey": "***", "url": "https://old.example" } } } }
        """);
        using var editor = JsonDocument.Parse("""
        { "models": { "providers": { "custom.openai": { "apiKey": "***", "url": "https://new.example" } } } }
        """);
        var edited = new[] { "models.providers" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Null(blocked);
        var provider = sent.GetProperty("models").GetProperty("providers").GetProperty("custom.openai");
        Assert.False(provider.TryGetProperty("apiKey", out _));
        Assert.Equal("https://new.example", provider.GetProperty("url").GetString());
    }

    [Fact]
    public void GatewayRedactionMask_UnrelatedEdit_OmitsApiKeyAndKeepsModels()
    {
        using var stored = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom.openai": {
                "apiKey": "__OPENCLAW_REDACTED__",
                "baseUrl": "https://old.example",
                "models": [ { "id": "m1", "name": "one" } ]
              }
            }
          }
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom.openai": {
                "apiKey": "__OPENCLAW_REDACTED__",
                "baseUrl": "https://new.example",
                "models": [ { "id": "m1", "name": "one" } ]
              }
            }
          }
        }
        """);
        var edited = new[] { "models.providers.custom.openai.baseUrl" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);
        var provider = sent.GetProperty("models").GetProperty("providers").GetProperty("custom.openai");

        Assert.Null(blocked);
        Assert.False(provider.TryGetProperty("apiKey", out _));
        Assert.Equal("https://new.example", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("m1", provider.GetProperty("models")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void GatewayRedactionMask_ParentEdit_RefusesAndKeepsTheDraft()
    {
        using var stored = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom.openai": {
                "apiKey": "__OPENCLAW_REDACTED__",
                "baseUrl": "https://old.example",
                "models": [ { "id": "m1" } ]
              }
            }
          }
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom.openai": {
                "apiKey": "***",
                "baseUrl": "https://draft.example",
                "models": [ { "id": "m1" } ]
              }
            }
          }
        }
        """);
        var edited = new[] { "models.providers" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);
        var provider = sent.GetProperty("models").GetProperty("providers").GetProperty("custom.openai");

        Assert.Equal("models.providers.custom.openai.apiKey", blocked);
        Assert.Equal("***", provider.GetProperty("apiKey").GetString());
        Assert.Equal("https://draft.example", provider.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public void GatewayRedactionMask_DirectEdit_RefusesLegacyPlaceholder()
    {
        using var stored = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom": {
                "apiKey": "__OPENCLAW_REDACTED__",
                "baseUrl": "https://old.example"
              }
            }
          }
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "models": {
            "providers": {
              "custom": {
                "apiKey": "***",
                "baseUrl": "https://old.example"
              }
            }
          }
        }
        """);
        var edited = new[] { "models.providers.custom.apiKey" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);
        var provider = sent.GetProperty("models").GetProperty("providers").GetProperty("custom");

        Assert.Equal("models.providers.custom.apiKey", blocked);
        Assert.Equal("***", provider.GetProperty("apiKey").GetString());
        Assert.Equal("https://old.example", provider.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public void GatewayRedactionMask_DirectSecretRefIdEdit_KeepsTheReplacement()
    {
        using var stored = JsonDocument.Parse("""
        { "auth": { "profiles": { "one": { "id": "__OPENCLAW_REDACTED__" } } } }
        """);
        using var editor = JsonDocument.Parse("""
        { "auth": { "profiles": { "one": { "id": "***" } } } }
        """);
        var edited = new[] { "auth.profiles.one.id" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("auth").GetProperty("profiles").GetProperty("one").GetProperty("id").GetString());
    }

    [Fact]
    public void GatewayRedactionMask_SecretRefProviderEdit_KeepsTheMaskedId()
    {
        using var stored = JsonDocument.Parse("""
        {
          "auth": {
            "profiles": {
              "one": {
                "source": "env",
                "provider": "one",
                "id": "__OPENCLAW_REDACTED__"
              }
            }
          }
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "auth": {
            "profiles": {
              "one": {
                "source": "env",
                "provider": "two",
                "id": "__OPENCLAW_REDACTED__"
              }
            }
          }
        }
        """);
        var edited = new[] { "auth.profiles" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);
        var profile = sent.GetProperty("auth").GetProperty("profiles").GetProperty("one");

        Assert.Null(blocked);
        Assert.Equal("two", profile.GetProperty("provider").GetString());
        Assert.Equal("__OPENCLAW_REDACTED__", profile.GetProperty("id").GetString());
    }

    [Fact]
    public void DottedProviderKey_NonIdAccountsArray_RefusesAndKeepsTheMask()
    {
        using var stored = JsonDocument.Parse("""
        { "custom.openai": { "accounts": [ { "token": "***", "label": "desk" } ] } }
        """);
        using var editor = JsonDocument.Parse("""
        { "custom.openai": { "accounts": [ { "token": "***", "label": "renamed" } ] } }
        """);
        var edited = new[] { "custom.openai" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Equal("custom.openai.accounts[0].token", blocked);
        var account = sent.GetProperty("custom.openai").GetProperty("accounts")[0];
        Assert.Equal("***", account.GetProperty("token").GetString());
        Assert.Equal("renamed", account.GetProperty("label").GetString());
    }

    [Fact]
    public void DottedProviderKey_UnrelatedEdit_RemovesTheNonIdAccountsArray()
    {
        using var stored = JsonDocument.Parse("""
        { "enabled": false, "custom.openai": { "accounts": [ { "token": "***" } ] } }
        """);
        using var editor = JsonDocument.Parse("""
        { "enabled": true, "custom.openai": { "accounts": [ { "token": "***" } ] } }
        """);
        var edited = new[] { "enabled" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Null(blocked);
        Assert.True(sent.GetProperty("enabled").GetBoolean());
        Assert.False(sent.GetProperty("custom.openai").TryGetProperty("accounts", out _));
    }

    [Fact]
    public void NestedArray_ReorderedOuterIds_DoNotTreatTheOtherRowsArrayAsIdKeyed()
    {
        using var stored = JsonDocument.Parse("""
        {
          "groups": [
            { "id": "row-a", "accounts": [ { "id": "acct-1", "token": "secret-a" } ] },
            { "id": "row-b", "accounts": [ { "token": "***" } ] }
          ]
        }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "groups": [
            { "id": "row-b", "accounts": [ { "token": "***" } ] },
            { "id": "row-a", "accounts": [ { "id": "acct-1", "token": "<redacted>" } ] }
          ]
        }
        """);
        var edited = new[] { "groups" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Equal("groups[0].accounts[0].token", blocked);
        Assert.Equal("***", sent.GetProperty("groups")[0].GetProperty("accounts")[0].GetProperty("token").GetString());
    }

    [Fact]
    public void IdKeyedArray_NewRowWithMask_IsRefusedAndKeepsTheDraft()
    {
        using var stored = JsonDocument.Parse("""
        { "accounts": [ { "id": "acct-1", "token": "<redacted>", "label": "desk" } ] }
        """);
        using var editor = JsonDocument.Parse("""
        {
          "accounts": [
            { "id": "acct-1", "token": "<redacted>", "label": "desk" },
            { "id": "acct-2", "token": "<redacted>", "label": "new" }
          ]
        }
        """);
        var edited = new[] { "accounts" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            editor.RootElement, edited, stored.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            editor.RootElement, edited, stored.RootElement);

        Assert.Equal("accounts[1].token", blocked);
        Assert.Equal("<redacted>", sent.GetProperty("accounts")[1].GetProperty("token").GetString());
        Assert.False(sent.GetProperty("accounts")[0].TryGetProperty("token", out _));
    }

    /// <summary>
    /// Models the Gateway config.patch array merge used by these saves:
    /// id-keyed arrays keep omitted keys, other arrays replace, and a
    /// replacement that drops a stored entry is rejected.
    /// </summary>
    private static class GatewayArrayPatch
    {
        public readonly record struct Result(JsonNode Merged, string? Error);

        public static Result Apply(JsonElement stored, JsonElement patch)
        {
            var storedNode = JsonNode.Parse(stored.GetRawText()) ?? new JsonObject();
            var patchNode = JsonNode.Parse(patch.GetRawText()) ?? new JsonObject();
            var merged = MergeObjects(storedNode as JsonObject, patchNode as JsonObject ?? new JsonObject());
            var paths = new List<string>();
            CollectDestructive(storedNode, patchNode, merged, "", paths);
            if (paths.Count == 0)
                return new Result(merged, null);

            return new Result(
                merged,
                "config.patch would remove entries from array path(s): " + string.Join(", ", paths) +
                ". Pass replacePaths with the exact path(s) when this is intentional, or use config.apply for full-config replacement.");
        }

        private static JsonObject MergeObjects(JsonObject? baseObject, JsonObject patch)
        {
            var result = baseObject?.DeepClone().AsObject() ?? new JsonObject();
            foreach (var property in patch)
            {
                if (property.Value is null)
                {
                    result.Remove(property.Key);
                    continue;
                }

                if (result[property.Key] is JsonArray baseArray && property.Value is JsonArray patchArray)
                {
                    result[property.Key] = IsIdKeyed(baseArray)
                        ? MergeIdArray(baseArray, patchArray)
                        : patchArray.DeepClone();
                    continue;
                }

                if (property.Value is JsonObject childPatch)
                {
                    result[property.Key] = MergeObjects(result[property.Key] as JsonObject, childPatch);
                    continue;
                }

                result[property.Key] = property.Value.DeepClone();
            }

            return result;
        }

        private static JsonArray MergeIdArray(JsonArray baseArray, JsonArray patchArray)
        {
            var merged = baseArray.DeepClone().AsArray();
            foreach (var patchEntry in patchArray)
            {
                if (patchEntry is not JsonObject patchObject || !TryGetStringId(patchObject, out var id))
                {
                    merged.Add(patchEntry?.DeepClone());
                    continue;
                }

                var index = IndexOfId(merged, id);
                if (index < 0)
                {
                    merged.Add(patchObject.DeepClone());
                    continue;
                }

                merged[index] = MergeObjects(merged[index] as JsonObject, patchObject);
            }

            return merged;
        }

        private static void CollectDestructive(
            JsonNode? baseNode,
            JsonNode? patch,
            JsonNode? merged,
            string path,
            List<string> paths)
        {
            if (patch is not JsonObject patchObject || baseNode is not JsonObject baseObject)
                return;

            var mergedObject = merged as JsonObject;
            foreach (var property in patchObject)
            {
                var childPath = path.Length == 0 ? property.Key : $"{path}.{property.Key}";
                var baseValue = baseObject[property.Key];
                var mergedValue = mergedObject?[property.Key];
                if (baseValue is JsonArray baseArray)
                {
                    if (property.Value is not JsonArray)
                    {
                        paths.Add(childPath);
                        continue;
                    }

                    if (mergedValue is JsonArray mergedArray)
                    {
                        if (IsIdKeyed(baseArray))
                        {
                            if (!PreservesIds(baseArray, mergedArray))
                            {
                                paths.Add(childPath);
                                continue;
                            }
                        }
                        else if (!PreservesEntries(baseArray, mergedArray))
                        {
                            paths.Add(childPath);
                            continue;
                        }
                    }
                }
                else if (property.Value is JsonObject childPatch)
                {
                    CollectDestructive(baseValue, childPatch, mergedValue, childPath, paths);
                }
            }
        }

        private static bool IsIdKeyed(JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject obj || !TryGetStringId(obj, out _))
                    return false;
            }

            return true;
        }

        private static bool PreservesIds(JsonArray baseArray, JsonArray merged)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in merged)
            {
                if (item is JsonObject obj && TryGetStringId(obj, out var id))
                    ids.Add(id);
            }

            foreach (var item in baseArray)
            {
                if (item is not JsonObject obj || !TryGetStringId(obj, out var id) || !ids.Contains(id))
                    return false;
            }

            return true;
        }

        private static bool PreservesEntries(JsonArray baseArray, JsonArray merged)
        {
            var unused = new List<JsonNode?>();
            foreach (var item in merged)
                unused.Add(item);

            foreach (var baseEntry in baseArray)
            {
                var match = unused.FindIndex(item => JsonNode.DeepEquals(item, baseEntry));
                if (match < 0)
                    return false;
                unused.RemoveAt(match);
            }

            return true;
        }

        private static bool TryGetStringId(JsonObject obj, out string id)
        {
            id = "";
            if (obj["id"] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrEmpty(text))
            {
                return false;
            }

            id = text;
            return true;
        }

        private static int IndexOfId(JsonArray array, string id)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonObject obj &&
                    TryGetStringId(obj, out var existing) &&
                    existing == id)
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
