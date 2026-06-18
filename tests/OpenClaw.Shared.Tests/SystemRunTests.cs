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
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_UsesPayloadSessionKey_WhenArgsOmitSessionKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var prompt = new FakePromptHandler(ExecApprovalPromptDecision.AllowOnce());
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(prompt);

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-session-1",
                Command = "system.run",
                Args = Parse("""{"command":"Write-Output hello","shell":"powershell"}"""),
                SessionKey = "payload-session"
            });

            Assert.True(res.Ok, res.Error);
            Assert.Equal(1, prompt.CallCount);
            Assert.Equal("payload-session", prompt.LastRequest?.SessionKey);
            Assert.NotNull(runner.LastRequest);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_PayloadSessionKeyOverridesArgsSessionKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var prompt = new FakePromptHandler(ExecApprovalPromptDecision.AllowOnce());
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(prompt);

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-session-override",
                Command = "system.run",
                Args = Parse("""{"command":"Write-Output hello","shell":"powershell","sessionKey":"spoofed-session"}"""),
                SessionKey = "trusted-session"
            });

            Assert.True(res.Ok, res.Error);
            Assert.Equal(1, prompt.CallCount);
            Assert.Equal("trusted-session", prompt.LastRequest?.SessionKey);
            Assert.NotNull(runner.LastRequest);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPolicy_EvaluatesImplicitShellUsingRunnerEffectiveShell()
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
                    new ExecApprovalRule
                    {
                        Pattern = "Get-Process",
                        Action = ExecApprovalAction.Allow,
                        Shells = new[] { "powershell" }
                    }
                },
                ExecApprovalAction.Deny);
            var runner = new FakeCommandRunner { EffectiveShellForNull = "pwsh" };
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "implicit-shell-policy",
                Command = "system.run",
                Args = Parse("""{"command":"Get-Process"}""")
            });

            Assert.False(res.Ok);
            Assert.Contains("denied", res.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(runner.LastRequest);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_PromptsOnceForShellWrapper_WhenUserApprovesOnce()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var prompt = new FakePromptHandler(ExecApprovalPromptDecision.AllowOnce());
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(prompt);

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-wrapper-1",
                Command = "system.run",
                Args = Parse("""{"command":"cmd /c echo hello"}""")
            });

            Assert.True(res.Ok, res.Error);
            Assert.NotNull(runner.LastRequest);
            Assert.Equal(1, prompt.CallCount);
            Assert.Empty(policy.Rules);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_AlwaysAllowWrapperPersistsSingleRule_AndCoversRepeat()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var logger = new ExecTestLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(Array.Empty<ExecApprovalRule>(), ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var prompt = new FakePromptHandler(ExecApprovalPromptDecision.AlwaysAllow());
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(prompt);

            var req = new NodeInvokeRequest
            {
                Id = "prompt-wrapper-2",
                Command = "system.run",
                Args = Parse("""{"command":"cmd /c echo repeat"}""")
            };

            var first = await cap.ExecuteAsync(req);
            Assert.True(first.Ok, first.Error);
            Assert.Equal(1, prompt.CallCount);
            Assert.Single(policy.Rules);
            Assert.Equal("cmd /c echo repeat", policy.Rules[0].Pattern);

            var second = await cap.ExecuteAsync(req);
            Assert.True(second.Ok, second.Error);
            Assert.Equal(1, prompt.CallCount);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemRun_WithPromptPolicy_StillDeniesExplicitBlockedShellWrapperPayload()
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
                    new ExecApprovalRule { Pattern = "del *", Action = ExecApprovalAction.Deny }
                },
                ExecApprovalAction.Prompt);
            var runner = new FakeCommandRunner();
            var prompt = new FakePromptHandler(ExecApprovalPromptDecision.AllowOnce());
            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(runner);
            cap.SetApprovalPolicy(policy);
            cap.SetPromptHandler(prompt);

            var res = await cap.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "prompt-wrapper-3",
                Command = "system.run",
                Args = Parse("""{"command":"cmd /c del C:\\important.txt"}""")
            });

            Assert.False(res.Ok);
            Assert.Contains("denied", res.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(runner.LastRequest);
            Assert.Equal(1, prompt.CallCount);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
        public string EffectiveShellForNull { get; set; } = "powershell";

        public string ResolveEffectiveShell(string? requestedShell)
        {
            return string.IsNullOrWhiteSpace(requestedShell)
                ? EffectiveShellForNull
                : requestedShell.Trim();
        }

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

        public int CallCount { get; private set; }
        public ExecApprovalPromptRequest? LastRequest { get; private set; }

        public Task<ExecApprovalPromptDecision> RequestAsync(
            ExecApprovalPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_decision);
        }
    }
}

[CollectionDefinition("EnvironmentMutation", DisableParallelization = true)]
public sealed class EnvironmentMutationTestCollection
{
    public const string Name = "EnvironmentMutation";
}

