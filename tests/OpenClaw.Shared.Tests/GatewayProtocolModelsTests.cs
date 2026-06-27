using System.Linq;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for the gateway protocol DTOs and parsing. Exercises the internal static parse methods
/// directly via crafted JsonElement payloads that mirror the canonical
/// openclaw/openclaw gateway-protocol schemas, plus request payload shaping.
/// </summary>
public class GatewayProtocolModelsTests
{
    [Fact]
    public void NewGatewayProtocolMembers_AreDefaultInterfaceMethods_SoTheyDoNotSourceBreakImplementers()
    {
        // Backwards-compat guard: the members added to IOperatorGatewayClient
        // ship as default interface methods (non-abstract), so existing external
        // implementers / test doubles keep compiling without implementing them.
        // Looked up by exact signature because some names (e.g. PatchSessionAsync)
        // have a pre-existing legacy overload that must remain.
        var iface = typeof(IOperatorGatewayClient);
        var newMembers = new (string Name, System.Type[] Args)[]
        {
            ("ListCommandsAsync", new[] { typeof(CommandCatalogQuery), typeof(int) }),
            ("PatchSessionAsync", new[] { typeof(string), typeof(SessionPatch) }),
            ("ListSessionFilesAsync", new[] { typeof(string), typeof(string), typeof(string), typeof(int) }),
            ("GetSessionFileAsync", new[] { typeof(string), typeof(string), typeof(int) }),
            ("ListCompactionCheckpointsAsync", new[] { typeof(string), typeof(int) }),
            ("GetCompactionCheckpointAsync", new[] { typeof(string), typeof(string), typeof(int) }),
            ("BranchCompactionCheckpointAsync", new[] { typeof(string), typeof(string), typeof(int) }),
            ("RestoreCompactionCheckpointAsync", new[] { typeof(string), typeof(string), typeof(int) }),
        };

        foreach (var (name, args) in newMembers)
        {
            var method = iface.GetMethod(name, args);
            Assert.NotNull(method);
            Assert.False(method!.IsAbstract,
                $"{name} must have a default implementation (non-abstract) to avoid source-breaking IOperatorGatewayClient implementers");
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── commands.list ──

    [Fact]
    public void ParseCommandCatalog_ParsesCommandsArgsAndObjectChoices()
    {
        var payload = Parse("""
        {
          "commands": [
            {
              "name": "model",
              "nativeName": "Model",
              "textAliases": ["/m", "/setmodel"],
              "description": "Change the active model",
              "category": "session",
              "source": "native",
              "scope": "both",
              "acceptsArgs": true,
              "args": [
                {
                  "name": "id",
                  "description": "Model id",
                  "type": "string",
                  "required": true,
                  "dynamic": true,
                  "choices": [
                    { "value": "gpt-5", "label": "GPT-5" },
                    { "value": "claude", "label": "Claude" }
                  ]
                }
              ]
            },
            {
              "name": "help",
              "description": "Show help",
              "category": "status",
              "source": "native",
              "scope": "text",
              "acceptsArgs": false
            }
          ]
        }
        """);

        var catalog = OpenClawGatewayClient.ParseCommandCatalog(payload);

        Assert.True(catalog.IsSupported);
        Assert.Equal(2, catalog.Count);

        var model = catalog.Commands[0];
        Assert.Equal("model", model.Name);
        Assert.Equal("Model", model.NativeName);
        Assert.Equal(new[] { "/m", "/setmodel" }, model.TextAliases);
        Assert.Equal("session", model.Category);
        Assert.Equal("native", model.Source);
        Assert.Equal("both", model.Scope);
        Assert.True(model.AcceptsArgs);

        var arg = Assert.Single(model.Args);
        Assert.Equal("id", arg.Name);
        Assert.Equal("string", arg.Type);
        Assert.True(arg.Required);
        Assert.True(arg.IsDynamic);
        Assert.Equal(2, arg.Choices.Count);
        Assert.Equal("gpt-5", arg.Choices[0].Value);
        Assert.Equal("GPT-5", arg.Choices[0].Label);

        // Facets derived from the commands (gateway returns no facet lists).
        Assert.Equal(new[] { "session", "status" }, catalog.Categories);
        Assert.Equal(new[] { "both", "text" }, catalog.Scopes);
        Assert.Equal(new[] { "native" }, catalog.Sources);
    }

    [Fact]
    public void ParseCommandCatalog_AcceptsTopLevelArray()
    {
        var payload = Parse("""[ { "name": "ping", "description": "", "source": "native", "scope": "text", "acceptsArgs": false } ]""");

        var catalog = OpenClawGatewayClient.ParseCommandCatalog(payload);

        var cmd = Assert.Single(catalog.Commands);
        Assert.Equal("ping", cmd.Name);
        Assert.False(cmd.AcceptsArgs);
    }

    [Fact]
    public void CommandCatalogQuery_Matches_FiltersByCategorySourceScopeAndSearch()
    {
        var cmd = new GatewayCommand
        {
            Name = "model",
            Description = "Change the model",
            Category = "session",
            Source = "native",
            Scope = "both",
            AcceptsArgs = true,
            TextAliases = new[] { "/m" }
        };

        Assert.True(new CommandCatalogQuery { Category = "SESSION" }.Matches(cmd));
        Assert.False(new CommandCatalogQuery { Category = "status" }.Matches(cmd));
        Assert.True(new CommandCatalogQuery { Source = "native", Scope = "both" }.Matches(cmd));
        Assert.True(new CommandCatalogQuery { Search = "change" }.Matches(cmd));
        Assert.True(new CommandCatalogQuery { Search = "/m" }.Matches(cmd)); // alias
        Assert.False(new CommandCatalogQuery { Search = "zzz" }.Matches(cmd));
        Assert.True(new CommandCatalogQuery { AcceptsArgs = true }.Matches(cmd));
        Assert.False(new CommandCatalogQuery { AcceptsArgs = false }.Matches(cmd));
    }

    [Fact]
    public void ApplyCommandQuery_FiltersCatalogAndPreservesFacets()
    {
        var catalog = OpenClawGatewayClient.ParseCommandCatalog(Parse("""
        {
          "commands": [
            { "name": "a", "description": "", "category": "session", "source": "native", "scope": "both", "acceptsArgs": false },
            { "name": "b", "description": "", "category": "status", "source": "native", "scope": "both", "acceptsArgs": false }
          ]
        }
        """));

        var filtered = OpenClawGatewayClient.ApplyCommandQuery(catalog, new CommandCatalogQuery { Category = "session" });

        Assert.Single(filtered.Commands);
        Assert.Equal("a", filtered.Commands[0].Name);
        Assert.Equal(new[] { "session", "status" }, filtered.Categories);
    }

    [Fact]
    public void CommandCatalogQuery_HasFilter_TrueOnlyWhenSet()
    {
        Assert.False(new CommandCatalogQuery().HasFilter);
        Assert.True(new CommandCatalogQuery { Search = "x" }.HasFilter);
        Assert.True(new CommandCatalogQuery { AcceptsArgs = false }.HasFilter);
    }

    // ── sessions.patch payload shape ──

    [Fact]
    public void SessionPatch_ToPayload_EncodesEnumsAndWireNames()
    {
        var patch = new SessionPatch
        {
            Model = "gpt-5",
            ThinkingLevel = "high",
            FastMode = SessionFastMode.Auto,
            ReasoningLevel = "medium",
            ResponseUsage = ResponseUsageMode.Tokens,
            ExecSecurity = "allowlist",
            ExecNode = "worker-1",
            SendPolicy = SessionSendPolicy.Allow,
            GroupActivation = SessionGroupActivation.Mention
        };

        var payload = patch.ToPayload("agent:main");

        Assert.Equal("agent:main", payload["key"]);
        Assert.Equal("gpt-5", payload["model"]);
        Assert.Equal("high", payload["thinkingLevel"]);
        Assert.Equal("auto", payload["fastMode"]);
        Assert.Equal("medium", payload["reasoningLevel"]);
        Assert.Equal("tokens", payload["responseUsage"]);
        Assert.Equal("allowlist", payload["execSecurity"]);
        Assert.Equal("worker-1", payload["execNode"]);
        Assert.Equal("allow", payload["sendPolicy"]);
        Assert.Equal("mention", payload["groupActivation"]);

        // Unset fields must not be present.
        Assert.False(payload.ContainsKey("verboseLevel"));
        Assert.False(payload.ContainsKey("traceLevel"));
        Assert.False(payload.ContainsKey("elevatedLevel"));
        Assert.False(payload.ContainsKey("execHost"));
        Assert.False(payload.ContainsKey("execAsk"));
    }

    [Fact]
    public void SessionPatch_ToPayload_FastModeBooleanAndResponseUsageVariants()
    {
        var on = new SessionPatch { FastMode = SessionFastMode.On, ResponseUsage = ResponseUsageMode.Off }.ToPayload("k");
        Assert.Equal(true, on["fastMode"]);
        Assert.Equal("off", on["responseUsage"]);

        var off = new SessionPatch { FastMode = SessionFastMode.Off, ResponseUsage = ResponseUsageMode.Full }.ToPayload("k");
        Assert.Equal(false, off["fastMode"]);
        Assert.Equal("full", off["responseUsage"]);

        var legacy = new SessionPatch { ResponseUsage = ResponseUsageMode.On, SendPolicy = SessionSendPolicy.Deny, GroupActivation = SessionGroupActivation.Always }.ToPayload("k");
        Assert.Equal("on", legacy["responseUsage"]);
        Assert.Equal("deny", legacy["sendPolicy"]);
        Assert.Equal("always", legacy["groupActivation"]);
    }

    [Fact]
    public void SessionPatch_ToPayload_SerializesFastModeAutoAsString()
    {
        var json = JsonSerializer.Serialize(new SessionPatch { FastMode = SessionFastMode.Auto }.ToPayload("k"));
        Assert.Contains("\"fastMode\":\"auto\"", json);

        var jsonOn = JsonSerializer.Serialize(new SessionPatch { FastMode = SessionFastMode.On }.ToPayload("k"));
        Assert.Contains("\"fastMode\":true", jsonOn);
    }

    [Fact]
    public void CommandCatalogQuery_Scope_IncludesBothSurfaceCommands()
    {
        var textCmd = new GatewayCommand { Name = "t", Scope = "text" };
        var nativeCmd = new GatewayCommand { Name = "n", Scope = "native" };
        var bothCmd = new GatewayCommand { Name = "b", Scope = "both" };

        // Filtering "text" includes text + both; excludes native-only.
        Assert.True(new CommandCatalogQuery { Scope = "text" }.Matches(textCmd));
        Assert.True(new CommandCatalogQuery { Scope = "text" }.Matches(bothCmd));
        Assert.False(new CommandCatalogQuery { Scope = "text" }.Matches(nativeCmd));

        // Filtering "native" includes native + both; excludes text-only.
        Assert.True(new CommandCatalogQuery { Scope = "native" }.Matches(nativeCmd));
        Assert.True(new CommandCatalogQuery { Scope = "native" }.Matches(bothCmd));
        Assert.False(new CommandCatalogQuery { Scope = "native" }.Matches(textCmd));

        // Filtering "both" returns everything.
        Assert.True(new CommandCatalogQuery { Scope = "both" }.Matches(textCmd));
        Assert.True(new CommandCatalogQuery { Scope = "both" }.Matches(nativeCmd));
        Assert.True(new CommandCatalogQuery { Scope = "both" }.Matches(bothCmd));
    }

    [Fact]
    public void SessionPatch_ToPayload_OmitsBlankStringsAndNoChangeKeyOnly()
    {
        // Blank/whitespace NonEmptyString fields must not be sent (strict gateway rejects "").
        var patch = new SessionPatch { Model = "  ", ExecHost = "", ThinkingLevel = "high" };
        var payload = patch.ToPayload("k");

        Assert.False(payload.ContainsKey("model"));
        Assert.False(payload.ContainsKey("execHost"));
        Assert.Equal("high", payload["thinkingLevel"]);

        // A patch whose only string fields are blank reports no changes.
        Assert.False(new SessionPatch { Model = "   " }.HasChanges);
    }

    [Fact]
    public void SessionPatch_HasChanges_ReflectsSetState()
    {
        Assert.False(new SessionPatch().HasChanges);
        Assert.True(new SessionPatch { Model = "x" }.HasChanges);
        Assert.True(new SessionPatch { GroupActivation = SessionGroupActivation.Always }.HasChanges);
        // A clear-only patch is a change (it removes an override).
        Assert.True(new SessionPatch { Model = SessionPatch.Clear }.HasChanges);
        Assert.True(new SessionPatch { FastMode = SessionPatch.Clear }.HasChanges);
    }

    [Fact]
    public void SessionPatch_ToPayload_ClearEmitsExplicitNullForStringAndEnumFields()
    {
        var patch = new SessionPatch
        {
            Model = SessionPatch.Clear,
            ExecNode = SessionPatch.Clear,
            FastMode = SessionPatch.Clear,
            ResponseUsage = SessionPatch.Clear,
            SendPolicy = SessionPatch.Clear,
            GroupActivation = SessionPatch.Clear
        };

        var payload = patch.ToPayload("agent:main");

        // Cleared fields are present with an explicit null value (not omitted).
        foreach (var name in new[] { "model", "execNode", "fastMode", "responseUsage", "sendPolicy", "groupActivation" })
        {
            Assert.True(payload.ContainsKey(name), $"expected '{name}' to be present");
            Assert.Null(payload[name]);
        }

        // Untouched fields stay omitted.
        Assert.False(payload.ContainsKey("thinkingLevel"));
        Assert.False(payload.ContainsKey("execHost"));
    }

    [Fact]
    public void SessionPatch_ToPayload_ClearSerializesToJsonNull()
    {
        var json = JsonSerializer.Serialize(new SessionPatch { Model = SessionPatch.Clear }.ToPayload("k"));
        Assert.Contains("\"model\":null", json);

        var fastJson = JsonSerializer.Serialize(new SessionPatch { FastMode = SessionPatch.Clear }.ToPayload("k"));
        Assert.Contains("\"fastMode\":null", fastJson);
    }

    [Fact]
    public void SessionPatch_ToPayload_MixesSetAndClearAndUnset()
    {
        // model set to value, execHost cleared, thinkingLevel left unset.
        var payload = new SessionPatch
        {
            Model = "gpt-5",
            ExecHost = SessionPatch.Clear
        }.ToPayload("k");

        Assert.Equal("gpt-5", payload["model"]);
        Assert.True(payload.ContainsKey("execHost"));
        Assert.Null(payload["execHost"]);
        Assert.False(payload.ContainsKey("thinkingLevel"));
    }

    [Fact]
    public void PatchField_TriStateFlags()
    {
        PatchField<string> unset = default;
        Assert.False(unset.IsSpecified);
        Assert.False(unset.IsClear);
        Assert.False(unset.HasValue);

        PatchField<string> set = "v";
        Assert.True(set.IsSpecified);
        Assert.False(set.IsClear);
        Assert.True(set.HasValue);
        Assert.Equal("v", set.Value);

        PatchField<string> cleared = SessionPatch.Clear;
        Assert.True(cleared.IsSpecified);
        Assert.True(cleared.IsClear);
        Assert.False(cleared.HasValue);

        // A null reference assigned via implicit conversion is unset, not a clear.
        PatchField<string> fromNull = (string?)null;
        Assert.False(fromNull.IsSpecified);
        Assert.False(fromNull.IsClear);
    }

    [Fact]
    public void SessionPatch_ToPayload_EmitsOnlyKnownGatewaySchemaFields()
    {
        // Contract guard: every wire name must exist in the upstream
        // SessionsPatchParamsSchema (additionalProperties:false). A single typo
        // would cause the gateway to reject the whole patch, so pin the exact
        // key set here. Set every modelled field so the full surface is covered.
        var patch = new SessionPatch
        {
            Model = "m",
            ThinkingLevel = "t",
            FastMode = SessionFastMode.Auto,
            VerboseLevel = "v",
            TraceLevel = "tr",
            ReasoningLevel = "r",
            ResponseUsage = ResponseUsageMode.Full,
            ElevatedLevel = "e",
            ExecHost = "auto",
            ExecSecurity = "allowlist",
            ExecAsk = "on-miss",
            ExecNode = "n",
            SendPolicy = SessionSendPolicy.Allow,
            GroupActivation = SessionGroupActivation.Mention
        };

        var keys = patch.ToPayload("agent:main").Keys.OrderBy(k => k).ToArray();

        var expected = new[]
        {
            "elevatedLevel", "execAsk", "execHost", "execNode", "execSecurity",
            "fastMode", "groupActivation", "key", "model", "reasoningLevel",
            "responseUsage", "sendPolicy", "thinkingLevel", "traceLevel", "verboseLevel"
        }.OrderBy(k => k).ToArray();

        Assert.Equal(expected, keys);
    }

    // ── sessions.files.list / get ──

    [Fact]
    public void ParseSessionFileList_ParsesFilesAndBrowser()
    {
        var payload = Parse("""
        {
          "sessionKey": "agent:main:main",
          "root": "/work/repo",
          "files": [
            { "path": "src/main.cs", "name": "main.cs", "kind": "modified", "missing": false, "size": 1234, "updatedAtMs": 1700000000000 },
            { "path": "old.txt", "name": "old.txt", "kind": "read", "missing": true }
          ],
          "browser": {
            "path": "src",
            "parentPath": "",
            "entries": [
              { "path": "src/sub", "name": "sub", "kind": "directory", "sessionKind": "mixed" },
              { "path": "src/main.cs", "name": "main.cs", "kind": "file", "size": 1234, "updatedAtMs": 1700000000000 }
            ],
            "truncated": false
          }
        }
        """);

        var list = OpenClawGatewayClient.ParseSessionFileList(payload, "agent:main:main");

        Assert.True(list.IsSupported);
        Assert.Equal("agent:main:main", list.Key);
        Assert.Equal("/work/repo", list.Root);
        Assert.Equal(2, list.Files.Count);

        var file = list.Files[0];
        Assert.Equal("src/main.cs", file.Path);
        Assert.Equal("main.cs", file.Name);
        Assert.Equal("modified", file.Kind);
        Assert.False(file.Missing);
        Assert.Equal(1234, file.Size);
        Assert.NotNull(file.UpdatedAt);

        Assert.True(list.Files[1].Missing);

        Assert.NotNull(list.Browser);
        Assert.Equal("src", list.Browser!.Path);
        Assert.Equal(2, list.Browser.Entries.Count);
        Assert.True(list.Browser.Entries[0].IsDirectory);
        Assert.Equal("mixed", list.Browser.Entries[0].SessionKind);
        Assert.False(list.Browser.Entries[1].IsDirectory);
    }

    [Fact]
    public void ParseSessionFileContent_ParsesNestedFile()
    {
        var payload = Parse("""
        {
          "sessionKey": "agent:main:main",
          "root": "/work/repo",
          "file": {
            "path": "notes.txt",
            "name": "notes.txt",
            "kind": "modified",
            "missing": false,
            "size": 11,
            "updatedAtMs": 1700000000000,
            "content": "hello world"
          }
        }
        """);

        var content = OpenClawGatewayClient.ParseSessionFileContent(payload, "agent:main:main", "notes.txt");

        Assert.True(content.IsSupported);
        Assert.True(content.Found);
        Assert.False(content.Missing);
        Assert.Equal("agent:main:main", content.Key);
        Assert.Equal("/work/repo", content.Root);
        Assert.Equal("notes.txt", content.Path);
        Assert.Equal("hello world", content.Content);
        Assert.Equal("modified", content.Kind);
        Assert.Equal(11, content.Size);
        Assert.NotNull(content.UpdatedAt);
    }

    [Fact]
    public void ParseSessionFileContent_NoFileMeansMissing()
    {
        var payload = Parse("""{ "sessionKey": "agent:main:main" }""");

        var content = OpenClawGatewayClient.ParseSessionFileContent(payload, "agent:main:main", "missing.txt");

        Assert.True(content.Missing);
        Assert.False(content.Found);
        Assert.Null(content.Content);
        Assert.Equal("missing.txt", content.Path);
    }

    // ── sessions.compaction.* ──

    [Fact]
    public void ParseCompactionCheckpointList_ParsesCheckpoints()
    {
        var payload = Parse("""
        {
          "ok": true,
          "key": "agent:main:main",
          "checkpoints": [
            { "checkpointId": "cp1", "sessionKey": "agent:main:main", "sessionId": "sid-1", "createdAt": 1700000000000, "reason": "auto-threshold", "tokensBefore": 9000, "tokensAfter": 1200, "summary": "snapshot" },
            { "checkpointId": "cp2", "sessionKey": "agent:main:main", "sessionId": "sid-2", "createdAt": 1700000001000, "reason": "manual" }
          ]
        }
        """);

        var list = OpenClawGatewayClient.ParseCompactionCheckpointList(payload, "agent:main:main");

        Assert.True(list.IsSupported);
        Assert.Equal("agent:main:main", list.Key);
        Assert.Equal(2, list.Checkpoints.Count);

        var cp = list.Checkpoints[0];
        Assert.Equal("cp1", cp.Id);
        Assert.Equal("sid-1", cp.SessionId);
        Assert.Equal("auto-threshold", cp.Reason);
        Assert.Equal(9000, cp.TokensBefore);
        Assert.Equal(1200, cp.TokensAfter);
        Assert.Equal("snapshot", cp.Summary);
        Assert.NotNull(cp.CreatedAt);
    }

    [Fact]
    public void ParseCompactionCheckpointList_PreservesUntargetableCheckpointsForRestoreSafety()
    {
        var payload = Parse("""
        {
          "ok": true,
          "key": "agent:main:main",
          "checkpoints": [
            { "createdAt": 1700000001000, "reason": "missing-id" },
            { "checkpointId": "cp1", "createdAt": 1700000000000, "reason": "manual" }
          ]
        }
        """);

        var list = OpenClawGatewayClient.ParseCompactionCheckpointList(payload, "agent:main:main");

        Assert.Equal(2, list.Checkpoints.Count);
        Assert.Equal("", list.Checkpoints[0].Id);
        Assert.Equal("missing-id", list.Checkpoints[0].Reason);
        Assert.Equal("cp1", list.Checkpoints[1].Id);
        Assert.Null(SessionCheckpointSelection.ResolveUnambiguousLatest(list.Checkpoints));
    }

    [Fact]
    public void ParseCompactionCheckpointResult_ParsesSingleCheckpoint()
    {
        var payload = Parse("""
        {
          "ok": true,
          "key": "agent:main:main",
          "checkpoint": { "checkpointId": "cp1", "sessionId": "sid-1", "reason": "manual" }
        }
        """);

        var result = OpenClawGatewayClient.ParseCompactionCheckpointResult(payload, "agent:main:main");

        Assert.True(result.IsSupported);
        Assert.True(result.Found);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal("cp1", result.Checkpoint!.Id);
        Assert.Equal("manual", result.Checkpoint.Reason);
    }

    [Fact]
    public void ParseCompactionMutation_BranchExtractsSourceAndNewKey()
    {
        var payload = Parse("""
        {
          "ok": true,
          "sourceKey": "agent:main:main",
          "key": "agent:main:branch-1",
          "sessionId": "sid-branch",
          "checkpoint": { "checkpointId": "cp1" }
        }
        """);

        var result = OpenClawGatewayClient.ParseCompactionMutation(payload, "agent:main:main", "cp1");

        Assert.True(result.Ok);
        Assert.True(result.IsSupported);
        Assert.Equal("agent:main:main", result.Key);
        Assert.Equal("cp1", result.CheckpointId);
        Assert.Equal("agent:main:main", result.SourceKey);
        Assert.Equal("agent:main:branch-1", result.ResultSessionKey);
        Assert.Equal("sid-branch", result.SessionId);
        Assert.NotNull(result.Checkpoint);
    }

    [Fact]
    public void ParseCompactionMutation_RestoreUsesKeyAsResult()
    {
        var payload = Parse("""
        {
          "ok": true,
          "key": "agent:main:main",
          "sessionId": "sid-restored",
          "checkpoint": { "checkpointId": "cp1" }
        }
        """);

        var result = OpenClawGatewayClient.ParseCompactionMutation(payload, "agent:main:main", "cp1");

        Assert.True(result.Ok);
        Assert.Null(result.SourceKey);
        Assert.Equal("agent:main:main", result.ResultSessionKey);
        Assert.Equal("sid-restored", result.SessionId);
    }
}
