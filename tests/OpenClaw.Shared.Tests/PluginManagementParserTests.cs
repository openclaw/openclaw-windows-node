using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class PluginManagementParserTests
{
    [Fact]
    public void ParseInspection_RequiresCompleteReviewData()
    {
        var inspection = PluginManagementParser.ParseInspection(Json("""
            {
              "ok": true,
              "plugin": {
                "id": "diagnostics-otel",
                "name": "Diagnostics OpenTelemetry",
                "description": "Exports telemetry.",
                "installed": false,
                "enabled": false
              },
              "source": {
                "kind": "official-catalog",
                "packageName": "@openclaw/diagnostics-otel"
              },
              "declared": {
                "channels": [],
                "providers": [],
                "tools": ["diagnostics.export"],
                "contracts": [],
                "hooks": [],
                "mcpServers": [],
                "cliCommands": [],
                "cliBackends": [],
                "skills": [],
                "dangerousConfigFlags": ["diagnostics.otel.endpoint"]
              },
              "reviewToken": "review-token",
              "grants": {
                "hooks": {
                  "allowPromptInjection": false,
                  "allowConversationAccess": false
                },
                "llm": {
                  "allowedModels": ["gpt-5", "claude"]
                }
              },
              "trust": {
                "disposition": "review-recommended",
                "reasons": ["Network exporter"]
              }
            }
            """));

        Assert.Equal("diagnostics-otel", inspection.PluginId);
        Assert.Equal("@openclaw/diagnostics-otel", inspection.PackageName);
        Assert.Equal("review-token", inspection.ReviewToken);
        Assert.Collection(
            inspection.DeclaredCapabilities,
            group =>
            {
                Assert.Equal("tools", group.Name);
                Assert.Equal(["diagnostics.export"], group.Values);
            },
            group =>
            {
                Assert.Equal("dangerousConfigFlags", group.Name);
                Assert.Equal(["diagnostics.otel.endpoint"], group.Values);
            });
        Assert.Contains("hooks.allowPromptInjection: False", inspection.Grants);
        Assert.Contains("llm.allowedModels: gpt-5, claude", inspection.Grants);
        Assert.Equal("review-recommended", inspection.TrustDisposition);
        Assert.Equal(["Network exporter"], inspection.TrustReasons);
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"plugin":{"id":"p","name":"Plugin"},"declared":{},"reviewToken":""}""")]
    [InlineData("""{"ok":false}""")]
    [InlineData("""[]""")]
    public void ParseInspection_RejectsDataThatCannotPopulateApproval(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            PluginManagementParser.ParseInspection(Json(json)));
    }

    [Fact]
    public void ParseInstall_ProjectsRestartAndWarnings()
    {
        var result = PluginManagementParser.ParseInstall(Json("""
            {
              "ok": true,
              "restartRequired": true,
              "warnings": ["Reload deferred"]
            }
            """));

        Assert.True(result.RestartRequired);
        Assert.Equal(["Reload deferred"], result.Warnings);
    }

    [Theory]
    [InlineData("""{"ok":false}""")]
    [InlineData("""{"ok":true,"warnings":"bad"}""")]
    public void ParseInstall_DoesNotInventSuccess(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            PluginManagementParser.ParseInstall(Json(json)));
    }

    [Fact]
    public void ParseSkillInstall_ProjectsVerifiedResult()
    {
        var result = ClawHubSkillManagementParser.ParseInstall(Json("""
            {
              "ok": true,
              "slug": "@alipay/alipay-aipay",
              "version": "1.2.3",
              "warning": "Review the installed skill before use."
            }
            """));

        Assert.Equal("@alipay/alipay-aipay", result.Slug);
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal("Review the installed skill before use.", result.Warning);
    }

    [Theory]
    [InlineData("""{"ok":false}""")]
    [InlineData("""{"ok":true,"slug":"skill"}""")]
    [InlineData("""[]""")]
    public void ParseSkillInstall_DoesNotInventSuccess(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            ClawHubSkillManagementParser.ParseInstall(Json(json)));
    }

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