[Collection(EnvironmentMutationTestCollection.Name)]
public class LocalCommandRunnerTests
{
    [Fact]
    public void BuildProcessArgs_DefaultShellUsesPwshWhenAvailableOnPath()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "openclaw-pwsh-path-" + Guid.NewGuid().ToString("N"))).FullName;
        var fakePwsh = Path.Combine(tempDir, "pwsh.exe");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathMixedCase = Environment.GetEnvironmentVariable("Path");
        try
        {
            File.WriteAllBytes(fakePwsh, Array.Empty<byte>());
            Environment.SetEnvironmentVariable("PATH", tempDir);
            Environment.SetEnvironmentVariable("Path", tempDir);

            var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
            {
                Command = "Write-Output hi",
            });

            Assert.Equal(fakePwsh, fileName);
            Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("Path", originalPathMixedCase);
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide assertion failures.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildProcessArgs_DefaultShellFallsBackToWindowsPowerShellWhenPwshMissing()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathMixedCase = Environment.GetEnvironmentVariable("Path");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("Path", string.Empty);

            var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
            {
                Command = "Write-Output hi",
            });

            Assert.Equal(ExpectedWindowsPowerShellExe(), fileName);
            Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("Path", originalPathMixedCase);
        }
    }

    [Fact]
    public void BuildProcessArgs_ExplicitPwshDoesNotFallback()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathMixedCase = Environment.GetEnvironmentVariable("Path");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("Path", string.Empty);

            var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
            {
                Command = "Write-Output hi",
                Shell = "pwsh",
            });

            Assert.Equal("pwsh.exe", fileName);
            Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("Path", originalPathMixedCase);
        }
    }

    [Fact]
    public void BuildProcessArgs_ExplicitWindowsPowerShellUsesWindowsPowerShell()
    {
        var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
        {
            Command = "Write-Output hi",
            Shell = "powershell",
        });

        Assert.Equal(ExpectedWindowsPowerShellExe(), fileName);
        Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
    }

    private static string ExpectedWindowsPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
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
            TimeoutMs = 30000
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
            Shell = "cmd",
            TimeoutMs = 5000
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

/// <summary>
/// Unit tests for LocalCommandRunner.PlanExecution — the direct-argv vs shell-wrapped
/// decision for the exec-approvals direct-argv path. Always-on: no process is spawned.
/// </summary>
public class LocalCommandRunnerPlanTests
{
    [Fact]
    public void DirectArgv_UsesArgv0AsFileName_AndNoShell()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Argv = new[] { "C:\\tools\\where.exe", "dotnet" },
            // These must be ignored when Argv is present.
            Command = "Get-Process",
            Shell = "powershell",
            Args = new[] { "ignored" },
        });

        Assert.True(plan.IsDirectArgv);
        Assert.Equal("C:\\tools\\where.exe", plan.FileName);
        Assert.Equal(new[] { "dotnet" }, plan.ArgList);
        Assert.Null(plan.Arguments);
    }

    [Fact]
    public void DirectArgv_PreservesArgumentsVerbatim_NoTrimNoMangle()
    {
        // Arguments are passed through untouched. The nasty cases (whitespace, empty,
        // quotes, backslashes, shell metacharacters, Unicode) are round-tripped by
        // ProcessStartInfo.ArgumentList at the OS boundary; here we only assert our
        // planner does not alter them.
        var args = new[]
        {
            "  leading-and-trailing  ",
            "",
            "with \"quotes\"",
            "trailing\\",
            "% ! & | ^ > < ( )",
            "café-ünïcode-日本語",
        };
        var argv = new List<string> { "C:\\probe.exe" };
        argv.AddRange(args);

        var plan = LocalCommandRunner.PlanExecution(new CommandRequest { Argv = argv });

        Assert.True(plan.IsDirectArgv);
        Assert.Equal("C:\\probe.exe", plan.FileName);
        Assert.Equal(args, plan.ArgList);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DirectArgv_RejectsEmptyExecutable(string exe)
    {
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe } }));
    }

    [Theory]
    [InlineData("where.exe")]          // bare name → Windows would guess from PATH
    [InlineData("tools\\probe.exe")]   // relative path
    [InlineData("\\probe.exe")]        // rooted but not fully qualified
    public void DirectArgv_RejectsNonAbsoluteExecutable(string exe)
    {
        // argv[0] must be a fully-qualified path so Windows never guesses the
        // executable (Program.exe hijack).
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe } }));
    }

    [Theory]
    [InlineData("C:\\scripts\\deploy.bat")]
    [InlineData("C:\\scripts\\deploy.cmd")]
    [InlineData("C:\\scripts\\DEPLOY.BAT")]
    public void DirectArgv_RejectsBatchScripts(string exe)
    {
        // .bat/.cmd need cmd.exe, which re-parses args and breaks the verbatim-argv
        // guarantee.
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe, "arg" } }));
    }

    [Fact]
    public void DirectArgv_SingleElement_HasEmptyArgList()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Argv = new[] { "C:\\Windows\\System32\\whoami.exe" },
        });

        Assert.True(plan.IsDirectArgv);
        Assert.Empty(plan.ArgList!);
    }

    [Fact]
    public void LegacyPath_WhenArgvNull_WrapsInPowerShell()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Command = "Write-Output hi",
            Shell = "powershell",
        });

        Assert.False(plan.IsDirectArgv);
        Assert.Null(plan.ArgList);
        Assert.Equal("powershell.exe", plan.FileName);
        Assert.Contains("Write-Output hi", plan.Arguments);
    }

    [Fact]
    public void DirectArgv_WhenArgvEmptyButNotNull_ThrowsNotFallback()
    {
        // A non-null but empty Argv is a malformed approved payload. It must fail
        // closed, never silently fall back to the shell (ICommandRunner contract:
        // only null Argv means legacy).
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest
            {
                Argv = System.Array.Empty<string>(),
                Command = "echo hi",
                Shell = "cmd",
            }));
    }
}
