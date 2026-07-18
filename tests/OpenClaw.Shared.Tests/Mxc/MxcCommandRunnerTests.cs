using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcCommandRunnerTests
{
    private static SettingsData NewSettings(
        bool sandboxEnabled = true,
        bool blockHostFallbackWhenMxcUnavailable = false)
    {
        return new SettingsData
        {
            SystemRunSandboxEnabled = sandboxEnabled,
            SystemRunBlockHostFallbackWhenMxcUnavailable = blockHostFallbackWhenMxcUnavailable,
            SystemRunAllowOutbound = false,
        };
    }

    private static MxcCommandRunner NewRunner(
        ISandboxExecutor executor,
        ICommandRunner hostFallback,
        SettingsData settings,
        bool sandboxAvailable = true,
        IOpenClawLogger? logger = null)
    {
        return new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => "C:\\test\\settings",
            () => sandboxAvailable,
            invalidateAvailability: null,
            logger ?? NullLogger.Instance);
    }

    [Fact]
    public void ResolveEffectiveShell_DefaultsToSandboxCmd_WhenSandboxEnabled()
    {
        var fallback = new FakeCommandRunner { EffectiveShellForNull = "pwsh" };
        var runner = NewRunner(new FakeSandboxExecutor(), fallback, NewSettings(sandboxEnabled: true));

        Assert.Equal("cmd", runner.ResolveEffectiveShell(null));
        Assert.Equal("cmd", runner.ResolveEffectiveShell(" cmd "));
        Assert.Equal("powershell", runner.ResolveEffectiveShell("bash"));
    }

    [Fact]
    public void ResolveEffectiveShell_DelegatesToHost_WhenSandboxDisabled()
    {
        var fallback = new FakeCommandRunner { EffectiveShellForNull = "pwsh" };
        var runner = NewRunner(new FakeSandboxExecutor(), fallback, NewSettings(sandboxEnabled: false));

        Assert.Equal("pwsh", runner.ResolveEffectiveShell(null));
        Assert.Equal("powershell", runner.ResolveEffectiveShell(" powershell "));
        Assert.Equal("powershell", runner.ResolveEffectiveShell("bash"));
    }

    [Fact]
    public void ResolveEffectiveShell_DelegatesToHost_WhenMxcUnavailableAndCompatibilityFallbackEnabled()
    {
        var fallback = new FakeCommandRunner { EffectiveShellForNull = "pwsh" };
        var runner = NewRunner(
            new FakeSandboxExecutor(),
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false),
            sandboxAvailable: false);

        Assert.Equal("pwsh", runner.ResolveEffectiveShell(null));
        Assert.Equal("powershell", runner.ResolveEffectiveShell(" powershell "));
        Assert.Equal("powershell", runner.ResolveEffectiveShell("bash"));
    }

    [Fact]
    public void ResolveEffectiveShell_UsesSandboxShell_WhenStrictFallbackBlockingEnabledAndMxcUnavailable()
    {
        var fallback = new FakeCommandRunner { EffectiveShellForNull = "pwsh" };
        var runner = NewRunner(
            new FakeSandboxExecutor(),
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: true),
            sandboxAvailable: false);

        Assert.Equal("cmd", runner.ResolveEffectiveShell(null));
        Assert.Equal("powershell", runner.ResolveEffectiveShell(" powershell "));
        Assert.Equal("powershell", runner.ResolveEffectiveShell("bash"));
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_FallsBackWhenExecutorIsUnavailableAndCompatibilityFallbackEnabled()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "test reason" };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host-ran" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "powershell" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host-ran", result.Stdout);
        Assert.Equal(NodeToolExecutionMode.HostFallback, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.None, result.ErrorCategory);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_OmittedShellFallsBackToApprovedHostDefaultWhenExecutorIsUnavailable()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "test reason" };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host-ran" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false));

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hi",
            ApprovedEffectiveShell = "cmd",
            ApprovedHostFallbackShell = "powershell",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host-ran", result.Stdout);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DeniesOmittedShellFallbackWhenHostDefaultWasNotApproved()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "test reason" };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host-ran" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("without prior approval", result.Stderr);
        Assert.Equal(
            NodeToolSandboxDenialReason.FallbackShellUnapproved,
            result.SandboxDenialReason);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_DeniesWhenEffectiveShellDriftsAfterApproval()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: true),
            sandboxAvailable: true);

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hi",
            ApprovedEffectiveShell = "powershell",
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("effective shell changed after approval", result.Stderr);
        Assert.Equal(
            NodeToolSandboxDenialReason.EffectiveShellChanged,
            result.SandboxDenialReason);
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxDisabled_AlwaysRoutesToHost()
    {
        var executor = new FakeSandboxExecutor(); // healthy
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var availabilityChecks = 0;
        var runner = new MxcCommandRunner(
            executor,
            fallback,
            () => NewSettings(sandboxEnabled: false),
            () => "C:\\test\\settings",
            () =>
            {
                availabilityChecks++;
                return true;
            },
            invalidateAvailability: null,
            NullLogger.Instance);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal("host", result.Stdout);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal(0, availabilityChecks);
        // Executor must not have been touched.
        Assert.Null(executor.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_RejectsCustomEnvWithoutHostFallback()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hi",
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("custom environment variables", result.Stderr);
        Assert.Equal(
            NodeToolSandboxDenialReason.CustomEnvironmentUnsupported,
            result.SandboxDenialReason);
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_MxcUnavailable_RejectsCustomEnvWithoutHostFallback()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false),
            sandboxAvailable: false);

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hi",
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("custom environment variables", result.Stderr);
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_MxcUnavailable_RoutesToHost_WithSandboxToggleOff()
    {
        // Explicit sandbox opt-out means host execution is intentional, even on
        // hosts where MXC is unavailable.
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: false),
            sandboxAvailable: false);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host", result.Stdout);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
        Assert.NotNull(fallback.LastRequest);
        Assert.Null(executor.LastRequest);
    }

    [Fact]
    public async Task RunAsync_MxcUnavailable_RoutesToHost_WhenCompatibilityFallbackEnabled()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "MXC missing" };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false),
            sandboxAvailable: false);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host", result.Stdout);
        Assert.Equal(NodeToolExecutionMode.HostFallback, result.ExecutionMode);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
        Assert.Null(executor.LastRequest);
    }

    [Fact]
    public async Task RunAsync_MxcUnavailable_Denies_WithStrictFallbackBlocking()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: true),
            sandboxAvailable: false);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.SandboxUnavailable, result.ErrorCategory);
        Assert.Contains("host fallback is blocked", result.Stderr);
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_Success_MapsSandboxResultIntoCommandResult()
    {
        var executor = new FakeSandboxExecutor
        {
            Result = new SandboxExecutionResult(
                ExitCode: 0,
                Stdout: "hello world",
                Stderr: string.Empty,
                TimedOut: false,
                DurationMs: 123,
                ContainmentTag: "mxc"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Get-Process",
            Shell = "powershell",
            Cwd = "C:\\",
            TimeoutMs = 5000,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.None, result.ErrorCategory);
        Assert.Equal("hello world", result.Stdout);
        Assert.Equal(123, result.DurationMs);
        Assert.False(result.TimedOut);

        // Sandbox request should carry the capability + command + shell.
        Assert.NotNull(executor.LastRequest);
        Assert.Equal("system.run", executor.LastRequest!.CapabilityCommand);
        var args = executor.LastRequest.Args;
        Assert.Equal("Get-Process", args.GetProperty("command").GetString());
        Assert.Equal("powershell", args.GetProperty("shell").GetString());
        Assert.Equal(5000, executor.LastRequest.TimeoutMs);
    }

    [Fact]
    public async Task RunAsync_DefaultShell_UsesCmdForMxcProcessContainer()
    {
        var executor = new FakeSandboxExecutor
        {
            Result = new SandboxExecutionResult(
                ExitCode: 0,
                Stdout: "hello",
                Stderr: string.Empty,
                TimedOut: false,
                DurationMs: 1,
                ContainmentTag: "mxc"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        await runner.RunAsync(new CommandRequest { Command = "echo hello" });

        Assert.NotNull(executor.LastRequest);
        var args = executor.LastRequest!.Args;
        Assert.Equal("cmd", args.GetProperty("shell").GetString());
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DoesNotFallBack_OnSandboxFailure()
    {
        // SandboxUnavailableException is the only exception that triggers the fallback path.
        // A normal failed exec inside the sandbox propagates as an error CommandResult.
        var executor = new FakeSandboxExecutor
        {
            Result = new SandboxExecutionResult(
                ExitCode: 7,
                Stdout: string.Empty,
                Stderr: "sandboxed command failed",
                TimedOut: false,
                DurationMs: 1,
                ContainmentTag: "mxc"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "fail-me" });

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.CommandFailed, result.ErrorCategory);
        Assert.Contains("sandboxed command failed", result.Stderr);
        // Fallback must NOT have been used.
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxUnavailableException_InvalidatesAvailabilityCacheAndFallsBack()
    {
        // When the executor throws SandboxUnavailableException at runtime the
        // runner invokes its invalidate-availability callback and preserves
        // the compatible host fallback path for this call.
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "wxc-exec went missing" };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var invalidationCount = 0;
        var runner = new MxcCommandRunner(
            executor,
            fallback,
            () => NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false),
            () => "C:\\test\\settings",
            () => true,
            invalidateAvailability: () => invalidationCount++,
            NullLogger.Instance);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "powershell" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host", result.Stdout);
        Assert.Equal(1, invalidationCount);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
    }

    [Fact]
    public async Task RunAsync_CustomEnv_RejectsBeforeReprobeOrHostFallback()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var sandboxAvailable = true;
        var invalidationCount = 0;
        var runner = new MxcCommandRunner(
            executor,
            fallback,
            () => NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false),
            () => "C:\\test\\settings",
            () => sandboxAvailable,
            invalidateAvailability: () =>
            {
                invalidationCount++;
                sandboxAvailable = false;
            },
            NullLogger.Instance);

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hi",
            Shell = "powershell",
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("custom environment variables", result.Stderr);
        Assert.Equal(0, invalidationCount);
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxUnavailableException_Denies_WhenStrictFallbackBlockingEnabled()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "wxc-exec went missing" };
        var fallback = new FakeCommandRunner();
        var invalidationCount = 0;
        var runner = new MxcCommandRunner(
            executor,
            fallback,
            () => NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: true),
            () => "C:\\test\\settings",
            () => true,
            invalidateAvailability: () => invalidationCount++,
            NullLogger.Instance);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("host fallback is blocked", result.Stderr);
        Assert.Equal(1, invalidationCount);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_GenericException_ReturnsDeny_DoesNotPropagate()
    {
        // The catch-all in RunAsync handles unexpected bridge/JSON/IO failures by
        // returning a -1 CommandResult instead of letting the exception escape.
        // Without this, a bridge crash could take down the node loop.
        var executor = new FakeSandboxExecutor
        {
            ThrowsArbitrary = new InvalidOperationException("bridge JSON parse error"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("bridge JSON parse error", result.Stderr);
        Assert.Contains("InvalidOperationException", result.Stderr);
        // Unexpected executor errors must not become host execution.
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_PowerShellUiUnsupported_DeniesEvenWhenCompatibilityFallbackEnabled()
    {
        var executor = new FakeSandboxExecutor
        {
            ThrowsArbitrary = new NotSupportedException("PowerShell-family shells require UI access"),
        };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: true, blockHostFallbackWhenMxcUnavailable: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "Write-Output hi", Shell = "powershell" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cannot execute PowerShell-family shells", result.Stderr);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_PowerShellUiUnsupported_DeniesWhenStrictFallbackBlockingEnabled()
    {
        var executor = new FakeSandboxExecutor
        {
            ThrowsArbitrary = new NotSupportedException("PowerShell-family shells require UI access"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: true, blockHostFallbackWhenMxcUnavailable: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "Write-Output hi", Shell = "powershell" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cannot execute PowerShell-family shells", result.Stderr);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_OtherNotSupportedException_ReturnsExplicitDeny_DoesNotFallBack()
    {
        var executor = new FakeSandboxExecutor
        {
            ThrowsArbitrary = new NotSupportedException("Explicit environment variables are not supported by the Windows MXC 0.7 processcontainer backend."),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Explicit environment variables are not supported", result.Stderr);
        Assert.Equal(
            NodeToolSandboxDenialReason.UnsupportedSandboxRequest,
            result.SandboxDenialReason);
        Assert.DoesNotContain("unexpected", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_OperationCanceled_Propagates()
    {
        // OperationCanceledException is the ONE exception type that propagates.
        // The catch-all would otherwise swallow it and the caller would see a
        // -1 result instead of the actual cancellation.
        var executor = new FakeSandboxExecutor
        {
            ThrowsArbitrary = new OperationCanceledException("caller cancelled"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(new CommandRequest { Command = "echo hi" }));
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DirectArgv_FailsClosed_DoesNotSerializeLegacy()
    {
        // A direct-argv request cannot be carried by the sandbox protocol yet, so the
        // runner must fail closed rather than silently serialize the legacy command
        // fields and run something other than the approved argv.
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner { Result = new CommandResult { ExitCode = 0, Stdout = "host" } };
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest
        {
            Argv = new[] { @"C:\Windows\System32\whoami.exe" },
            Command = "should-be-ignored",
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(
            NodeToolSandboxDenialReason.DirectArgvUnsupported,
            result.SandboxDenialReason);
        // Neither the sandbox executor nor the host fallback ran the command.
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxUnavailable_DirectArgv_FallsBackToHostThatHonorsArgv()
    {
        // When the sandbox is unavailable the request routes to the host runner, which
        // does honor Argv. The fail-closed guard must not interfere with that path.
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner { Result = new CommandResult { ExitCode = 0, Stdout = "host" } };
        var runner = NewRunner(
            executor, fallback, NewSettings(sandboxEnabled: true), sandboxAvailable: false);

        var argv = new[] { @"C:\Windows\System32\whoami.exe" };
        var result = await runner.RunAsync(new CommandRequest { Argv = argv });

        Assert.Equal("host", result.Stdout);
        Assert.NotNull(fallback.LastRequest);
        Assert.Same(argv, fallback.LastRequest!.Argv);
        Assert.Null(executor.LastRequest);
    }

    private sealed class FakeSandboxExecutor : ISandboxExecutor
    {
        public string Name => "fake";
        public bool IsContained => true;

        public SandboxExecutionRequest? LastRequest { get; private set; }
        public SandboxExecutionResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, 0, "mxc");
        public bool ThrowsUnavailable { get; set; }
        public string UnavailableReason { get; set; } = "fake unavailable";
        public Exception? ThrowsArbitrary { get; set; }

        public Task<SandboxExecutionResult> ExecuteAsync(
            SandboxExecutionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowsArbitrary != null)
                throw ThrowsArbitrary;
            if (ThrowsUnavailable)
                throw new SandboxUnavailableException(UnavailableReason);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public string Name => "fake-host";
        public CommandRequest? LastRequest { get; private set; }
        public CommandResult Result { get; set; } = new() { ExitCode = 0, Stdout = string.Empty };
        public string EffectiveShellForNull { get; set; } = "powershell";

        public string ResolveEffectiveShell(string? requestedShell)
        {
            if (string.IsNullOrWhiteSpace(requestedShell))
                return EffectiveShellForNull;

            return requestedShell.Trim().ToLowerInvariant() switch
            {
                "cmd" => "cmd",
                "pwsh" => "pwsh",
                "powershell" => "powershell",
                _ => "powershell",
            };
        }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class CapturingLogger : IOpenClawLogger
    {
        public List<string> DebugMessages { get; } = new();

        public void Info(string message) { }
        public void Debug(string message) => DebugMessages.Add(message);
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    [Theory]
    [InlineData(0, null, 0)]              // both unset → 0 (no cap)
    [InlineData(30_000, null, 30_000)]    // agent only
    [InlineData(0, 60_000, 60_000)]       // policy only
    [InlineData(30_000, 60_000, 30_000)]  // agent smaller → use agent
    [InlineData(90_000, 60_000, 60_000)]  // policy smaller → use policy (sandbox cap wins)
    [InlineData(-1, 60_000, 60_000)]      // negative agent treated as no cap
    public void CombineTimeouts_TakesMinOfAgentAndPolicy(int agentMs, int? policyMs, int expected)
    {
        Assert.Equal(expected, MxcCommandRunner.CombineTimeouts(agentMs, policyMs));
    }

    [Fact]
    public async Task RunAsync_PassesMaxOutputBytesToExecutor()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var settings = NewSettings(sandboxEnabled: true);
        settings.SandboxMaxOutputBytes = 16 * 1024 * 1024;
        var runner = NewRunner(executor, fallback, settings);

        await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.NotNull(executor.LastRequest);
        Assert.Equal(16L * 1024L * 1024L, executor.LastRequest!.MaxOutputBytes);
    }

    [Fact]
    public async Task RunAsync_SandboxRequestUsesNormalizedEffectiveShellForUnsupportedExplicitShell()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "bash" });

        Assert.NotNull(executor.LastRequest);
        Assert.Equal("powershell", executor.LastRequest!.Args.GetProperty("shell").GetString());
        Assert.False(executor.LastRequest.Policy.Ui!.AllowWindows);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task RunAsync_SandboxRequestKeepsUiDeniedForPowerShellFamilyShells(string shell)
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        await runner.RunAsync(new CommandRequest { Command = "Write-Output hi", Shell = shell });

        Assert.NotNull(executor.LastRequest);
        Assert.False(executor.LastRequest!.Policy.Ui!.AllowWindows);
    }

    [Fact]
    public async Task RunAsync_SandboxRequestKeepsUiDeniedForCmdShell()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "cmd" });

        Assert.NotNull(executor.LastRequest);
        Assert.False(executor.LastRequest!.Policy.Ui!.AllowWindows);
    }

    [Fact]
    public async Task RunAsync_HostFallbackUsesNormalizedEffectiveShellForUnsupportedExplicitShell()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: true, blockHostFallbackWhenMxcUnavailable: false),
            sandboxAvailable: false);

        await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "bash" });

        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
    }

    [Fact]
    public async Task RunAsync_LogsRedactedSandboxSettingsAndPolicySummary()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var settings = NewSettings(sandboxEnabled: true);
        settings.SystemRunAllowOutbound = true;
        settings.SandboxClipboard = SandboxClipboardMode.Both;
        settings.SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly;
        settings.SandboxCustomFolders = new()
        {
            new SandboxCustomFolder { Path = "C:\\Code\\repo", Access = SandboxFolderAccess.ReadWrite },
        };
        var logger = new CapturingLogger();
        var runner = NewRunner(executor, fallback, settings, logger: logger);

        await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        var requestLog = Assert.Single(logger.DebugMessages, m => m.Contains("system.run sandbox request", StringComparison.Ordinal));
        Assert.Contains("sandboxSettings={enabled=True", requestLog);
        Assert.Contains("allowOutbound=True", requestLog);
        Assert.Contains("clipboard=Both", requestLog);
        Assert.Contains("customFolderCount=1", requestLog);
        Assert.Contains("settingsDirectoryPath=<set>", requestLog);
        Assert.Contains("policy={readonlyCount=", requestLog);
        Assert.Contains("readwriteCount=1", requestLog);
        Assert.Contains("networkAllowOutbound=True", requestLog);
        Assert.DoesNotContain("sandboxSettingsJson=", requestLog);
        Assert.DoesNotContain("policyJson=", requestLog);
        Assert.DoesNotContain("C:\\Code\\repo", requestLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\test\\settings", requestLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_LogsFullSandboxSettingsAndPolicy_WhenFullConfigDiagnosticsEnabled()
    {
        var previous = Environment.GetEnvironmentVariable(DirectAppContainerExecutor.LogFullConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DirectAppContainerExecutor.LogFullConfigEnvVar, "1");
            var executor = new FakeSandboxExecutor();
            var fallback = new FakeCommandRunner();
            var settings = NewSettings(sandboxEnabled: true);
            settings.SystemRunAllowOutbound = true;
            settings.SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = "C:\\Code\\repo", Access = SandboxFolderAccess.ReadWrite },
            };
            var logger = new CapturingLogger();
            var runner = NewRunner(executor, fallback, settings, logger: logger);

            await runner.RunAsync(new CommandRequest { Command = "echo hi" });

            var fullLog = Assert.Single(logger.DebugMessages, m => m.Contains("system.run sandbox request (full)", StringComparison.Ordinal));
            Assert.Contains("sandboxSettingsJson=", fullLog);
            Assert.Contains("policyJson=", fullLog);
            Assert.Contains("\"path\":\"C:\\\\Code\\\\repo\"", fullLog);
            Assert.Contains("\"readwritePaths\":[\"C:\\\\Code\\\\repo\"", fullLog);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DirectAppContainerExecutor.LogFullConfigEnvVar, previous);
        }
    }

    [Fact]
    public async Task RunAsync_PolicyTimeoutCapsAgentTimeout()
    {
        var executor = new FakeSandboxExecutor();
        var fallback = new FakeCommandRunner();
        var settings = NewSettings(sandboxEnabled: true);
        settings.SandboxTimeoutMs = 10_000; // sandbox cap is 10s
        var runner = NewRunner(executor, fallback, settings);

        // Agent asks for 60s; policy caps to 10s.
        await runner.RunAsync(new CommandRequest { Command = "echo hi", TimeoutMs = 60_000 });

        Assert.NotNull(executor.LastRequest);
        Assert.Equal(10_000, executor.LastRequest!.TimeoutMs);
    }

    [Fact]
    public async Task RunAsync_UnavailableExecutor_FallsBackToHost_WhenCompatibilityFallbackEnabled()
    {
        var executor = new FakeSandboxExecutor
        {
            ThrowsUnavailable = true,
            UnavailableReason = "test: MXC not installed",
        };
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(
                sandboxEnabled: true,
                blockHostFallbackWhenMxcUnavailable: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi", Shell = "powershell" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("host", result.Stdout);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("powershell", fallback.LastRequest!.Shell);
    }
}
