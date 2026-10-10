using System.Text.Json;
using OpenClawTray.Helpers;
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
    public void SensitiveArray_UnrelatedEdit_PreservesStoredEntries()
    {
        var session = new SensitiveArrayEditSession(2);

        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Null(session.Replacement);
        Assert.Equal("", session.Draft);
        Assert.Equal("2 entries are configured. Stored values stay hidden.", session.CountText);
        Assert.DoesNotContain("stored-secret", session.CountText, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveArray_ReplaceAll_SendsOnlyTheNewArray()
    {
        var session = new SensitiveArrayEditSession(2);
        session.BeginReplace();
        Assert.Equal("", session.Draft);

        session.SetDraft("""[{"url":"https://new.example/hook"}]""");
        Assert.True(session.TryApplyReplace());

        Assert.Equal(SensitiveArrayDecision.Replace, session.Decision);
        var raw = session.Replacement!.Value.GetRawText();
        Assert.Contains("https://new.example/hook", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret", raw, StringComparison.Ordinal);
        Assert.Equal("", session.Draft);
    }

    [Fact]
    public void SensitiveArray_ClearAll_SendsAnEmptyArray()
    {
        var session = new SensitiveArrayEditSession(2);
        session.BeginClear();
        Assert.True(session.ClearConfirmOpen);
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);

        session.ConfirmClear();

        Assert.Equal(SensitiveArrayDecision.Clear, session.Decision);
        Assert.Null(session.Replacement);
        Assert.Equal(0, SensitiveArrayEditSession.EmptyArray().GetArrayLength());
    }

    [Fact]
    public void SensitiveArray_CancelReplaceOrClear_KeepsTheStoredArray()
    {
        var session = new SensitiveArrayEditSession(1);
        session.BeginReplace();
        session.SetDraft("""[{"token":"typed-then-cancelled"}]""");
        session.CancelReplace();

        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Equal("", session.Draft);
        Assert.Null(session.Replacement);

        session.BeginClear();
        session.CancelClear();
        Assert.False(session.ClearConfirmOpen);
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
    }

    [Fact]
    public void SensitiveArray_RejectedRetryThenCanceledClear_RestoresCommittedReplacement()
    {
        var session = new SensitiveArrayEditSession(1);
        using var committed = JsonDocument.Parse("""[{"url":"https://example.invalid/hook-dummy"}]""");
        session.CommitReplace(committed.RootElement.Clone());

        session.BeginReplace();
        session.SetDraft("not-json");
        Assert.False(session.TryReadDraft(out _));

        session.BeginClear();
        session.CancelClear();

        Assert.True(session.TryRestoreCommittedReplacement(out var restored));
        Assert.Equal(SensitiveArrayDecision.Replace, session.Decision);
        Assert.Contains("hook-dummy", restored.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveArray_NearMatchObject_IsNotASecretArrayDecision()
    {
        Assert.False(ConfigPathSensitivity.IsSensitive("channels.googlechat.webhookUrlExtra"));
        Assert.True(ConfigPathSensitivity.IsSensitive("channels.googlechat.webhookUrl"));
    }

    [Fact]
    public void SensitiveObject_UnrelatedEditAndCancel_PreserveStoredObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "webhookUrl": {
              "id": "stored-dummy",
              "material": "stored-secret"
            }
          },
          "mode": "hybrid"
        }
        """);

        var session = new SensitiveArrayEditSession(2, JsonValueKind.Object);
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Equal("", session.Draft);
        Assert.DoesNotContain("stored-dummy", session.CountText, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret", session.CountText, StringComparison.Ordinal);

        session.BeginReplace();
        session.SetDraft("""{"id":"typed-then-cancelled"}""");
        session.CancelReplace();
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Null(session.Replacement);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.webhookUrl"] = new object(),
                ["mode"] = "manual",
            });

        Assert.Equal("stored-dummy", updated.GetProperty("channels").GetProperty("webhookUrl").GetProperty("id").GetString());
        Assert.Equal("stored-secret", updated.GetProperty("channels").GetProperty("webhookUrl").GetProperty("material").GetString());
        Assert.Equal("manual", updated.GetProperty("mode").GetString());
    }

    [Fact]
    public void SensitiveObject_ReplaceSendsOnlyTheNewObject_ClearSendsEmptyObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "webhookUrl": {
              "id": "stored-dummy"
            }
          }
        }
        """);

        var session = new SensitiveArrayEditSession(1, JsonValueKind.Object);
        session.BeginReplace();
        session.SetDraft("""["not-an-object"]""");
        Assert.False(session.TryApplyReplace());
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);

        session.SetDraft("""{"id":"replacement-dummy"}""");
        Assert.True(session.TryApplyReplace());
        Assert.Equal(JsonValueKind.Object, session.Replacement!.Value.ValueKind);
        Assert.Contains("replacement-dummy", session.Replacement.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("stored-dummy", session.Replacement.Value.GetRawText(), StringComparison.Ordinal);

        var replaced = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.webhookUrl"] = session.Replacement,
            });
        Assert.Equal("replacement-dummy", replaced.GetProperty("channels").GetProperty("webhookUrl").GetProperty("id").GetString());
        Assert.False(replaced.GetProperty("channels").GetProperty("webhookUrl").TryGetProperty("material", out _));

        var cleared = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.webhookUrl"] = SensitiveArrayEditSession.EmptyObject(),
            });
        Assert.Equal(JsonValueKind.Object, cleared.GetProperty("channels").GetProperty("webhookUrl").ValueKind);
        Assert.Empty(cleared.GetProperty("channels").GetProperty("webhookUrl").EnumerateObject());
    }

    [Theory]
    [InlineData("""{"type":"object"}""", """["not-an-object"]""", "Item 1: Must be a JSON object.")]
    [InlineData("""{"type":"object"}""", """[{}, "not-an-object"]""", "Item 2: Must be a JSON object.")]
    [InlineData("""{"type":"string"}""", """[{"url":"https://example.invalid/hook"}]""", "Item 1: Must be a JSON string.")]
    public void ArrayItemKinds_RejectsTheWrongJsonKind(string itemSchema, string arrayJson, string expected)
    {
        using var schema = JsonDocument.Parse(itemSchema);
        using var value = JsonDocument.Parse(arrayJson);
        Assert.Equal(expected, ConfigEditorModel.FirstArrayItemKindError(value.RootElement, schema.RootElement));
    }

    [Fact]
    public void ArrayItemKinds_AcceptsObjectAndStringReplacements()
    {
        using var objects = JsonDocument.Parse("""{"type":"object"}""");
        using var strings = JsonDocument.Parse("""{"type":"string"}""");
        using var objectValue = JsonDocument.Parse("""[{"url":"https://example.invalid/hook"}]""");
        using var stringValue = JsonDocument.Parse("""["https://example.invalid/hook"]""");

        Assert.Null(ConfigEditorModel.FirstArrayItemKindError(objectValue.RootElement, objects.RootElement));
        Assert.Null(ConfigEditorModel.FirstArrayItemKindError(stringValue.RootElement, strings.RootElement));
    }

    [Fact]
    public void UseHiddenObjectEditor_SensitiveObjectWithProperties_StaysHidden()
    {
        using var document = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "signingSecret": {
              "type": "object",
              "properties": {
                "value": { "type": "string" }
              }
            },
            "token": { "type": "string" },
            "displayName": { "type": "string" },
            "webhookUrl": {
              "type": ["object", "null"],
              "properties": {
                "id": { "type": "string" }
              }
            }
          }
        }
        """);

        var properties = document.RootElement.GetProperty("properties");
        Assert.True(ConfigEditorModel.UseHiddenObjectEditor(
            "channels.slack.signingSecret",
            properties.GetProperty("signingSecret")));
        Assert.True(ConfigEditorModel.UseHiddenObjectEditor(
            "channels.googlechat.webhookUrl",
            properties.GetProperty("webhookUrl")));
        Assert.False(ConfigEditorModel.UseHiddenObjectEditor(
            "channels.discord.token",
            properties.GetProperty("token")));
        Assert.False(ConfigEditorModel.UseHiddenObjectEditor(
            "channels.slack.displayName",
            properties.GetProperty("displayName")));
        Assert.False(ConfigEditorModel.UseHiddenObjectEditor(
            "channels.slack",
            document.RootElement));
    }
}
