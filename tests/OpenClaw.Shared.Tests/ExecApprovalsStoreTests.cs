using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Coverage: deserialization, normalization, cascade resolution, malformed/version guards,
// default-deny semantics, persistence, and observation behavior.
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

    private ExecApprovalsStore Store(string stateDir) => new(_dir, _log, stateDir);

    private string FilePath => Path.Combine(_dir, "exec-approvals.json");

    private void WriteFile(string json) => File.WriteAllText(FilePath, json);

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    // Two generated entries can share an executable and differ only by argument
    // binding. Usage metadata identifies which grant actually authorized a run, so
    // stamping it on every sibling with the same pattern makes the audit trail claim a
    // rule was used when it never matched.
    [Fact]
    public async Task RecordAllowlistUse_StampsOnlyTheMatchedArgumentBinding()
    {
        var used = ExecArgPattern.BuildArgPattern(new[] { "C:\\bin\\tool.exe", "--safe" });
        var other = ExecArgPattern.BuildArgPattern(new[] { "C:\\bin\\tool.exe", "--dangerous" });
        WriteFile($$"""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [
                { "id": "11111111-1111-1111-1111-111111111111", "pattern": "**/tool.exe", "source": "allow-always", "argPattern": {{JsonSerializer.Serialize(used)}} },
                { "id": "22222222-2222-2222-2222-222222222222", "pattern": "**/tool.exe", "source": "allow-always", "argPattern": {{JsonSerializer.Serialize(other)}} }
              ]
            }
          }
        }
        """);

        var changed = await Store().RecordAllowlistUseAsync(
            "main", "**/tool.exe", "C:\\bin\\tool.exe", "tool.exe --safe", entryId: Guid.Parse("11111111-1111-1111-1111-111111111111"), argPattern: used);
        Assert.True(changed);

        var entries = JsonDocument.Parse(File.ReadAllText(FilePath))
            .RootElement.GetProperty("agents").GetProperty("main").GetProperty("allowlist")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e);

        Assert.True(entries["11111111-1111-1111-1111-111111111111"].TryGetProperty("lastUsedAt", out _));
        Assert.False(
            entries["22222222-2222-2222-2222-222222222222"].TryGetProperty("lastUsedAt", out _),
            "The non-matching argument binding must not be stamped as used.");
        Assert.False(entries["22222222-2222-2222-2222-222222222222"].TryGetProperty("lastUsedCommand", out _));
    }

    // ── Prompt-on-miss when file absent ──────────────────────────────────────

    // A generated entry is only safe because its argument pattern is what makes it
    // match. Rewriting the file must not drop argPattern or source: losing either one
    // silently converts a narrow generated rule into a path-only rule, which is a
    // wider grant than the operator ever approved.
    [Fact]
    public async Task Normalization_PreservesGeneratedEntryBinding()
    {
        WriteFile(
            """
            {"version":1,"defaults":{"security":"allowlist","ask":"on-miss"},"agents":{"main":{"allowlist":[
              {"pattern":"C:\\tools\\a.exe","source":"allow-always","argPattern":"^--one\u0000\u0000$","commandText":"a.exe --one"}
            ]}}}
            """);

        // Any write goes through the same normalization path that rebuilds every entry.
        await Store().AddAllowlistEntryAsync("main", "C:\\tools\\b.exe");

        var entry = Assert.Single(
            Store().ResolveReadOnly("main").Allowlist,
            candidate => candidate.Pattern == "C:\\tools\\a.exe");
        Assert.Equal("allow-always", entry.Source);
        Assert.Equal("^--one\u0000\u0000$", entry.ArgPattern);
        Assert.Equal("a.exe --one", entry.CommandText);
    }

    [Fact]
    public void ResolveReadOnly_NoFile_ReturnsAllowlistOnMiss()
    {
        var resolved = Store().ResolveReadOnly(null);

        Assert.Equal("main", resolved.AgentId);
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.AskFallback);
        Assert.False(resolved.Defaults.AutoAllowSkills);
        Assert.Empty(resolved.Allowlist);
    }

    [Fact]
    public void ResolveReadOnly_DirectoryAtFilePath_ReturnsDefaultDeny()
    {
        Directory.CreateDirectory(FilePath);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, warning => warning.Contains("path is a directory"));
    }

    [Fact]
    public void ResolveReadOnly_DirectoryAtLegacyPathWithStateOverride_ReturnsDefaultDeny()
    {
        var stateDir = Path.Combine(_dir, "state");
        Directory.CreateDirectory(FilePath);

        var resolved = Store(stateDir).ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.False(File.Exists(Path.Combine(stateDir, "exec-approvals.json")));
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

    [Fact]
    public async Task Snapshot_HashMatchesExactPersistedBytes()
    {
        var snapshot = await Store().GetSnapshotAsync();
        var raw = File.ReadAllText(FilePath);

        Assert.Equal(Hash(raw), snapshot.Hash);
    }

    [Fact]
    public async Task Replace_HashMatchesExactPersistedBytes()
    {
        var store = Store();
        var before = await store.GetSnapshotAsync();
        before.File.Defaults!.Ask = ExecAsk.Always;

        var after = await store.ReplaceAsync(before.Hash, before.File);

        Assert.NotNull(after);
        Assert.Equal(Hash(File.ReadAllText(FilePath)), after!.Hash);
    }

    [Fact]
    public void ResolveFilePath_StateDirOverrideWins()
    {
        var stateDir = Path.Combine(_dir, "state");

        var path = ExecApprovalsStore.ResolveFilePath(
            _dir,
            stateDir,
            openClawHomeOverride: null,
            osHomeOverride: _dir);

        Assert.Equal(Path.Combine(stateDir, "exec-approvals.json"), path);
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

    [Fact]
    public void ResolveReadOnly_NumericEnumValue_ReturnsDefaultDenyAndWarns()
    {
        WriteFile(
            """{"version":1,"defaults":{"security":-1},"agents":{}}""");

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Contains(_log.Warnings, warning => warning.Contains("malformed"));
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
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.AskFallback);
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

        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.AskFallback);
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
    public async Task ResolveAsync_NoFile_CreatesFileAndReturnsAllowlistOnMiss()
    {
        var resolved = await Store().ResolveAsync(null);

        Assert.True(File.Exists(FilePath));
        Assert.Equal("main", resolved.AgentId);
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
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

    [Fact]
    public void ResolveReadOnly_CustomStateDir_FailsClosedUntilMigration()
    {
        WriteFile(MinimalFileWithAgent("main", "allowlist"));
        var stateDir = Path.Combine(_dir, "custom-state");

        var resolved = Store(stateDir).ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.False(File.Exists(Path.Combine(stateDir, "exec-approvals.json")));
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists($"{FilePath}.migrated"));
        Assert.DoesNotContain(_log.Infos, message => message.Contains("Migrated"));
    }

    [Fact]
    public void MigrateLegacyFileIfNeeded_CustomStateDir_MigratesLegacyFile()
    {
        WriteFile("""
        {
          "version": 1,
          "socket": { "path": "legacy.sock", "token": "socket-token" },
          "defaults": { "askFallback": "off" },
          "agents": {
            "main": {
              "security": "allowlist",
              "allowlist": [{ "pattern": "tool.exe", "argPattern": "^safe$" }]
            }
          }
        }
        """);
        var stateDir = Path.Combine(_dir, "custom-state");

        var store = Store(stateDir);
        store.MigrateLegacyFileIfNeeded();
        var resolved = store.ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Equal(ExecSecurity.Full, resolved.Defaults.AskFallback);
        Assert.Equal("socket-token", resolved.SocketToken);
        Assert.True(File.Exists(Path.Combine(stateDir, "exec-approvals.json")));
        Assert.Contains("\"argPattern\": \"^safe$\"", File.ReadAllText(Path.Combine(stateDir, "exec-approvals.json")));
        Assert.False(File.Exists(FilePath));
        Assert.True(File.Exists($"{FilePath}.migrated"));
        Assert.Contains(_log.Infos, message => message.Contains("Migrated"));
    }

    [Fact]
    public void ResolveReadOnly_CustomStateDir_TargetWinsOverLegacyFile()
    {
        WriteFile(MinimalFileWithAgent("main", "deny"));
        var stateDir = Path.Combine(_dir, "custom-state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(
            Path.Combine(stateDir, "exec-approvals.json"),
            MinimalFileWithAgent("main", "full"));

        var resolved = Store(stateDir).ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists($"{FilePath}.migrated"));
    }

    [Fact]
    public async Task ResolveAsync_CustomStateDir_InvalidLegacyFile_FailsClosedWithoutReplacement()
    {
        WriteFile("{ bad json }");
        var stateDir = Path.Combine(_dir, "custom-state");

        var resolved = await Store(stateDir).ResolveAsync("main");

        Assert.Equal(ExecSecurity.Deny, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.False(File.Exists(Path.Combine(stateDir, "exec-approvals.json")));
        Assert.Equal("{ bad json }", File.ReadAllText(FilePath));
        Assert.Contains(_log.Warnings, message => message.Contains("could not be migrated"));
    }

    [Fact]
    public async Task ResolveAsync_HomeRelativeStateDir_UsesOpenClawHome()
    {
        WriteFile(MinimalFileWithAgent("main", "full"));
        var openClawHome = Path.Combine(_dir, "effective-home");
        var stateDirOverride = $"~{Path.DirectorySeparatorChar}custom-state";
        var store = new ExecApprovalsStore(
            _dir,
            _log,
            stateDirOverride,
            openClawHomeOverride: openClawHome,
            osHomeOverride: Path.Combine(_dir, "os-home"));

        var resolved = await store.ResolveAsync("main");

        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.True(File.Exists(Path.Combine(openClawHome, "custom-state", "exec-approvals.json")));
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
                AskFallback = ExecSecurity.Full,
                AutoAllowSkills = false,
            },
            Agents = [],
        };

        var json = JsonSerializer.Serialize(file, ExecApprovalsStore.JsonOptions);

        Assert.Contains("\"allowlist\"", json);
        Assert.Contains("\"on-miss\"", json);
        Assert.Contains("\"full\"", json);
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
          "defaults": { "security": "full", "ask": "always", "askFallback": "allowlist", "autoAllowSkills": true },
          "agents": {}
        }
        """);

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.AskFallback);
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
          "agents": { "*": { "ask": "always", "askFallback": "allowlist", "autoAllowSkills": true } }
        }
        """);

        var resolved = Store().ResolveReadOnly("any-agent");

        Assert.Equal(ExecAsk.Always, resolved.Defaults.Ask);
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.AskFallback);
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
        Assert.Contains("\"defaults\"", json);
        Assert.Contains("\"security\": \"allowlist\"", json);
        Assert.DoesNotContain("null", json);
    }

    // ── Serialization: ExecAsk.Deny serializes as "deny" ─────────────────────

    [Fact]
    public void JsonOptions_ExecAskDeny_SerializesAsDeny()
    {
        var defaults = new ExecApprovalsDefaults { AskFallback = ExecSecurity.Deny };
        var json = JsonSerializer.Serialize(defaults, ExecApprovalsStore.JsonOptions);
        Assert.Contains("\"deny\"", json);
    }

    [Theory]
    [InlineData("off", ExecSecurity.Full)]
    [InlineData("on-miss", ExecSecurity.Allowlist)]
    [InlineData("always", ExecSecurity.Deny)]
    public void ResolveReadOnly_LegacyAskFallback_MapsToSecurity(string legacyValue, ExecSecurity expected)
    {
        WriteFile($"{{\"version\":1,\"defaults\":{{\"askFallback\":\"{legacyValue}\"}},\"agents\":{{}}}}");

        var resolved = Store().ResolveReadOnly("main");

        Assert.Equal(expected, resolved.Defaults.AskFallback);
    }

    // ── Write path: AddAllowlistEntryAsync ───────────────────────────────────

    [Fact]
    public async Task AddAllowlistEntryAsync_ExistingFile_AddsEntryWithIdAndMetadata()
    {
        WriteFile(MinimalFile());
        var store = Store();
        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.True(result);
        var resolved = store.ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        var entry = resolved.Allowlist[0];
        Assert.Equal("**/git.exe", entry.Pattern);
        Assert.NotNull(entry.Id);
        Assert.Null(entry.LastUsedAt); // macOS addAllowlistEntry: {id, pattern} only — no lastUsedAt on creation
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_DuplicatePattern_NotAddedCaseInsensitive()
    {
        WriteFile(MinimalFile());
        var store = Store();
        var first = await store.AddAllowlistEntryAsync("main", "**/git.exe");
        var second = await store.AddAllowlistEntryAsync("main", "**/GIT.EXE");

        Assert.True(first);
        Assert.True(second); // already present → true
        Assert.Single(store.ResolveReadOnly("main").Allowlist);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_EmptyOrWhitespacePattern_ReturnsFalse()
    {
        WriteFile(MinimalFile());
        var store = Store();

        Assert.False(await store.AddAllowlistEntryAsync("main", ""));
        Assert.False(await store.AddAllowlistEntryAsync("main", "   "));
        Assert.Empty(store.ResolveReadOnly("main").Allowlist);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_NoFile_CreatesFileWithEntry()
    {
        var store = Store();
        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.True(result);
        Assert.True(File.Exists(FilePath));
        var resolved = store.ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.Equal("**/git.exe", resolved.Allowlist[0].Pattern);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_MalformedFile_RefusesToWriteAndWarns()
    {
        WriteFile("{ bad json }");
        var original = File.ReadAllText(FilePath);
        var store = Store();

        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.False(result);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Contains(_log.Warnings, w => w.Contains("Refusing to write"));
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_AtomicWrite_NoTempFileLeft()
    {
        WriteFile(MinimalFile());
        await Store().AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_NewAgent_CreatesAgentWithEntry()
    {
        WriteFile("""{"version":1,"agents":{}}""");
        var store = Store();
        var result = await store.AddAllowlistEntryAsync("agent-xyz", "**/git.exe");

        Assert.True(result);
        Assert.Single(store.ResolveReadOnly("agent-xyz").Allowlist);
    }

    // ── Write path: RecordAllowlistUseAsync ──────────────────────────────────

    [Fact]
    public async Task RecordAllowlistUseAsync_UpdatesMetadataAndPreservesIdAndPattern()
    {
        var id = Guid.NewGuid();
        WriteFile($$"""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [
                { "id": "{{id}}", "pattern": "**/git.exe" }
              ]
            }
          }
        }
        """);
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", "/usr/bin/git");

        Assert.True(result);
        var entry = store.ResolveReadOnly("main").Allowlist[0];
        Assert.Equal(id, entry.Id);
        Assert.Equal("**/git.exe", entry.Pattern);
        Assert.NotNull(entry.LastUsedAt);
        Assert.Equal("/usr/bin/git", entry.LastResolvedPath);
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_PatternNotPresent_ReturnsFalse()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": { "allowlist": [{ "pattern": "**/git.exe" }] }
          }
        }
        """);
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("main", "**/rg.exe", null);

        Assert.False(result);
        Assert.Null(store.ResolveReadOnly("main").Allowlist[0].LastUsedAt);
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_AgentNotPresent_ReturnsFalse()
    {
        WriteFile("""{"version":1,"agents":{}}""");
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("nonexistent", "**/git.exe", null);

        Assert.False(result);
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_MalformedFile_ReturnsFalse()
    {
        WriteFile("{ bad json }");
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", null);

        Assert.False(result);
        Assert.Equal("{ bad json }", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_DoesNotTouchOtherEntries()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [
                { "pattern": "**/git.exe" },
                { "pattern": "**/rg.exe" }
              ]
            }
          }
        }
        """);
        var store = Store();
        await store.RecordAllowlistUseAsync("main", "**/git.exe", null);

        var allowlist = store.ResolveReadOnly("main").Allowlist;
        Assert.NotNull(allowlist.First(e => e.Pattern == "**/git.exe").LastUsedAt);
        Assert.Null(allowlist.First(e => e.Pattern == "**/rg.exe").LastUsedAt);
    }

    // ResolveReadOnly merges wildcard entries into the resolved allowlist, so a hit can be
    // authorized by agents["*"]. RecordAllowlistUseAsync must follow the same source.
    [Fact]
    public async Task RecordAllowlistUseAsync_WildcardBucketOnly_UpdatesMetadata()
    {
        var id = Guid.NewGuid();
        WriteFile($$"""
        {
          "version": 1,
          "agents": {
            "*": {
              "allowlist": [
                { "id": "{{id}}", "pattern": "**/git.exe" }
              ]
            }
          }
        }
        """);
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", "/usr/bin/git");

        Assert.True(result);
        var entry = store.ResolveReadOnly("main").Allowlist.Single();
        Assert.Equal(id, entry.Id);
        Assert.Equal("/usr/bin/git", entry.LastResolvedPath);
        Assert.NotNull(entry.LastUsedAt);
    }

    // Same pattern in both buckets: both entries get metadata updated. The matcher cannot
    // tell them apart structurally, and metadata is informative — not authorization-bearing.
    [Fact]
    public async Task RecordAllowlistUseAsync_PatternInBothBuckets_UpdatesBoth()
    {
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": { "allowlist": [{ "pattern": "**/git.exe" }] },
            "*":    { "allowlist": [{ "pattern": "**/git.exe" }] }
          }
        }
        """);
        var store = Store();
        var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", null);

        Assert.True(result);
        var json = File.ReadAllText(FilePath);
        var lastUsedCount = System.Text.RegularExpressions.Regex.Matches(json, "\"lastUsedAt\"").Count;
        Assert.Equal(2, lastUsedCount);
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_BestEffort_IoExceptionReturnsFalse()
    {
        // Entry present so the mutate lambda returns true (pattern found → something to update).
        // Without a matching entry the mutate would be a no-op and UpdateFileAsync would return
        // false before reaching SaveFileAsync — that is the not-found path, not the IOException path.
        WriteFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "allowlist": [{ "pattern": "**/git.exe" }]
            }
          }
        }
        """);
        var store = Store();

        // FileShare.Read: LoadFile succeeds; File.Move(tmp, target, overwrite:true) fails
        // because the target is open without write/delete sharing → IOException degraded-save path.
        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", null);
            Assert.False(result);           // IOException absorbed, no exception escaped
            Assert.NotEmpty(_log.Warnings); // write failure logged as Warn
        }
    }

    // ── Round-trip and integration ────────────────────────────────────────────

    [Fact]
    public async Task AddAllowlistEntry_RoundTrip_ResolvedByReadPath()
    {
        WriteFile(MinimalFile());
        var store = Store();
        await store.AddAllowlistEntryAsync("main", "**/git.exe");

        var resolved = store.ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.Equal("**/git.exe", resolved.Allowlist[0].Pattern);
    }

    [Fact]
    public async Task RoundTrip_WrittenFileIsValidJsonWithCorrectFields()
    {
        WriteFile(MinimalFile());
        var store = Store();
        await store.AddAllowlistEntryAsync("main", "**/git.exe");
        // lastUsedAt is absent on creation; RecordAllowlistUseAsync stamps it on first use.
        await store.RecordAllowlistUseAsync("main", "**/git.exe", null);

        var json = File.ReadAllText(FilePath);
        using var doc = System.Text.Json.JsonDocument.Parse(json); // valid JSON
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        // lastUsedAt must be a JSON number (Unix epoch ms), not a string.
        var lastUsedAt = doc.RootElement
            .GetProperty("agents").GetProperty("main")
            .GetProperty("allowlist")[0].GetProperty("lastUsedAt");
        Assert.Equal(System.Text.Json.JsonValueKind.Number, lastUsedAt.ValueKind);
        // lastUsedCommand must never appear in the persisted file (security regression guard).
        Assert.DoesNotContain("lastUsedCommand", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_Concurrency_SamePattern_SingleEntry()
    {
        WriteFile(MinimalFile());
        var store = Store();
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => store.AddAllowlistEntryAsync("main", "**/git.exe"))
            .ToList();
        await Task.WhenAll(tasks);

        Assert.Single(store.ResolveReadOnly("main").Allowlist);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_BestEffort_IoExceptionReturnsFalse()
    {
        // FileShare.Read: LoadFile (File.ReadAllText / FileAccess.Read) succeeds because
        // read-sharing is granted. File.Move(tmp, target, overwrite:true) fails because the
        // target is open without write/delete sharing. This exercises the IOException degraded-save
        // path, not the malformed-file refusal branch.
        WriteFile(MinimalFile());
        var store = Store();

        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");
            Assert.False(result);                // IOException absorbed, no exception escaped
            Assert.NotEmpty(_log.Warnings);      // write failure logged as Warn
        }
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_CustomStateDir_MigratesLegacyBeforeWriting()
    {
        WriteFile(MinimalFileWithAgent("main", "allowlist"));
        var stateDir = Path.Combine(_dir, "custom-state");
        var store = Store(stateDir);

        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.True(result);
        // Legacy file migrated first; the write lands on the migrated content, not a fresh file.
        Assert.False(File.Exists(FilePath));
        Assert.True(File.Exists($"{FilePath}.migrated"));
        var resolved = store.ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Single(resolved.Allowlist);
        Assert.Equal("**/git.exe", resolved.Allowlist[0].Pattern);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_CustomStateDir_UnreadableLegacy_RefusesToWrite()
    {
        WriteFile("{ bad json }");
        var stateDir = Path.Combine(_dir, "custom-state");
        var store = Store(stateDir);

        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.False(result);
        // No target file may be created: that would permanently block legacy migration.
        Assert.False(File.Exists(Path.Combine(stateDir, "exec-approvals.json")));
        Assert.True(File.Exists(FilePath));
        Assert.Contains(_log.Warnings, w => w.Contains("Refusing to write"));
    }

    // ── State dir: tilde-only expansion ──────────────────────────────────────

    /// <summary>
    /// A stateDirOverride of exactly "~" (no trailing separator) must resolve to the
    /// effective home directory.  This exercises the path == "~" branch of ExpandHomePrefix.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_TildeOnlyStateDir_ResolvesToEffectiveHome()
    {
        // Legacy file at _dir; stateDir = effectiveHome (via "~")
        WriteFile(MinimalFileWithAgent("main", "full"));
        var openClawHome = Path.Combine(_dir, "effective-home");
        var osHome      = Path.Combine(_dir, "os-home");

        var store = new ExecApprovalsStore(
            _dir,
            _log,
            stateDirOverride:      "~",
            openClawHomeOverride:  openClawHome,
            osHomeOverride:        osHome);

        var resolved = await store.ResolveAsync("main");

        // Migration should have moved the legacy file to effectiveHome.
        Assert.Equal(ExecSecurity.Full, resolved.Defaults.Security);
        Assert.True(File.Exists(Path.Combine(openClawHome, "exec-approvals.json")),
            "exec-approvals.json should have been migrated to the effective-home state dir");
    }

    /// <summary>
    /// When openClawHomeOverride itself starts with "~/" it is expanded relative to
    /// osHomeOverride before being used as the base for further tilde expansion in
    /// stateDirOverride.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_TildePrefixedOpenClawHome_ExpandsRelativeToOsHome()
    {
        WriteFile(MinimalFileWithAgent("main", "allowlist"));
        var osHome = Path.Combine(_dir, "os-home");
        var sep    = Path.DirectorySeparatorChar;

        var store = new ExecApprovalsStore(
            _dir,
            _log,
            stateDirOverride:      $"~{sep}custom-state",
            openClawHomeOverride:  $"~{sep}.openclaw",   // should expand to osHome/.openclaw
            osHomeOverride:        osHome);

        var resolved = await store.ResolveAsync("main");

        // effectiveHome = osHome/.openclaw; stateDir = osHome/.openclaw/custom-state
        var expectedFile = Path.Combine(osHome, ".openclaw", "custom-state", "exec-approvals.json");
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.True(File.Exists(expectedFile),
            $"exec-approvals.json should be at {expectedFile}");
    }

    [Fact]
    public async Task GetSnapshotReadOnlyAsync_MissingStore_ReturnsDefaultSnapshotAndCreatesNothing()
    {
        var dataPath = Path.Combine(_dir, "readonly-missing");
        var filePath = Path.Combine(dataPath, "exec-approvals.json");
        using var store = StoreAt(dataPath);

        var result = await store.GetSnapshotReadOnlyAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.False(result.Snapshot!.Exists);
        Assert.Equal($"missing:{Hash(string.Empty)}", result.Snapshot.Hash);
        Assert.Equal(ExecSecurity.Allowlist, result.Snapshot.File.Defaults!.Security);
        Assert.Equal(ExecAsk.OnMiss, result.Snapshot.File.Defaults.Ask);
        Assert.False(Directory.Exists(dataPath));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task GetSnapshotReadOnlyAsync_MalformedFile_ReturnsTypedFailureWithoutRewrite()
    {
        var dataPath = Path.Combine(_dir, "readonly-invalid");
        Directory.CreateDirectory(dataPath);
        var filePath = Path.Combine(dataPath, "exec-approvals.json");
        WriteFile(filePath, "{ bad json }");
        using var store = StoreAt(dataPath);

        var result = await store.GetSnapshotReadOnlyAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Failure);
        Assert.Equal(ExecApprovalsSnapshotFailureKind.MalformedJson, result.Failure!.Kind);
        Assert.Equal("{ bad json }", File.ReadAllText(filePath));
    }

    [Fact]
    public void Changed_SubscribeWithMissingDirectory_CreatesNothing()
    {
        var dataPath = Path.Combine(_dir, "observer-missing");
        var filePath = Path.Combine(dataPath, "exec-approvals.json");
        using var store = StoreAt(dataPath);
        EventHandler<ExecApprovalsChangedEventArgs> handler = (_, _) => { };

        store.Changed += handler;

        Assert.False(Directory.Exists(dataPath));
        Assert.False(File.Exists(filePath));

        store.Changed -= handler;
    }

    [Fact]
    public async Task Changed_SubscribeWithMissingDirectory_ObservesFirstExternalCreateExactlyOnce()
    {
        var dataPath = Path.Combine(_dir, "observer-external-create", "state");
        var filePath = Path.Combine(dataPath, "exec-approvals.json");
        using var store = StoreAt(dataPath);
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        Directory.CreateDirectory(dataPath);
        WriteAtomically(filePath, SerializeFile(CreateAllowlistFile("**/git.exe")));

        await WaitForConditionAsync(() => changes.Count == 1, "Expected the first external create event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.True(
            change.Kind == ExecApprovalsChangeKind.SnapshotUpdated,
            $"Expected a valid snapshot, got {change.Kind}: {change.Failure?.Kind} {change.Failure?.Message}");
        Assert.NotNull(change.Snapshot);
        Assert.Equal("**/git.exe", change.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task ReplaceAsync_MissingHashFromReadOnlySnapshot_CreatesFile()
    {
        var dataPath = Path.Combine(_dir, "missing-hash-write");
        var filePath = Path.Combine(dataPath, "exec-approvals.json");
        using var store = StoreAt(dataPath);
        var readOnly = await store.GetSnapshotReadOnlyAsync();

        var updated = await store.ReplaceAsync(
            readOnly.Snapshot!.Hash,
            CreateAllowlistFile("**/git.exe"),
            origin: null);

        Assert.NotNull(updated);
        Assert.True(File.Exists(filePath));
        Assert.True(updated!.Exists);
        Assert.Single(store.ResolveReadOnly("main").Allowlist);
    }

    [Fact]
    public async Task Changed_TwoOriginFilteredSubscribers_ReceiveOtherWriterExactlyOnce()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        var originA = store.CreateWriterOrigin();
        var originB = store.CreateWriterOrigin();
        var seenByA = 0;
        var seenByB = 0;

        store.Changed += (_, args) =>
        {
            changes.Enqueue(args);
            if (!ReferenceEquals(args.Origin, originA))
            {
                Interlocked.Increment(ref seenByA);
            }

            if (!ReferenceEquals(args.Origin, originB))
            {
                Interlocked.Increment(ref seenByB);
            }
        };

        var current = (await store.GetSnapshotReadOnlyAsync()).Snapshot!;
        var first = await store.ReplaceAsync(current.Hash, CreateAllowlistFile("**/git.exe"), originA);
        var second = await store.ReplaceAsync(first!.Hash, CreateAllowlistFile("**/rg.exe"), originB);

        Assert.NotNull(second);
        await WaitForConditionAsync(() => changes.Count == 2, "Expected two managed change events.");
        await Task.Delay(250);

        var published = changes.ToArray();
        Assert.Equal(2, published.Length);
        Assert.Same(originA, published[0].Origin);
        Assert.Same(originB, published[1].Origin);
        Assert.True(published[0].Sequence > 0);
        Assert.True(published[1].Sequence > published[0].Sequence);
        Assert.Equal(1, seenByA);
        Assert.Equal(1, seenByB);
    }

    [Fact]
    public async Task Changed_ManagedThenExternalEvents_HaveMonotonicSequences()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        var current = (await store.GetSnapshotReadOnlyAsync()).Snapshot!;

        var managed = await store.ReplaceAsync(
            current.Hash,
            CreateAllowlistFile("**/git.exe"),
            store.CreateWriterOrigin());
        Assert.NotNull(managed);
        await WaitForConditionAsync(() => changes.Count == 1, "Expected the managed update event.");
        await Task.Delay(250);

        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/rg.exe")));
        await WaitForConditionAsync(() => changes.Count == 2, "Expected the external update event.");
        await Task.Delay(250);

        var published = changes.ToArray();
        Assert.Equal(2, published.Length);
        Assert.True(published[0].Sequence > 0);
        Assert.True(published[1].Sequence > published[0].Sequence);
        Assert.NotNull(published[1].Snapshot);
        Assert.Equal("**/rg.exe", published[1].Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task Changed_ExternalAtomicValidReplacement_RaisesExactlyOnce()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/git.exe")));

        await WaitForConditionAsync(() => changes.Count == 1, "Expected one external update event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotUpdated, change.Kind);
        Assert.NotNull(change.Snapshot);
        Assert.Null(change.Failure);
        Assert.Equal("**/git.exe", change.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task GetSnapshotReadOnlyAsync_ActiveSubscriber_DoesNotConsumePendingExternalChange()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        await store.GetSnapshotReadOnlyAsync();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);

        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/git.exe")));
        var directRead = await store.GetSnapshotReadOnlyAsync();

        Assert.Equal("**/git.exe", directRead.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
        await WaitForConditionAsync(() => changes.Count == 1, "Expected the pending external update event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotUpdated, change.Kind);
        Assert.Equal("**/git.exe", change.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task Changed_ReactivateAfterUnobservedEdit_DoesNotSuppressRevertToPriorContent()
    {
        var originalJson = SerializeFile(CreateAllowlistFile("**/git.exe"));
        WriteFile(originalJson);
        using var store = Store();
        await store.GetSnapshotReadOnlyAsync();
        EventHandler<ExecApprovalsChangedEventArgs> firstHandler = (_, _) => { };
        store.Changed += firstHandler;
        store.Changed -= firstHandler;

        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/rg.exe")));

        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        var reactivated = await store.GetSnapshotReadOnlyAsync();
        Assert.Equal("**/rg.exe", reactivated.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);

        WriteAtomically(FilePath, originalJson);

        await WaitForConditionAsync(() => changes.Count == 1, "Expected the reverted external update event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.Equal("**/git.exe", change.Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task Changed_ExternalCorruptThenValid_RaisesFailureThenRecovery()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        WriteAtomically(FilePath, "{ bad json }");

        await WaitForConditionAsync(() => changes.Count == 1, "Expected one failure event.");
        var failure = Assert.Single(changes);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotInvalid, failure.Kind);
        Assert.NotNull(failure.Failure);
        Assert.Equal(ExecApprovalsSnapshotFailureKind.MalformedJson, failure.Failure!.Kind);
        Assert.Null(failure.Snapshot);
        Assert.NotNull(failure.LastValidSnapshot);
        Assert.True(failure.LastValidSnapshot!.Exists);

        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/rg.exe")));

        await WaitForConditionAsync(() => changes.Count == 2, "Expected a recovery event.");
        await Task.Delay(250);

        var published = changes.ToArray();
        Assert.Equal(2, published.Length);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotRecovered, published[1].Kind);
        Assert.NotNull(published[1].Snapshot);
        Assert.Equal("**/rg.exe", published[1].Snapshot!.File.Agents!["main"].Allowlist![0].Pattern);
    }

    [Fact]
    public async Task AddAllowlistEntryAsync_RaisesChangedOnceWithoutWatcherEcho()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        var result = await store.AddAllowlistEntryAsync("main", "**/git.exe");

        Assert.True(result);
        await WaitForConditionAsync(() => changes.Count == 1, "Expected one add event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotUpdated, change.Kind);
        Assert.Null(change.Origin);
        Assert.NotNull(change.Snapshot);
        Assert.Single(change.Snapshot!.File.Agents!["main"].Allowlist!);
    }

    [Fact]
    public async Task RecordAllowlistUseAsync_RaisesChangedOnceWithoutWatcherEcho()
    {
        WriteFile(SerializeFile(CreateAllowlistFile("**/git.exe")));
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        var result = await store.RecordAllowlistUseAsync("main", "**/git.exe", "C:\\tools\\git.exe");

        Assert.True(result);
        await WaitForConditionAsync(() => changes.Count == 1, "Expected one usage event.");
        await Task.Delay(250);

        var change = Assert.Single(changes);
        Assert.Equal(ExecApprovalsChangeKind.SnapshotUpdated, change.Kind);
        Assert.NotNull(change.Snapshot);
        Assert.NotNull(change.Snapshot!.File.Agents!["main"].Allowlist![0].LastUsedAt);
    }

    [Fact]
    public async Task Dispose_StopsObservation()
    {
        WriteFile(MinimalFile());
        var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        await store.GetSnapshotReadOnlyAsync();

        store.Dispose();
        WriteAtomically(FilePath, SerializeFile(CreateAllowlistFile("**/git.exe")));
        await Task.Delay(350);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task ReplaceAsync_StaleHash_ReturnsNullAndDoesNotRaiseSuccess()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        var readOnly = await store.GetSnapshotReadOnlyAsync();

        var first = await store.ReplaceAsync(
            readOnly.Snapshot!.Hash,
            CreateAllowlistFile("**/git.exe"),
            store.CreateWriterOrigin());

        Assert.NotNull(first);
        await WaitForConditionAsync(() => changes.Count == 1, "Expected the first managed change.");

        var stale = await store.ReplaceAsync(
            readOnly.Snapshot.Hash,
            CreateAllowlistFile("**/rg.exe"),
            store.CreateWriterOrigin());

        Assert.Null(stale);
        await Task.Delay(250);
        Assert.Single(changes);
    }

    [Fact]
    public async Task ReplaceAsync_ConcurrentDisjointWrites_OneStaleRejection()
    {
        WriteFile(MinimalFile());
        using var store = Store();
        var changes = new ConcurrentQueue<ExecApprovalsChangedEventArgs>();
        store.Changed += (_, args) => changes.Enqueue(args);
        var readOnly = await store.GetSnapshotReadOnlyAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ExecApprovalsSnapshot?> ReplaceAsync(string pattern, ExecApprovalsWriterOrigin origin)
        {
            await gate.Task;
            return await store.ReplaceAsync(readOnly.Snapshot!.Hash, CreateAllowlistFile(pattern), origin);
        }

        var first = ReplaceAsync("**/git.exe", store.CreateWriterOrigin());
        var second = ReplaceAsync("**/rg.exe", store.CreateWriterOrigin());
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result is not null));
        Assert.Equal(1, results.Count(result => result is null));
        await WaitForConditionAsync(() => changes.Count == 1, "Expected one winning change.");
        await Task.Delay(250);

        Assert.Single(changes);
        Assert.Single(store.ResolveReadOnly("main").Allowlist);
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

    private ExecApprovalsStore StoreAt(string dataPath) => new(dataPath, _log);

    private static void WriteFile(string filePath, string json) => File.WriteAllText(filePath, json);

    private static string SerializeFile(ExecApprovalsFile file) =>
        JsonSerializer.Serialize(file, ExecApprovalsStore.JsonOptions);

    private static ExecApprovalsFile CreateAllowlistFile(string pattern) =>
        new()
        {
            Version = 1,
            Agents = new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new ExecApprovalsAgent
                {
                    Security = ExecSecurity.Allowlist,
                    Allowlist =
                    [
                        new ExecAllowlistEntry
                        {
                            Id = Guid.NewGuid(),
                            Pattern = pattern,
                        },
                    ],
                },
            },
        };

    private static void WriteAtomically(string filePath, string json)
    {
        var directoryPath = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directoryPath);
        var tempPath = Path.Combine(directoryPath, $".exec-approvals-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), message);
    }
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
