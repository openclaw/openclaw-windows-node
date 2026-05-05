using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for system.run capability and LocalCommandRunner.
/// </summary>
public class SystemRunTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void SystemCapability_CanHandle_SystemRun()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("system.run"));
        Assert.True(cap.CanHandle("system.notify"));
    }

    [Fact]
    public async Task SystemRun_ReturnsError_WhenNoRunner()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        // Don't set a runner
        var req = new NodeInvokeRequest
        {
            Id = "r1",
            Command = "system.run",
            Args = Parse("""{"command":"echo hello"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemRun_ReturnsError_WhenCommandMissing()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeCommandRunner());

        var req = new NodeInvokeRequest
        {
            Id = "r2",
            Command = "system.run",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("command", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemRun_DelegatesToRunner()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r3",
            Command = "system.run",
            Args = Parse("""{"command":"echo hello","shell":"cmd","cwd":"C:\\","timeout":5000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("echo hello", runner.LastRequest!.Command);
        Assert.Equal("cmd", runner.LastRequest.Shell);
        Assert.Equal("C:\\", runner.LastRequest.Cwd);
        Assert.Equal(5000, runner.LastRequest.TimeoutMs);
    }

    [Fact]
    public async Task SystemRun_ParsesArgsArray()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r4",
            Command = "system.run",
            Args = Parse("""{"command":"git","args":["status","--short"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest!.Args);
        Assert.Equal(2, runner.LastRequest.Args!.Length);
        Assert.Equal("status", runner.LastRequest.Args[0]);
        Assert.Equal("--short", runner.LastRequest.Args[1]);
    }

    [Fact]
    public async Task SystemRun_ParsesEnvDict()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r5",
            Command = "system.run",
            Args = Parse("""{"command":"test","env":{"FOO":"bar","BAZ":"qux"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest!.Env);
        Assert.Equal("bar", runner.LastRequest.Env!["FOO"]);
        Assert.Equal("qux", runner.LastRequest.Env["BAZ"]);
    }

    [Fact]
    public async Task SystemRun_BlocksDangerousEnvOverride()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r5b",
            Command = "system.run",
            Args = Parse("""{"command":"test","env":{"PATH":"C:\\evil","FOO":"bar"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("environment variable", res.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task SystemRun_BlocksInvalidEnvName()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r5c",
            Command = "system.run",
            Args = Parse("""{"command":"test","env":{"BAD NAME":"value"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("environment variable", res.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task SystemRun_BlocksSecretEnvOverride()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r5d",
            Command = "system.run",
            Args = Parse("""{"command":"test","env":{"GITHUB_TOKEN":"secret","FOO":"bar"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("GITHUB_TOKEN", res.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task SystemRun_DefaultsTimeout_To30s()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r6",
            Command = "system.run",
            Args = Parse("""{"command":"test"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal(30000, runner.LastRequest!.TimeoutMs);
    }

    [Fact]
    public async Task SystemRun_ReturnsStdoutStderrExitCode()
    {
        var runner = new FakeCommandRunner
        {
            Result = new CommandResult { Stdout = "hello", Stderr = "warn", ExitCode = 0, DurationMs = 42 }
        };
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r7",
            Command = "system.run",
            Args = Parse("""{"command":"test"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        // Verify payload has the right shape by serializing and re-parsing
        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("hello", root.GetProperty("stdout").GetString());
        Assert.Equal("warn", root.GetProperty("stderr").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(42, root.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public async Task SystemRun_HandlesRunnerException()
    {
        var runner = new FakeCommandRunner { ShouldThrow = true };
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r8",
            Command = "system.run",
            Args = Parse("""{"command":"test"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Execution failed", res.Error);
    }

    [Fact]
    public async Task SystemRunPrepare_ParsesArgvArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "rp1",
            Command = "system.run.prepare",
            Args = Parse("""{"command":["git","status","--short"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok, res.Error);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("git status --short", root.GetProperty("cmdText").GetString());
        var plan = root.GetProperty("plan");
        var argv = plan.GetProperty("argv");
        Assert.Equal("git", argv[0].GetString());
        Assert.Equal("status", argv[1].GetString());
        Assert.Equal("--short", argv[2].GetString());
    }

    [Fact]
    public async Task SystemRunPrepare_ParsesStringCommand()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "rp2",
            Command = "system.run.prepare",
            Args = Parse("""{"command":"echo hello"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok, res.Error);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("\"echo hello\"", root.GetProperty("cmdText").GetString());
        var argv = root.GetProperty("plan").GetProperty("argv");
        Assert.Single(argv.EnumerateArray());
        Assert.Equal("echo hello", argv[0].GetString());
    }

    [Fact]
    public async Task SystemRunPrepare_ReturnsError_WhenMissingCommand()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "rp3",
            Command = "system.run.prepare",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Missing command", res.Error);
    }

    [Fact]
    public async Task SystemRunPrepare_ReturnsError_WhenEmptyArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "rp4",
            Command = "system.run.prepare",
            Args = Parse("""{"command":[]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Missing command", res.Error);
    }

    [Fact]
    public async Task SystemRun_ParsesArgvArrayForRun()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "ra1",
            Command = "system.run",
            Args = Parse("""{"command":["git","push","origin","main"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("git", runner.LastRequest!.Command);
        Assert.NotNull(runner.LastRequest.Args);
        Assert.Equal(3, runner.LastRequest.Args!.Length);
        Assert.Equal("push", runner.LastRequest.Args[0]);
    }

    [Fact]
    public async Task SystemRun_WithPolicy_DeniesEncodedPowerShellPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(
                new[]
                {
                    new ExecApprovalRule { Pattern = "Remove-Item *", Action = ExecApprovalAction.Deny }
                },
                ExecApprovalAction.Allow);

            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(new FakeCommandRunner());
            cap.SetApprovalPolicy(policy);

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Remove-Item -Recurse -Force C:\\important"));
            var req = new NodeInvokeRequest
            {
                Id = "r10",
                Command = "system.run",
                Args = Parse($$"""{"command":["powershell","-EncodedCommand","{{encoded}}"]}""")
            };

            var res = await cap.ExecuteAsync(req);
            Assert.False(res.Ok);
            Assert.Contains("denied", res.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_AllowsOnce_WhenUserApproves()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(new FakePromptHandler(ExecApprovalPromptDecision.AllowOnce()));

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-1",
                Command = "system.run",
                Args = Parse("""{"command":"Write-Output hello","shell":"powershell"}""")
            });

            Assert.True(res.Ok);
            Assert.NotNull(runner.LastRequest);
            Assert.Empty(policy.Rules);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_PersistsExactAllowRule_WhenUserAlwaysAllows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(new FakeCommandRunner());
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(new FakePromptHandler(ExecApprovalPromptDecision.AlwaysAllow()));

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-2",
                Command = "system.run",
                Args = Parse("""{"command":"whoami"}""")
            });

            Assert.True(res.Ok);
            Assert.Single(policy.Rules);
            Assert.Equal("whoami", policy.Rules[0].Pattern);
            Assert.Equal(ExecApprovalAction.Allow, policy.Rules[0].Action);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_Denies_WhenUserDenies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(new FakePromptHandler(ExecApprovalPromptDecision.Deny()));

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-3",
                Command = "system.run",
                Args = Parse("""{"command":"whoami"}""")
            });

            Assert.False(res.Ok);
            Assert.Null(runner.LastRequest);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Fake runner for unit testing — no actual process execution.
    /// </summary>
    private class FakeCommandRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }
        public CommandResult Result { get; set; } = new() { Stdout = "ok", ExitCode = 0 };
        public bool ShouldThrow { get; set; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (ShouldThrow)
                throw new InvalidOperationException("Runner error");
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePromptHandler : IExecApprovalPromptHandler
    {
        private readonly ExecApprovalPromptDecision _decision;

        public FakePromptHandler(ExecApprovalPromptDecision decision)
        {
            _decision = decision;
        }

        public Task<ExecApprovalPromptDecision> RequestAsync(
            ExecApprovalPromptRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_decision);
    }
}

/// <summary>
/// Integration tests for LocalCommandRunner — actually executes processes.
/// Gated by OPENCLAW_RUN_INTEGRATION=1.
/// </summary>
public class LocalCommandRunnerIntegrationTests
{
    [IntegrationFact]
    public async Task Run_EchoCommand_Powershell()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Write-Output 'hello world'",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.Stdout);
        Assert.False(result.TimedOut);
    }

    [IntegrationFact]
    public async Task Run_EchoCommand_Cmd()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hello cmd",
            Shell = "cmd",
            TimeoutMs = 10000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello cmd", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_NonZeroExitCode()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "exit 42",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.Equal(42, result.ExitCode);
    }

    [IntegrationFact]
    public async Task Run_CapturesStderr()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Write-Error 'oops' 2>&1; exit 0",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.True(result.Stderr.Length > 0 || result.Stdout.Contains("oops"));
    }

    [IntegrationFact]
    public async Task Run_Timeout_KillsProcess()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Start-Sleep -Seconds 30",
            Shell = "powershell",
            TimeoutMs = 1000
        });

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [IntegrationFact]
    public async Task Run_WithCwd()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Get-Location | Select -ExpandProperty Path",
            Shell = "powershell",
            Cwd = "C:\\",
            TimeoutMs = 10000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("C:\\", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_WithEnvVars()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo %TEST_OPENCLAW_VAR%",
            Shell = "cmd",
            TimeoutMs = 10000,
            Env = new() { { "TEST_OPENCLAW_VAR", "hello_from_test" } }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello_from_test", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_InvalidCommand_ReturnsError()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "this-command-does-not-exist-12345",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    /// <summary>
    /// Regression test for: system.run hangs indefinitely for CLI tools that connect to a
    /// running process via local IPC (e.g. Obsidian.com, docker version).
    ///
    /// Root cause: WaitForExitAsync (.NET 6+) internally calls WaitForExit() which blocks
    /// until async stream reads reach EOF. When a CLI tool spawns a background child process
    /// (as IPC clients often do), that child inherits the stdout pipe write handle. The outer
    /// process exits, but the pipe stays open because the child still holds the write end —
    /// so WaitForExitAsync never returns.
    ///
    /// Fix: Use process.Exited event (fires on process exit only, not stream EOF) then drain
    /// remaining buffered output with a 500 ms deadline.
    /// </summary>
    [IntegrationFact]
    public async Task Run_CompletesPromptly_WhenOrphanChildProcessHoldsStdoutHandle()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // cmd.exe / start / timeout are Windows-only

        // Start a cmd that echoes output and then spawns a long-running background child.
        // The background child inherits the Hub's stdout pipe write handle, so EOF never
        // arrives on the pipe after the outer cmd exits.
        // Before the fix: WaitForExitAsync hangs for up to 30 s (the background child's lifetime).
        // After the fix: returns within the ~500 ms drain window.
        var runner = new LocalCommandRunner();
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = @"echo hello& start """" /B cmd.exe /C timeout /T 30 /NOBREAK >nul",
            Shell = "cmd",
            TimeoutMs = 5000
        });
        sw.Stop();

        Assert.Contains("hello", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TimedOut, "Command should not have timed out");
        // Allow 3 s: 500 ms drain deadline + generous margin for CI environment variability.
        // Without the fix this would block for ~30 s (the background child's lifetime).
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Command took {sw.ElapsedMilliseconds}ms — expected < 3000ms (possible WaitForExitAsync hang regression)");
    }
}
