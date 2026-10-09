using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Mxc;

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
        => new()
        {
            Id = id,
            Command = "system.run",
            Args = Parse("""{"command":["cmd.exe","/d","/s","/c","echo hello"]}""")
        };

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
    // 3. Configured V2 handler is used for every request
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
    public async Task V2Path_DoesNotExecuteWhenApprovalIsUnavailable()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(ExecApprovalV2NullHandler.Instance);

        await cap.ExecuteAsync(RunRequest());

        Assert.Null(runner.LastRequest);
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
        Assert.Null(runner.LastRequest);
    }

    // -------------------------------------------------------------------------
    // 6–9. Observability: correlation ID, selected path, decision, reason logged
    // -------------------------------------------------------------------------

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
    // Approved execution: allow results execute the approved payload
    // -------------------------------------------------------------------------

    private static ExecApprovedExecution ApprovedEcho()
        => new(new[] { "cmd", "/c", "echo hi" }, cwd: @"C:\work", timeoutMs: 5000,
            env: null);

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
        Assert.Null(runner.LastRequest.Env);
        Assert.True(logger.HasInfoContaining("path=v2 executed exit=0"),
            "execution outcome not logged on V2 path");
    }

    [Fact]
    public async Task CallerToken_ReachesAuthorizationRevalidationAndExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner();
        var handler = new FixedResultHandler(ExecApprovalV2Result.Allow(ApprovedEcho()));
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(handler);
        var response = await cap.ExecuteAsync(RunRequest(), cancellation.Token);
        Assert.True(response.Ok);
        Assert.Equal(cancellation.Token, handler.AuthorizationToken);
        Assert.Equal(cancellation.Token, handler.RevalidationToken);
        Assert.Equal(cancellation.Token, runner.LastToken);
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
        Assert.Equal(ApprovedEcho().Argv, runner.LastRequest!.Argv);
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

    [Fact]
    public async Task V2Allow_RevalidationRejects_DoesNotInvokeRunner()
    {
        var runner = new FakeRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(new FixedResultHandler(
            ExecApprovalV2Result.Allow(ApprovedEchoNoEnv()),
            ExecApprovalRevalidationResult.NotCurrent("policy-changed-before-execution")));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
        Assert.Contains("policy-changed-before-execution", res.Error);
        Assert.Null(runner.LastRequest);
    }

    // -------------------------------------------------------------------------
    // Mandatory containment: unavailable SDK is a protocol error, never a host route.
    // -------------------------------------------------------------------------

    private static ExecApprovedExecution ApprovedEchoNoEnv()
        => new(new[] { "cmd", "/c", "echo hi" }, cwd: @"C:\work", timeoutMs: 5000, env: null);

    [Fact]
    public async Task V2Allow_SdkUnavailable_IsProtocolErrorNotSuccessShapedPayload()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new MxcCommandRunner(() => new(), () => Environment.CurrentDirectory,
            () => new(false, false, false, ["Repair MXC."])));
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.Allow(ApprovedEchoNoEnv())));
        var response = await cap.ExecuteAsync(RunRequest());
        Assert.False(response.Ok);
        Assert.Null(response.Payload);
        Assert.Contains("Repair MXC", response.Error);
        Assert.Equal(OpenClaw.Shared.Telemetry.NodeToolErrorCategory.SandboxUnavailable, response.Diagnostic!.ErrorCategory);
    }

    [Fact]
    public async Task V2_RunnerWithoutArgvSupportContract_IsNeverGated()
    {
        // The existing ICommandRunner seam still dispatches every request through approvals.
        var runner = new FakeRunner();
        var handler = new TrackingHandler();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);
        cap.SetV2Handler(handler);

        await cap.ExecuteAsync(RunRequest());

        Assert.True(handler.WasCalled);
    }

    [Fact]
    public async Task V2Deny_WithRealMxcRunner_NeverReachesAnyTransport()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new MxcCommandRunner(() => new(), () => Environment.CurrentDirectory,
            () => throw new Exception("A denied command must not even probe.")));
        cap.SetV2Handler(new FixedResultHandler(ExecApprovalV2Result.SecurityDeny("blocked")));

        var res = await cap.ExecuteAsync(RunRequest());

        Assert.False(res.Ok);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class FakeRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
        {
            LastRequest = request;
            LastToken = ct;
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

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(ExecApprovalV2Result.Unavailable());
        }
    }

    private sealed class FixedResultHandler : IExecApprovalV2Handler
    {
        private readonly ExecApprovalV2Result _result;
        private readonly ExecApprovalRevalidationResult _revalidation;
        public CancellationToken AuthorizationToken { get; private set; }
        public CancellationToken RevalidationToken { get; private set; }

        public FixedResultHandler(
            ExecApprovalV2Result result,
            ExecApprovalRevalidationResult? revalidation = null)
        {
            _result = result;
            _revalidation = revalidation ?? ExecApprovalRevalidationResult.Current;
        }

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId, CancellationToken cancellationToken = default)
        {
            AuthorizationToken = cancellationToken;
            return Task.FromResult(_result);
        }

        public ValueTask<ExecApprovalRevalidationResult> RevalidateAsync(
            ExecApprovedExecution execution,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            RevalidationToken = cancellationToken;
            return ValueTask.FromResult(_revalidation);
        }
    }

    private sealed class ThrowingHandler : IExecApprovalV2Handler
    {
        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("handler exploded");
    }

    private sealed class CapturingCorrelationHandler : IExecApprovalV2Handler
    {
        private readonly Action<string> _capture;
        public CapturingCorrelationHandler(Action<string> capture) => _capture = capture;

        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId, CancellationToken cancellationToken = default)
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
