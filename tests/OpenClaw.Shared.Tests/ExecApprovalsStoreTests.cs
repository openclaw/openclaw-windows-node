using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Tests for PR4: ExecApprovalsStore read path.
// Coverage: deserialization, normalization, cascade resolution, malformed/version guards,
// default-deny semantics, and ensureFile behavior.
public class ExecApprovalsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly CapturingLogger _log;

    public ExecApprovalsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"oca-store-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new CapturingLogger();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ExecApprovalsStore Store() => new(_dir, _log);

    private string FilePath => Path.Combine(_dir, "exec-approvals.json");

    private void WriteFile(string json) => File.WriteAllText(FilePath, json);

    // ── Default-deny when file absent ────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_NoFile_ReturnsDefaultDeny()
    {
        var resolved = Store().ResolveReadOnly(null);

        Assert.Equal("main", resolved.AgentId);
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
        Assert.Equal(ExecAsk.Deny, resolved.Defaults.AskFallback);
        Assert.False(resolved.Defaults.AutoAllowSkills);
        Assert.Empty(resolved.Allowlist);
        Assert.Null(resolved.SocketToken);
    }

    [Fact]
    public void ResolveReadOnly_NullAgentId_NormalizesToMain()
    {
        WriteFile(MinimalFile());
        var resolved = Store().ResolveReadOnly(null);
        Assert.Equal("main", resolved.AgentId);
    }

    [Fact]
    public void ResolveReadOnly_EmptyAgentId_NormalizesToMain()
    {
        WriteFile(MinimalFile());
        var resolved = Store().ResolveReadOnly("  ");
        Assert.Equal("main", resolved.AgentId);
    }

    // ── Malformed JSON → default-deny + warning ───────────────────────────────

    [Fact]
    public void ResolveReadOnly_MalformedJson_ReturnsDefaultDenyAndWarns()
    {
        WriteFile("{ not valid json }");
        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, w => w.Contains("malformed"));
    }

    // ── Unsupported version → default-deny + warning ─────────────────────────

    [Fact]
    public void ResolveReadOnly_Version2_ReturnsDefaultDenyAndWarns()
    {
        WriteFile("""{"version":2,"agents":{}}""");
        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, w => w.Contains("unsupported version"));
    }

    [Fact]
    public void ResolveReadOnly_Version0_ReturnsDefaultDenyAndWarns()
    {
        WriteFile("""{"version":0,"agents":{}}""");
        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, w => w.Contains("unsupported version"));
    }

    [Fact]
    public void ResolveReadOnly_MissingVersion_ReturnsDefaultDenyAndWarns()
    {
        WriteFile("""
        {
          "defaults": { "security": "full" },
          "agents": {}
        }
        """);
        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, w => w.Contains("unsupported version missing"));
    }

    // ── Deserialization: enum values ──────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_EnumValues_DeserializedCorrectly()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "allowlist", "ask": "on-miss", "askFallback": "deny" },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
        Assert.Equal(ExecAsk.Deny, resolved.Defaults.AskFallback);
    }

    [Fact]
    public void ResolveReadOnly_FullSecurityAndAlwaysAsk_DeserializedCorrectly()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "full", "ask": "always" },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
    }

    // ── Cascade resolution ────────────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_AgentOverridesDefault()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "deny" },
          "agents": {
            "main": { "security": "allowlist" }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
    }

    [Fact]
    public void ResolveReadOnly_WildcardFillsGapsWhenNoAgentEntry()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "*": { "security": "full", "ask": "always" }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("unknown-agent");
        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
    }

    [Fact]
    public void ResolveReadOnly_AgentWinsOverWildcard()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "*":    { "security": "full" },
            "main": { "security": "deny" }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
    }

    [Fact]
    public void ResolveReadOnly_CascadeOrder_AgentWildcardDefaultsSystem()
    {
        // Only system defaults apply — nothing in file overrides.
        WriteFile("""{"version":1,"agents":{}}""");

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
        Assert.Equal(ExecAsk.Deny, resolved.Defaults.AskFallback);
        Assert.False(resolved.Defaults.AutoAllowSkills);
    }

    // ── Allowlist resolution ──────────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_AllowlistFromAgent_Returned()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "security": "allowlist",
              "allowlist": [
                { "id": "11111111-0000-0000-0000-000000000000", "pattern": "/usr/bin/git" }
              ]
            }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Single(resolved.Allowlist);
        Assert.Equal("/usr/bin/git", resolved.Allowlist[0].Pattern);
    }

    [Fact]
    public void ResolveReadOnly_AllowlistWildcardPlusAgent_Concatenated()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "*":    { "allowlist": [{ "pattern": "/usr/bin/rg" }] },
            "main": { "allowlist": [{ "pattern": "/usr/bin/git" }] }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(2, resolved.Allowlist.Count);
        // wildcard first
        Assert.Equal("/usr/bin/rg", resolved.Allowlist[0].Pattern);
        Assert.Equal("/usr/bin/git", resolved.Allowlist[1].Pattern);
    }

    [Fact]
    public void ResolveReadOnly_AllowlistDeduplicatedCaseInsensitive()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "*":    { "allowlist": [{ "pattern": "/usr/bin/git" }] },
            "main": { "allowlist": [{ "pattern": "/USR/BIN/GIT" }] }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
    }

    [Fact]
    public void ResolveReadOnly_AllowlistEmptyPatternDropped()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [
                { "pattern": "" },
                { "pattern": "/usr/bin/git" }
              ]
            }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.Equal("/usr/bin/git", resolved.Allowlist[0].Pattern);
    }

    // ── Normalization: default→main migration ─────────────────────────────────

    [Fact]
    public void Normalize_DefaultAgentMigratedToMain()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "default": { "security": "allowlist" }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
    }

    [Fact]
    public void Normalize_MainWinsOverDefaultOnConflict()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "default": { "security": "full" },
            "main":    { "security": "deny" }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
    }

    [Fact]
    public void Normalize_DefaultAllowlistMergedIntoMain()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "default": { "allowlist": [{ "pattern": "/usr/bin/rg" }] },
            "main":    { "allowlist": [{ "pattern": "/usr/bin/git" }] }
          }
        }
        """);

        // After normalization "default" is gone; "main" has both entries (default first).
        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal(2, resolved.Allowlist.Count);
        Assert.Equal("/usr/bin/rg", resolved.Allowlist[0].Pattern);
        Assert.Equal("/usr/bin/git", resolved.Allowlist[1].Pattern);
    }

    // ── Normalization: socket ─────────────────────────────────────────────────

    [Fact]
    public void Normalize_SocketTokenPreserved()
    {
        WriteFile("""
        {
          "version": 1,
          "socket": { "token": "abc123" },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal("abc123", resolved.SocketToken);
    }

    [Fact]
    public void Normalize_EmptySocketTokenNulled()
    {
        WriteFile("""
        {
          "version": 1,
          "socket": { "token": "   " },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Null(resolved.SocketToken);
    }

    // ── lastUsedAt as double? ─────────────────────────────────────────────────

    [Fact]
    public void Deserialize_LastUsedAt_AsDouble()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [
                { "pattern": "/usr/bin/git", "lastUsedAt": 1714000000000.0 }
              ]
            }
          }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.Equal(1714000000000.0, resolved.Allowlist[0].LastUsedAt);
    }

    // ── EnsureFile (ResolveAsync) ─────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoFile_CreatesFileAndReturnsDefaultDeny()
    {
        var resolved = await Store().ResolveAsync(null);

        Assert.True(File.Exists(FilePath));
        Assert.Equal("main", resolved.AgentId);
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Infos, i => i.Contains("Created"));
    }

    [Fact]
    public async Task ResolveAsync_MalformedFile_PreservesFileAndReturnsDefaultDeny()
    {
        WriteFile("{ bad json }");
        var resolved = await Store().ResolveAsync(null);

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal("{ bad json }", File.ReadAllText(FilePath));
        Assert.Contains(_log.Warnings, w => w.Contains("Preserving unreadable"));
        Assert.DoesNotContain(_log.Infos, i => i.Contains("Created"));
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedVersion_PreservesFileAndReturnsDefaultDeny()
    {
        WriteFile("""
        {
          "version": 2,
          "defaults": { "security": "full" },
          "agents": {}
        }
        """);
        var original = File.ReadAllText(FilePath);

        var resolved = await Store().ResolveAsync(null);

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Contains(_log.Warnings, w => w.Contains("unsupported version"));
        Assert.Contains(_log.Warnings, w => w.Contains("Preserving unreadable"));
    }

    [Fact]
    public async Task ResolveAsync_ExistingFile_DoesNotRecreate()
    {
        WriteFile(MinimalFileWithAgent("main", "allowlist"));
        var store = Store();

        var resolved = await store.ResolveAsync("main");

        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        // No "Created" log — file was not recreated.
        Assert.DoesNotContain(_log.Infos, i => i.Contains("Created"));
    }

    [Fact]
    public async Task ResolveAsync_NullAgentsField_InitializesAgentsAndSaves()
    {
        // File exists but agents is null — ensureFile should add agents:{} and save.
        WriteFile("""{"version":1}""");
        await Store().ResolveAsync(null);

        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"agents\"", json);
    }

    // ── Atomic write ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_AtomicWrite_NoTempFileLeft()
    {
        await Store().ResolveAsync(null);

        var temps = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(temps);
    }

    // ── AutoAllowSkills ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_AutoAllowSkills_True_WhenSetInAgent()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": { "main": { "autoAllowSkills": true } }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.True(resolved.Defaults.AutoAllowSkills);
    }

    // ── Serialization round-trip ──────────────────────────────────────────────

    [Fact]
    public void JsonOptions_SerializesEnumValues_MatchMacOS()
    {
        var file = new ExecApprovalsFile
        {
            Version = 1,
            Defaults = new ExecApprovalsDefaults
            {
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecAsk.Off,
                AutoAllowSkills = false,
            },
            Agents = [],
        };

        var json = JsonSerializer.Serialize(file, ExecApprovalsStore.JsonOptions);

        Assert.Contains("\"allowlist\"", json);
        Assert.Contains("\"on-miss\"", json);
        Assert.Contains("\"off\"", json);
    }

    [Fact]
    public void JsonOptions_SerializesDenyAndFull_MatchMacOS()
    {
        var defaults = new ExecApprovalsDefaults
        {
            Security = ExecSecurity.Deny,
            Ask = ExecAsk.Always,
        };

        var json = JsonSerializer.Serialize(defaults, ExecApprovalsStore.JsonOptions);

        Assert.Contains("\"deny\"", json);
        Assert.Contains("\"always\"", json);
    }

    // ── No side-effects contract ──────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_NoFile_DoesNotCreateFile()
    {
        Store().ResolveReadOnly(null);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void ResolveReadOnly_MalformedFile_DoesNotOverwriteFile()
    {
        WriteFile("{ bad }");
        Store().ResolveReadOnly(null);
        Assert.Equal("{ bad }", File.ReadAllText(FilePath));
    }

    // ── Cascade: defaults level ───────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_DefaultsLevel_UsedWhenNoAgentOrWildcard()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "full", "ask": "always", "askFallback": "on-miss", "autoAllowSkills": true },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.AskFallback);
        Assert.True(resolved.Defaults.AutoAllowSkills);
    }

    [Fact]
    public void ResolveReadOnly_AgentWinsOverDefaultsLevel()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "full", "ask": "always" },
          "agents": { "main": { "security": "deny", "ask": "off" } }
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Off, resolved.Defaults.Ask);
    }

    [Fact]
    public void ResolveReadOnly_WildcardWinsOverDefaultsLevel()
    {
        WriteFile("""
        {
          "version": 1,
          "defaults": { "security": "full" },
          "agents": { "*": { "security": "deny" } }
        }
        """);

        var resolved = Store().ResolveReadOnly("unknown");
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
    }

    // ── Cascade: wildcard covers Ask/AskFallback/AutoAllowSkills ─────────────

    [Fact]
    public void ResolveReadOnly_WildcardAsk_CascadesToUnknownAgent()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": { "*": { "ask": "always", "askFallback": "on-miss", "autoAllowSkills": true } }
        }
        """);

        var resolved = Store().ResolveReadOnly("any-agent");

        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.AskFallback);
        Assert.True(resolved.Defaults.AutoAllowSkills);
    }

    // ── Explicit non-main agentId ─────────────────────────────────────────────

    [Fact]
    public void ResolveReadOnly_ExplicitAgentId_PreservedInResult()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": { "agent-abc": { "security": "full" } }
        }
        """);

        var resolved = Store().ResolveReadOnly("agent-abc");

        Assert.Equal("agent-abc", resolved.AgentId);
        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
    }

    // ── Socket path ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_SocketPathPreserved()
    {
        WriteFile("""
        {
          "version": 1,
          "socket": { "path": "/run/openclaw.sock", "token": "tok" },
          "agents": {}
        }
        """);

        // path is not exposed via ExecApprovalsResolved (only token is), so we verify
        // indirectly: socket token is intact when path is also present.
        var resolved = Store().ResolveReadOnly("main");
        Assert.Equal("tok", resolved.SocketToken);
    }

    [Fact]
    public void Normalize_BothSocketFieldsEmpty_SocketBecomesNull()
    {
        WriteFile("""
        {
          "version": 1,
          "socket": { "path": "  ", "token": "" },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");
        Assert.Null(resolved.SocketToken);
    }

    // ── WhenWritingNull: null fields omitted from written JSON ────────────────

    [Fact]
    public async Task ResolveAsync_WrittenFile_OmitsNullFields()
    {
        await Store().ResolveAsync(null);

        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("\"socket\"", json);
        Assert.DoesNotContain("\"defaults\"", json);
        Assert.DoesNotContain("null", json);
    }

    // ── Serialization: ExecAsk.Deny serializes as "deny" ─────────────────────

    [Fact]
    public void JsonOptions_ExecAskDeny_SerializesAsDeny()
    {
        var defaults = new ExecApprovalsDefaults { AskFallback = ExecAsk.Deny };
        var json = JsonSerializer.Serialize(defaults, ExecApprovalsStore.JsonOptions);
        Assert.Contains("\"deny\"", json);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MinimalFile() => """{"version":1,"agents":{}}""";

    private static string MinimalFileWithAgent(string agentId, string security) => $$"""
        {
          "version": 1,
          "agents": {
            "{{agentId}}": { "security": "{{security}}" }
          }
        }
        """;
}

internal sealed class CapturingLogger : IOpenClawLogger
{
    public List<string> Infos { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    public void Info(string message) => Infos.Add(message);
    public void Debug(string message) { }
    public void Warn(string message) => Warnings.Add(message);
    public void Error(string message, Exception? ex = null) => Errors.Add(message);
}
