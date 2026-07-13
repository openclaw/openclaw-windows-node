using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for PR1: routing seam, null handler, and minimum observability.
/// Verifies invariants from rails 1, 2, 3, 7, 19.
/// </summary>
public class ExecApprovalV2RoutingTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static NodeInvokeRequest RunRequest(string id = "r1")
        => new() { Id = id, Command = "system.run", Args = Parse("""{"command":"echo hello"}""") };

    // -------------------------------------------------------------------------
    // 1. ExecApprovalV2Result — all 6 codes constructible (rail 7)
    // -------------------------------------------------------------------------

    [Fact]
    public void V2Result_AllSixCodesConstructible()
    {
        var r1 = ExecApprovalV2Result.Unavailable("test");
        var r2 = ExecApprovalV2Result.SecurityDeny("test");
        var r3 = ExecApprovalV2Result.AllowlistMiss("test");
        var r4 = ExecApprovalV2Result.UserDenied("test");
        var r5 = ExecApprovalV2Result.ValidationFailed("test");
        var r6 = ExecApprovalV2Result.ResolutionFailed("test");

        Assert.Equal(ExecApprovalV2Code.Unavailable, r1.Code);
        Assert.Equal(ExecApprovalV2Code.SecurityDeny, r2.Code);
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, r3.Code);
        Assert.Equal(ExecApprovalV2Code.UserDenied, r4.Code);
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, r5.Code);
        Assert.Equal(ExecApprovalV2Code.ResolutionFailed, r6.Code);
    }

    [Fact]
    public void V2Result_CarriesReason()
    {
        var result = ExecApprovalV2Result.SecurityDeny("blocked by policy");
        Assert.Equal("blocked by policy", result.Reason);
    }

    [Fact]
    public void V2Result_DefaultUnavailableReason()
    {
        var result = ExecApprovalV2Result.Unavailable();
        Assert.Equal(ExecApprovalV2Code.Unavailable, result.Code);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void V2Result_ToString_IncludesCodeAndReason()
    {
        var result = ExecApprovalV2Result.SecurityDeny("access denied");
        var text = result.ToString();
        Assert.Contains("SecurityDeny", text);
        Assert.Contains("access denied", text);
    }

    // -------------------------------------------------------------------------
    // 2. NullHandler — always unavailable, never throws (rail 1, 19)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NullHandler_ReturnsUnavailable_NotException()
    {
        var handler = ExecApprovalV2NullHandler.Instance;
        var result = await handler.HandleAsync(RunRequest(), "corr01");
        Assert.Equal(ExecApprovalV2Code.Unavailable, result.Code);
    }

    [Fact]
    public async Task NullHandler_DoesNotThrow()
    {
        var handler = ExecApprovalV2NullHandler.Instance;
        var ex = await Record.ExceptionAsync(() => handler.HandleAsync(RunRequest(), "corr02"));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // 3. Legacy path unchanged when _v2Handler is null (rail 3, 19)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LegacyPath_UsedWhen_V2HandlerIsNull()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        // No SetV2Handler — legacy must run

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest); // runner was called → legacy path
    }

    [Fact]
    public async Task LegacyPath_DenyPolicy_StillDenies_WhenNoV2Handler()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pr1test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var logger = new CapturingLogger();
            var policy = new ExecApprovalPolicy(tempDir, logger);
            policy.SetRules(new[] { new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Deny } },
                ExecApprovalAction.Deny);

            var cap = new SystemCapability(logger);
            cap.SetCommandRunner(new FakeRunner());
            cap.SetApprovalPolicy(policy);
            // No SetV2Handler

            var res = await cap.ExecuteAsync(RunRequest());

            Assert.False(res.Ok);
            Assert.Contains("denied", res.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // 4. V2 path entered when handler is set; legacy NOT invoked (rail 2, 3)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task V2Path_EntersHandlerWhenSet()
    {
        var trackingHandler = new TrackingHandler();
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(trackingHandler);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(trackingHandler.WasCalled); // V2 path called
    }

    [Fact]
    public async Task V2Path_DoesNotCallLegacyRunner()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.Null(runner.LastRequest); // runner was NOT called
    }

    // -------------------------------------------------------------------------
    // 5. No silent fallback (rail 1)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task V2Path_UnavailableResult_IsTypedError_NotSilentAllow()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("unavailable", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V2Path_SecurityDenyResult_IsTypedError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.SecurityDeny("blocked")));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("SecurityDeny", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V2Path_HandlerException_IsTypedError_NotSilentFallback()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(new ThrowingHandler());

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("exec-approvals-v2", res.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastRequest); // no silent fallback to legacy
    }

    // -------------------------------------------------------------------------
    // 6–9. Observability: correlation ID, selected path, decision, reason logged
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Observability_LegacyPath_LogsCorrelationId()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("corr="), "correlation ID not logged");
    }

    [Fact]
    public async Task Observability_LegacyPath_LogsPathLegacy()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("path=legacy"), "selected path not logged as 'legacy'");
    }

    [Fact]
    public async Task Observability_LegacyPath_LogsDecisionLegacy()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("decision=legacy"), "decision not logged as 'legacy'");
    }

    [Fact]
    public async Task Observability_LegacyPath_LogsReasonLegacy()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("reason=legacy"), "reason code not logged as 'legacy'");
    }

    [Fact]
    public async Task Observability_V2Path_LogsCorrelationId()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("corr="), "correlation ID not logged on V2 path");
    }

    [Fact]
    public async Task Observability_V2Path_LogsPathV2()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("path=v2"), "selected path not logged as 'v2'");
    }

    [Fact]
    public async Task Observability_V2Path_LogsDecisionCode()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("decision=Unavailable"), "decision code not logged");
    }

    [Fact]
    public async Task Observability_V2Path_LogsReasonCode()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(logger.HasInfoContaining("reason="), "reason code not logged on V2 path");
    }

    // -------------------------------------------------------------------------
    // I-1. CorrelationId propagated to handler equals value logged by routing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CorrelationId_PropagatedToHandler_MatchesLoggedValue()
    {
        var receivedId = (string?)null;
        var handler = new CapturingCorrelationHandler(id => receivedId = id);
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(new FakeRunner());
        cap.SetV2Handler(handler);

        await cap.ExecuteAsync(RunRequest());

        Assert.NotNull(receivedId);
        // The exact correlationId the handler received must appear in the routing log.
        Assert.True(logger.HasInfoContaining($"corr={receivedId}"),
            $"correlationId '{receivedId}' received by handler was not found in routing logs");
    }

    // -------------------------------------------------------------------------
    // I-2. Legacy path with null runner — no V2 activation, error unchanged
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LegacyPath_NullRunner_NoV2Handler_ReturnsNotAvailableError()
    {
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        // Neither SetCommandRunner nor SetV2Handler called — legacy path, runner null

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(logger.HasInfoContaining("path=v2"), "V2 path must not activate when no handler is set");
    }

    // -------------------------------------------------------------------------
    // Approved execution: allow results execute the approved payload
    // -------------------------------------------------------------------------

    private static ExecApprovedExecution ApprovedEcho()
        => new(new[] { "cmd", "/c", "echo hi" }, cwd: @"C:\work", timeoutMs: 5000,
            env: new Dictionary<string, string> { ["FOO"] = "bar" });

    [Fact]
    public async Task V2Allow_ExecutesApprovedArgv_WithLegacyResponseShape()
    {
        var runner = new FakeRunner();
        var logger = new CapturingLogger();
        var cap = new SystemCapability(logger);
        cap.SetCommandRunner(runner);
        var approved = ApprovedEcho();
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.Allow(approved)));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal(approved.Argv, runner.LastRequest!.Argv);
        Assert.Equal(approved.Cwd, runner.LastRequest.Cwd);
        Assert.Equal(approved.TimeoutMs, runner.LastRequest.TimeoutMs);
        Assert.Equal("bar", runner.LastRequest.Env!["FOO"]);
        Assert.True(logger.HasInfoContaining("path=v2 executed exit=0"),
            "execution outcome not logged on V2 path");
    }

    [Fact]
    public async Task V2Allow_ShellAndLegacyFieldsDoNotTravel()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.Allow(ApprovedEcho())));

        await cap.ExecuteAsync(RunRequest());

        // The approved argv must reach the runner verbatim: no shell wrapper,
        // no legacy command/args re-derivation from the raw request.
        Assert.NotNull(runner.LastRequest);
        Assert.Empty(runner.LastRequest!.Command);
        Assert.Null(runner.LastRequest.Args);
        Assert.Null(runner.LastRequest.Shell);
    }

    [Fact]
    public async Task V2Allow_NullRunner_ReturnsNotAvailableError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.Allow(ApprovedEcho())));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V2Allow_RunnerException_ReturnsExecutionFailed()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new ThrowingRunner());
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.Allow(ApprovedEcho())));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("Execution failed", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V2Deny_DoesNotInvokeRunner()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.SecurityDeny("blocked")));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Null(runner.LastRequest);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class FakeRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CommandResult { Stdout = "ok", ExitCode = 0 });
        }
    }

    private sealed class ThrowingRunner : ICommandRunner
    {
        public string Name => "throwing";

        public Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
            => throw new InvalidOperationException("runner exploded");
    }

    private sealed class TrackingHandler : IExecApprovalV2Handler
    {
        public bool WasCalled { get; private set; }

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
        {
            WasCalled = true;
            return Task.FromResult(ExecApprovalV2Result.Unavailable());
        }
    }

    private sealed class FixedResultHandler : IExecApprovalV2Handler
    {
        private readonly ExecApprovalV2Result _result;
        public FixedResultHandler(ExecApprovalV2Result result) => _result = result;

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingHandler : IExecApprovalV2Handler
    {
        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
            => throw new InvalidOperationException("handler exploded");
    }

    private sealed class CapturingCorrelationHandler : IExecApprovalV2Handler
    {
        private readonly Action<string> _capture;
        public CapturingCorrelationHandler(Action<string> capture) => _capture = capture;

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
        {
            _capture(correlationId);
            return Task.FromResult(ExecApprovalV2Result.Unavailable());
        }
    }

    private sealed class CapturingLogger : IOpenClawLogger
    {
        private readonly List<string> _infoMessages = new();

        public bool HasInfoContaining(string text)
            => _infoMessages.Exists(m => m.Contains(text, StringComparison.OrdinalIgnoreCase));

        public void Info(string message) => _infoMessages.Add(message);
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
