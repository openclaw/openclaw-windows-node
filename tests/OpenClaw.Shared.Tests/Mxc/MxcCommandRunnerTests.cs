using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcCommandRunnerTests
{
    private static SettingsData NewSettings(bool sandboxEnabled = true)
    {
        return new SettingsData
        {
            SystemRunSandboxEnabled = sandboxEnabled,
            SystemRunAllowOutbound = false,
        };
    }

    private static MxcCommandRunner NewRunner(
        ISandboxExecutor executor,
        ICommandRunner hostFallback,
        SettingsData settings,
        bool sandboxAvailable = true)
    {
        return new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => "C:\\test\\settings",
            () => sandboxAvailable,
            invalidateAvailability: null,
            NullLogger.Instance);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DeniesWhenSandboxUnavailable()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "test reason" };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Sandboxing is enabled", result.Stderr);
        Assert.Contains("test reason", result.Stderr);
        // Fallback must NOT have been called.
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
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal("host", result.Stdout);
        Assert.NotNull(fallback.LastRequest);
        // Executor must not have been touched.
        Assert.Null(executor.LastRequest);
    }

    [Fact]
    public async Task RunAsync_MxcUnavailable_BlocksEvenWithSandboxToggleOff()
    {
        // The UI hides the toggle when MXC is unavailable. A persisted toggle=OFF
        // (from a previous run or different machine) must NOT cause the runner to
        // silently route to host — the page says "commands blocked" and the
        // runner must match that promise.
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

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("unavailable", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", result.Stderr, StringComparison.OrdinalIgnoreCase);
        // Neither the sandbox executor nor the host fallback should have run.
        Assert.Null(executor.LastRequest);
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_MxcUnavailable_BlocksEvenWithSandboxToggleOn()
    {
        // Same as the toggle-off variant but with toggle=ON. The unavailability
        // short-circuit should fire BEFORE we get to the executor path.
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "MXC missing" };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(
            executor,
            fallback,
            NewSettings(sandboxEnabled: true),
            sandboxAvailable: false);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("unavailable", result.Stderr, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunAsync_SandboxEnabled_DoesNotFallBack_OnSandboxFailure()
    {
        // SandboxUnavailableException is the only exception that triggers the deny path.
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
        Assert.Contains("sandboxed command failed", result.Stderr);
        // Fallback must NOT have been used.
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxUnavailableException_InvalidatesAvailabilityCache()
    {
        // When the executor throws SandboxUnavailableException, the runner should
        // invoke its invalidate-availability callback so the next command re-probes.
        // Handles the case where MXC components were removed between this NodeService
        // starting up and the agent invoking a command.
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "wxc-exec went missing" };
        var fallback = new FakeCommandRunner();
        var invalidationCount = 0;
        var runner = new MxcCommandRunner(
            executor,
            fallback,
            () => NewSettings(sandboxEnabled: true),
            () => "C:\\test\\settings",
            () => true,
            invalidateAvailability: () => invalidationCount++,
            NullLogger.Instance);

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(1, invalidationCount);
        Assert.Contains("wxc-exec went missing", result.Stderr);
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
        // Host fallback must NOT have been touched — fail closed, not fallback.
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
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
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
    public async Task RunAsync_UnavailableExecutor_DeniesWithReason()
    {
        var executor = new UnavailableSandboxExecutor("test: MXC not installed");
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("MXC not installed", result.Stderr);
        Assert.Null(fallback.LastRequest); // never delegated to host
    }
}
