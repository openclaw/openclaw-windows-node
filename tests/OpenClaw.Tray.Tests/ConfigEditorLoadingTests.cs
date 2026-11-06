using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ConfigEditorLoadingTests
{
    [Theory]
    [InlineData("parsed", "hash")]
    [InlineData("config", "baseHash")]
    public async Task CompleteReadPair_PreservesExistingConfigAndMaskedValues(string rootField, string hashField)
    {
        using var existing = JsonDocument.Parse("""
            {"gateway":{"port":18789},"integration":{"credentials":"__OPENCLAW_REDACTED__"},
             "plugins":{"entries":{"example":{"enabled":true}}}}
            """);
        var response = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [rootField] = existing.RootElement,
            [hashField] = "stable-base-hash",
            ["valid"] = true
        });
        var schema = Schema();
        var pair = await ConfigEditorLoader.LoadAsync((method, _) =>
            Task.FromResult(method == "config.get" ? response : schema));
        Assert.True(pair.Succeeded);
        var snapshot = ConfigEditorModel.CaptureSnapshot(pair.Config!.Value);
        Assert.True(JsonElement.DeepEquals(existing.RootElement, snapshot.Root));
        Assert.Equal("stable-base-hash", snapshot.BaseHash);
        Assert.Equal(schema.GetRawText(), pair.Schema!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingObjectBranch_AllowsSchemaOnlyDescendantNavigation(bool undefined)
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"integration":{"type":"object","properties":{
              "credentials":{"type":"object","properties":{"account":{"type":"object","properties":{"id":{"type":"string"}}}}}
            }}}}
            """);
        using var config = JsonDocument.Parse("{}");
        var rootConfig = undefined ? default(JsonElement) : config.RootElement;
        var integration = Assert.Single(ConfigEditorModel.ReadSchemaSections(schema.RootElement, rootConfig));
        Assert.Null(integration.Config);
        var credentials = Assert.Single(ConfigEditorModel.ReadSchemaSections(integration.Schema, integration.Config));
        Assert.Null(credentials.Config);
        var account = Assert.Single(ConfigEditorModel.ReadSchemaSections(credentials.Schema, credentials.Config));
        Assert.Equal("account", account.Name);
        Assert.Null(account.Config);
    }

    [Theory]
    [InlineData("config.get")]
    [InlineData("config.schema")]
    public async Task FailedReply_StillWaitsForTheOtherReadBeforeCompleting(string failedMethod)
    {
        var remaining = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var load = ConfigEditorLoader.LoadAsync((method, _) =>
        {
            calls.Add(method);
            return method == failedMethod
                ? Task.FromException<JsonElement>(new InvalidOperationException("remote failure"))
                : remaining.Task;
        });
        Assert.Equal(new[] { "config.get", "config.schema" }, calls);
        Assert.False(load.IsCompleted);
        remaining.SetResult(failedMethod == "config.get" ? Schema() : Config());
        var result = await load;
        Assert.False(result.Succeeded);
        Assert.Equal(failedMethod != "config.get", result.Config.HasValue);
        Assert.Equal(failedMethod != "config.schema", result.Schema.HasValue);
    }

    [Theory]
    [InlineData(false, 7, 4, true)]
    [InlineData(true, 7, 4, false)]
    [InlineData(false, 8, 4, false)]
    [InlineData(false, 7, 5, false)]
    public void DraftSnapshot_RequiresLiveClientEpochAndProfile(bool replaced, long epoch, int revision, bool expected)
    {
        var client = new object();
        var snapshot = new ConfigEditorLoadTicket(2, client, 7, 4);
        Assert.Equal(expected, snapshot.MatchesConnection(replaced ? new object() : client, epoch, revision));
    }

    [Fact]
    public void DraftEditingAndSave_CheckLiveConnectionBeforeUsingRetainedDraft()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConfigPage.xaml.cs"));
        var save = source[source.IndexOf("private async Task OnSaveAsync()", StringComparison.Ordinal)..];
        Assert.Contains("!SnapshotMatchesCurrentConnection()", save, StringComparison.Ordinal);
        Assert.Contains("_pendingChanges.Count", save, StringComparison.Ordinal);
        Assert.True(save.IndexOf("!SnapshotMatchesCurrentConnection()", StringComparison.Ordinal) <
                    save.IndexOf("_pendingChanges.Count", StringComparison.Ordinal));
        var locked = source[source.IndexOf("private bool IsConfigEditingLocked", StringComparison.Ordinal)..];
        locked = locked[..locked.IndexOf(';')];
        Assert.Contains("!SnapshotMatchesCurrentConnection()", locked, StringComparison.Ordinal);
        Assert.Contains("SaveButton.IsEnabled = !editingLocked &&", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskedObjectSection_RemainsMaskedWithoutTraversingItsChildren()
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"integration":{"type":"object","properties":{
              "credentials":{"type":"object","properties":{"account":{"type":"object","properties":{"id":{"type":"string"}}}}}
            }}}}
            """);
        using var config = JsonDocument.Parse("""
            {"integration":{"credentials":"__OPENCLAW_REDACTED__"}}
            """);
        var integration = Assert.Single(ConfigEditorModel.ReadSchemaSections(schema.RootElement, config.RootElement));
        var credentials = Assert.Single(ConfigEditorModel.ReadSchemaSections(integration.Schema, integration.Config));
        Assert.Equal("credentials", credentials.Name);
        Assert.Equal("__OPENCLAW_REDACTED__", credentials.Config!.Value.GetString());
        Assert.Empty(ConfigEditorModel.ReadSchemaSections(credentials.Schema, credentials.Config));
        Assert.False(ConfigEditorModel.CanTraverseObjectConfig(credentials.Config.Value));
        Assert.True(ConfigEditorModel.CanTraverseObjectConfig(integration.Config!.Value));
        Assert.True(ConfigEditorModel.HasSchemaFields(integration.Schema));
        Assert.Equal("__OPENCLAW_REDACTED__", config.RootElement.GetProperty("integration").GetProperty("credentials").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("42")]
    public void NonObjectConfigBranch_DoesNotBreakOtherSchemaSections(string value)
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"first":{"type":"object","properties":{"nested":{"type":"object"}}},
              "second":{"type":"object","properties":{"enabled":{"type":"boolean"}}}}}
            """);
        using var config = JsonDocument.Parse("{\"first\":" + value + ",\"second\":{\"enabled\":true}}");
        var sections = ConfigEditorModel.ReadSchemaSections(schema.RootElement, config.RootElement).ToArray();
        Assert.Equal(new[] { "first", "second" }, sections.Select(section => section.Name));
        Assert.Empty(ConfigEditorModel.ReadSchemaSections(sections[0].Schema, sections[0].Config));
        Assert.True(sections[1].Config!.Value.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void MissingConfigSection_StillHasSchemaNavigation()
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"gateway":{"type":"object","properties":{"reload":{"type":"object"}}}}}
            """);
        var gateway = Assert.Single(ConfigEditorModel.ReadSchemaSections(schema.RootElement, null));
        Assert.Null(gateway.Config);
        Assert.Equal("reload", Assert.Single(ConfigEditorModel.ReadSchemaSections(gateway.Schema, gateway.Config)).Name);
    }

    [Fact]
    public async Task Refresh_WaitsForBothRepliesBeforePublishingThePair()
    {
        var config = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var schema = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var load = ConfigEditorLoader.LoadAsync((method, timeout) =>
        {
            Assert.True(timeout > 0);
            calls.Add(method);
            return method == "config.get" ? config.Task : schema.Task;
        });
        Assert.Equal(new[] { "config.get", "config.schema" }, calls);
        config.SetResult(Config());
        Assert.False(load.IsCompleted);
        schema.SetResult(Schema());
        var result = await load;
        Assert.True(result.Succeeded);
        Assert.Equal("revision-one", result.Config!.Value.GetProperty("hash").GetString());
    }

    [Theory]
    [InlineData("config.get", false)]
    [InlineData("config.schema", false)]
    [InlineData("config.get", true)]
    [InlineData("config.schema", true)]
    public async Task RejectedOrTimedOutRefresh_IsObservedAndTheNextRefreshCanRecover(string failedMethod, bool timeout)
    {
        var failed = await ConfigEditorLoader.LoadAsync((method, _) => method == failedMethod
            ? Task.FromException<JsonElement>(timeout ? new TimeoutException() : new InvalidOperationException("synthetic rejected request"))
            : Task.FromResult(method == "config.get" ? Config() : Schema()));
        Assert.False(failed.Succeeded);
        Assert.Equal(failedMethod == "config.get", failed.Config is null);
        Assert.Equal(failedMethod == "config.schema", failed.Schema is null);
        var recovered = await ConfigEditorLoader.LoadAsync((method, _) =>
            Task.FromResult(method == "config.get" ? Config() : Schema()));
        Assert.True(recovered.Succeeded);
    }

    [Theory]
    [InlineData("config.get", "null")]
    [InlineData("config.get", """{"valid":false,"parsed":null,"config":{}}""")]
    [InlineData("config.get", """{"parsed":[]}""")]
    [InlineData("config.schema", """{"schema":null}""")]
    [InlineData("config.schema", """{"schema":{}}""")]
    public async Task InvalidResponse_DoesNotBecomeAnEditableEmptyDocument(string invalidMethod, string response)
    {
        using var document = JsonDocument.Parse(response);
        var result = await ConfigEditorLoader.LoadAsync((method, _) => Task.FromResult(method == invalidMethod
            ? document.RootElement : method == "config.get" ? Config() : Schema()));
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(true, 2, false, 7, true, true)]
    [InlineData(false, 2, false, 7, true, false)]
    [InlineData(true, 3, false, 7, true, false)]
    [InlineData(true, 2, true, 7, true, false)]
    [InlineData(true, 2, false, 8, true, false)]
    [InlineData(true, 2, false, 7, false, false)]
    public void DelayedLoad_OnlyPublishesToItsStillObservedAuthenticatedConnection(
        bool observing, int generation, bool replaced, long epoch, bool ready, bool expected)
    {
        var client = new object();
        var ticket = new ConfigEditorLoadTicket(2, client, 7, 4);
        Assert.Equal(expected, ticket.IsCurrent(observing, generation, replaced ? new object() : client, epoch, 4, ready));
    }

    [Fact]
    public void CachedSnapshot_RejectsProfileChangeThatOccurredWhilePageWasUnloaded()
    {
        var client = new object();
        var profileRevision = 4;
        var snapshot = new ConfigEditorLoadTicket(2, client, 7, profileRevision);
        Assert.True(snapshot.MatchesConnection(client, 7, profileRevision));
        // GatewayService advances the shared revision even without a page subscription.
        profileRevision++;
        Assert.False(snapshot.MatchesConnection(client, 7, profileRevision));
        Assert.False(snapshot.IsCurrent(true, 2, client, 7, profileRevision, true));
        var current = new ConfigEditorLoadTicket(3, client, 7, profileRevision);
        Assert.True(current.MatchesConnection(client, 7, profileRevision));
    }

    [Fact]
    public void AncestorForms_KeepTheOpaqueObjectBoundaryBeforeBuildingControls()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Controls", "SchemaConfigEditor.xaml.cs"));
        Assert.Contains("!ConfigEditorModel.HasSchemaFields(schema)", source, StringComparison.Ordinal);
        // WinUI-only guard. Retire when mounted ancestor-form coverage owns this boundary.
        foreach (var signature in new[] { "private void RenderSchemaNode(", "private void RenderObjectSection(" })
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var body = source.IndexOf('{', start);
            var guard = source.IndexOf("if (!ConfigEditorModel.CanTraverseObjectConfig(config))", body, StringComparison.Ordinal);
            Assert.True(guard > body);
            var next = source.IndexOf("\n    private ", body, StringComparison.Ordinal);
            Assert.True(next < 0 || guard < next);
            var firstBlock = source[(body + 1)..source.IndexOf(';', body)];
            Assert.Contains("CanTraverseObjectConfig(config)", firstBlock, StringComparison.Ordinal);
            Assert.Contains("return", firstBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnloadedPage_DefersSaveDrivenRefreshUntilInitialization()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConfigPage.xaml.cs"));
        var start = source.IndexOf("private void RefreshFromGateway()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task LoadConfigAsync(", start, StringComparison.Ordinal);
        var refresh = source[start..end];
        var gate = refresh.IndexOf("if (!_observing)", StringComparison.Ordinal);
        var defer = refresh.IndexOf("_refreshConfigOnNextInitialize = true;", StringComparison.Ordinal);
        var request = refresh.IndexOf("LoadConfigAsync(client, ticket)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && defer > gate && request > defer);
        Assert.Contains("return;", refresh[defer..request], StringComparison.Ordinal);
        var initialize = source[source.IndexOf("public void Initialize()", StringComparison.Ordinal)..
            source.IndexOf("private bool ApplyLoadedConfig(", StringComparison.Ordinal)];
        Assert.Contains("if (_refreshConfigOnNextInitialize ||", initialize, StringComparison.Ordinal);
        Assert.Contains("_refreshConfigOnNextInitialize = false;", initialize, StringComparison.Ordinal);
    }

    private static JsonElement Config() => JsonSerializer.SerializeToElement(new
    {
        parsed = new { gateway = new { mode = "local" } }, hash = "revision-one", valid = true
    });

    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new
    {
        schema = new { type = "object", properties = new { gateway = new { type = "object" } } }
    });
}
