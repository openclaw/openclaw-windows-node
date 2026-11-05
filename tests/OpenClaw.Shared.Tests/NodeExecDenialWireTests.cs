using System.Text.Json;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

[Trait("Category", "Integration")]
[Collection("MxcOwnership")]
public sealed class NodeExecDenialWireTests(ITestOutputHelper output)
{
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, "system.run")]
    [InlineData(true, "system.run")]
    [InlineData(false, "system.run.prepare")]
    [InlineData(true, "system.run.prepare")]
    public async Task PermissionDisabledAfterRegistration_PreservesDefiniteRefusalOnWire(
        bool gatewayEvent, string nodeCommand)
    {
        var permissionGranted = true;
        var handler = new ApprovalHandler();
        var runner = new RecordingRunner();
        var response = await InvokeOverSocketAsync(
            gatewayEvent, handler, runner,
            nodeCommand: nodeCommand,
            includeRunCommands: () => permissionGranted,
            afterHandshake: () => permissionGranted = false);

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("disabled by user setting", error.GetProperty("message").GetString());
        Assert.True(error.TryGetProperty("code", out var code),
            "Revoked permission must remain a definite pre-execution refusal.");
        Assert.Equal("SYSTEM_RUN_DENIED", code.GetString());
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData(false, ExecApprovalV2Code.UserDenied)]
    [InlineData(true, ExecApprovalV2Code.UserDenied)]
    [InlineData(false, ExecApprovalV2Code.SecurityDeny)]
    [InlineData(true, ExecApprovalV2Code.SecurityDeny)]
    [InlineData(false, ExecApprovalV2Code.AskDeny)]
    [InlineData(true, ExecApprovalV2Code.AskDeny)]
    [InlineData(false, ExecApprovalV2Code.AllowlistMiss)]
    [InlineData(true, ExecApprovalV2Code.AllowlistMiss)]
    public async Task DefiniteApprovalDenial_PreservesStructuredCodeOnWire(
        bool gatewayEvent,
        ExecApprovalV2Code decision)
    {
        var runner = new RecordingRunner();
        var handler = new ApprovalHandler { Decision = decision };
        var response = await InvokeOverSocketAsync(gatewayEvent, handler, runner);

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains(decision.ToString(), error.GetProperty("message").GetString());
        Assert.True(error.TryGetProperty("code", out var code),
            "A known pre-execution denial must not become an unknown command outcome.");
        Assert.Equal("SYSTEM_RUN_DENIED", code.GetString());
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedApproval_PreservesDenialCodeWithoutRunning(bool gatewayEvent)
    {
        var runner = new RecordingRunner();
        var response = await InvokeOverSocketAsync(
            gatewayEvent,
            new ApprovalHandler { RevalidationCurrent = false },
            runner);

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("policy-changed", error.GetProperty("message").GetString());
        Assert.True(error.TryGetProperty("code", out var code));
        Assert.Equal("SYSTEM_RUN_DENIED", code.GetString());
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnexpectedFailure_IsNotMisreportedAsKnownDenial(
        bool gatewayEvent,
        bool afterDispatch)
    {
        var runner = new RecordingRunner { Throw = afterDispatch };
        var response = await InvokeOverSocketAsync(
            gatewayEvent,
            new ApprovalHandler { Throw = !afterDispatch },
            runner);

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(response.GetProperty("error").TryGetProperty("code", out _));
        Assert.Equal(afterDispatch ? 1 : 0, runner.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRevalidation_IsNotMisreportedAsPolicyDenial(bool gatewayEvent)
    {
        var runner = new RecordingRunner();
        var response = await InvokeOverSocketAsync(
            gatewayEvent,
            new ApprovalHandler { RevalidationFailed = true },
            runner);

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("policy-revalidation-failed", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("code", out _));
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SdkPrelaunchRevalidation_PreservesOnlyConfirmedDenialOnWire(
        bool gatewayEvent, bool unsupportedWindows, bool policyDenied)
    {
        using var temp = new TempDirectory();
        var settings = new SettingsData();
        var handler = new ApprovalHandler
        {
            SuccessfulRevalidations = 1,
            RevalidationCurrent = false,
            RevalidationFailed = !policyDenied
        };
        var runner = new OpenClaw.Shared.Mxc.MxcCommandRunner(
            () => settings, () => Path.Combine(temp.Path, "settings"),
            availabilityProvider: () => unsupportedWindows
                ? new(false, false, false, [], isWindowsUnsupported: true)
                : new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => throw new InvalidOperationException("must not spawn"),
            probe: _ => Mxc.MxcAvailabilityTests.BaseProbe(),
            scratchRoot: Path.Combine(temp.Path, "runs"));

        var response = await InvokeOverSocketAsync(
            gatewayEvent, handler, runner, SystemCommand());

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains(policyDenied ? "approval changed" : "could not be revalidated",
            error.GetProperty("message").GetString());
        Assert.Equal(policyDenied, error.TryGetProperty("code", out var code));
        if (policyDenied)
            Assert.Equal("SYSTEM_RUN_DENIED", code.GetString());
        Assert.Equal(2, handler.RevalidationCallCount);
        Assert.Empty(Directory.GetDirectories(Path.Combine(temp.Path, "runs")));
    }

    [Theory]
    [InlineData(false, "presenter")]
    [InlineData(true, "presenter")]
    [InlineData(false, "enqueue")]
    [InlineData(true, "enqueue")]
    [InlineData(false, "dialog")]
    [InlineData(true, "dialog")]
    [InlineData(false, "invalid-allow")]
    [InlineData(true, "invalid-allow")]
    public async Task RealCoordinator_PromptInfrastructureFailure_IsNotDefiniteDenial(
        bool gatewayEvent,
        string failure)
    {
        using var temp = new TempDirectory();
        WritePromptPolicy(temp.Path, "full");
        using var store = new ExecApprovalsStore(
            temp.Path, NullLogger.Instance, stateDirOverride: temp.Path);
        IExecApprovalV2PromptHandler prompt = failure == "presenter"
            ? new CallbackPromptHandler((_, _) =>
                throw new InvalidOperationException("fixture-presenter-failure"))
            : new ExecApprovalV2UiPromptHandler(
                action =>
                {
                    if (failure == "enqueue")
                        return false;
                    action();
                    return true;
                },
                (_, _) => failure == "dialog"
                    ? Task.FromException<ExecApprovalPromptOutcome>(
                        new InvalidOperationException("fixture-dialog-failure"))
                    : Task.FromResult(failure == "invalid-allow"
                        ? ExecApprovalPromptOutcome.Allow
                        : ExecApprovalPromptOutcome.AllowOnce),
                NullLogger.Instance);
        var coordinator = new ExecApprovalsCoordinator(
            store, AlwaysCanPresentEvaluator.Instance, prompt, NullLogger.Instance);
        var runner = new RecordingRunner();

        var response = await InvokeOverSocketAsync(
            gatewayEvent, coordinator, runner, SystemCommand());

        Assert.Equal(0, runner.CallCount);
        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("InternalError", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinator_ExplicitUserDenial_IsDefiniteDenial(bool gatewayEvent)
    {
        using var temp = new TempDirectory();
        WritePromptPolicy(temp.Path, "full");
        using var store = new ExecApprovalsStore(
            temp.Path, NullLogger.Instance, stateDirOverride: temp.Path);
        var prompt = new ExecApprovalV2UiPromptHandler(
            action => { action(); return true; },
            (_, _) => Task.FromResult(ExecApprovalPromptOutcome.Deny),
            NullLogger.Instance);
        var coordinator = new ExecApprovalsCoordinator(
            store, AlwaysCanPresentEvaluator.Instance, prompt, NullLogger.Instance);
        var runner = new RecordingRunner();

        var response = await InvokeOverSocketAsync(
            gatewayEvent, coordinator, runner, SystemCommand());

        Assert.Equal(0, runner.CallCount);
        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("UserDenied", error.GetProperty("message").GetString());
        Assert.Equal("SYSTEM_RUN_DENIED", error.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealCoordinator_PromptExpiry_IsNotDefiniteDenial(
        bool gatewayEvent, bool lateDecision)
    {
        using var temp = new TempDirectory();
        WritePromptPolicy(temp.Path, "full");
        using var store = new ExecApprovalsStore(
            temp.Path, NullLogger.Instance, stateDirOverride: temp.Path);
        var clock = new ManualTimeProvider();
        var timeout = TimeSpan.FromMinutes(1);
        var prompt = new CallbackPromptHandler((_, token) =>
        {
            clock.Advance(timeout);
            return lateDecision
                ? Task.FromResult(ExecApprovalPromptOutcome.AllowAlways)
                : Task.FromCanceled<ExecApprovalPromptOutcome>(token);
        });
        var coordinator = new ExecApprovalsCoordinator(
            store, AlwaysCanPresentEvaluator.Instance, prompt, NullLogger.Instance,
            promptTimeout: timeout, timeProvider: clock);
        var runner = new RecordingRunner();

        var response = await InvokeOverSocketAsync(
            gatewayEvent, coordinator, runner, SystemCommand());

        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("InternalError", error.GetProperty("message").GetString());
        Assert.Contains("prompt-timeout", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("code", out _));
        Assert.Equal(0, runner.CallCount);
        Assert.Empty(store.ResolveReadOnly("main").Allowlist);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinator_PolicyTightenedDuringPrompt_IsDefiniteDenial(bool gatewayEvent)
    {
        using var temp = new TempDirectory();
        WritePromptPolicy(temp.Path, "full");
        using var store = new ExecApprovalsStore(
            temp.Path, NullLogger.Instance, stateDirOverride: temp.Path);
        using var deadline = new CancellationTokenSource(InvocationTimeout);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<ExecApprovalPromptOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new ExecApprovalV2UiPromptHandler(
            action => { action(); return true; },
            (_, cancellationToken) =>
            {
                entered.TrySetResult();
                return decision.Task.WaitAsync(cancellationToken);
            },
            NullLogger.Instance);
        var coordinator = new ExecApprovalsCoordinator(
            store, AlwaysCanPresentEvaluator.Instance, prompt, NullLogger.Instance);
        var runner = new RecordingRunner();

        var invocation = InvokeOverSocketAsync(
            gatewayEvent, coordinator, runner, SystemCommand());
        await entered.Task.WaitAsync(deadline.Token);
        WritePromptPolicy(temp.Path, "deny");
        decision.SetResult(ExecApprovalPromptOutcome.AllowOnce);
        var response = await invocation;

        Assert.Equal(0, runner.CallCount);
        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("policy-changed-before-execution",
            error.GetProperty("message").GetString());
        Assert.Equal("SYSTEM_RUN_DENIED", error.GetProperty("code").GetString());
        Assert.Empty(store.ResolveReadOnly("main").Allowlist);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinator_StoreMalformedDuringPrompt_IsNotDefiniteDenial(bool gatewayEvent)
    {
        using var temp = new TempDirectory();
        WritePromptPolicy(temp.Path, "full");
        using var store = new ExecApprovalsStore(
            temp.Path, NullLogger.Instance, stateDirOverride: temp.Path);
        using var deadline = new CancellationTokenSource(InvocationTimeout);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<ExecApprovalPromptOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new ExecApprovalV2UiPromptHandler(
            action => { action(); return true; },
            (_, cancellationToken) =>
            {
                entered.TrySetResult();
                return decision.Task.WaitAsync(cancellationToken);
            },
            NullLogger.Instance);
        var coordinator = new ExecApprovalsCoordinator(
            store, AlwaysCanPresentEvaluator.Instance, prompt, NullLogger.Instance);
        var runner = new RecordingRunner();

        var invocation = InvokeOverSocketAsync(
            gatewayEvent, coordinator, runner, SystemCommand());
        await entered.Task.WaitAsync(deadline.Token);
        var policyPath = Path.Combine(temp.Path, "exec-approvals.json");
        File.WriteAllText(policyPath, "{");
        decision.SetResult(ExecApprovalPromptOutcome.AllowAlways);
        var response = await invocation;

        Assert.Equal(0, runner.CallCount);
        Assert.False(response.GetProperty("ok").GetBoolean());
        var error = response.GetProperty("error");
        Assert.Contains("InternalError", error.GetProperty("message").GetString());
        Assert.Contains("approval-store-unavailable", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("code", out _));
        Assert.Equal("{", File.ReadAllText(policyPath));
    }

    private static string[] SystemCommand() =>
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), "cmd.exe"];

    private static void WritePromptPolicy(string directory, string security) =>
        File.WriteAllText(Path.Combine(directory, "exec-approvals.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                defaults = new { security, ask = "always", askFallback = "full" }
            }));

    private async Task<JsonElement> InvokeOverSocketAsync(
        bool gatewayEvent,
        IExecApprovalV2Handler handler,
        ICommandRunner runner,
        string[]? command = null,
        string nodeCommand = "system.run",
        Func<bool>? includeRunCommands = null,
        Action? afterHandshake = null)
    {
        using var temp = new TempDirectory();
        using var deadline = new CancellationTokenSource(InvocationTimeout);
        using var server = new LoopbackWebSocketServer(useManagedWebSocket: true);
        await server.StartAsync();
        using var client = new WindowsNodeClient(
            server.WebSocketUrl, "fixture-shared-token", temp.Path, NullLogger.Instance);
        var capability = new SystemCapability(NullLogger.Instance, includeRunCommands ?? (() => true));
        capability.SetV2Handler(handler);
        capability.SetCommandRunner(runner);
        client.RegisterCapability(capability);
        var connected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => connected.TrySetResult();

        await client.ConnectAsync().WaitAsync(deadline.Token);
        await server.SendTextAsync(
            """{"type":"event","event":"connect.challenge","payload":{"nonce":"wire-test-nonce"}}""",
            deadline.Token);
        using var connect = JsonDocument.Parse(
            await server.ReceiveTextAsync(deadline.Token));
        Assert.Equal("connect", connect.RootElement.GetProperty("method").GetString());
        Assert.Contains(nodeCommand, connect.RootElement.GetProperty("params")
            .GetProperty("commands").EnumerateArray().Select(value => value.GetString()));
        var connectId = connect.RootElement.GetProperty("id").GetString();
        await server.SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "res",
                id = connectId,
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = 4,
                    auth = new { deviceToken = "fixture-node-token" }
                }
            }),
            deadline.Token);
        await connected.Task.WaitAsync(deadline.Token);
        afterHandshake?.Invoke();

        var parameters = new
        {
            requestId = "wire-exec",
            command = nodeCommand,
            args = new { command = command ?? ["where.exe", "cmd.exe"] }
        };
        var invocation = gatewayEvent
            ? JsonSerializer.Serialize(new
            {
                type = "event", @event = "node.invoke.request", payload = parameters
            })
            : JsonSerializer.Serialize(new
            {
                type = "req", id = "wire-exec", method = "node.invoke", @params = parameters
            });
        await server.SendTextAsync(invocation, deadline.Token);
        using var result = JsonDocument.Parse(
            await server.ReceiveTextAsync(deadline.Token));
        if (gatewayEvent)
        {
            Assert.Equal("node.invoke.result",
                result.RootElement.GetProperty("method").GetString());
            output.WriteLine($"Actual Gateway event reply: {result.RootElement.GetProperty("params").GetRawText()}");
            return result.RootElement.GetProperty("params").Clone();
        }

        Assert.Equal("res", result.RootElement.GetProperty("type").GetString());
        Assert.Equal("wire-exec", result.RootElement.GetProperty("id").GetString());
        output.WriteLine($"Actual legacy reply: {result.RootElement.GetRawText()}");
        return result.RootElement.Clone();
    }

    private sealed class CallbackPromptHandler(
        Func<ExecApprovalV2PromptRequest, CancellationToken, Task<ExecApprovalPromptOutcome>> callback)
        : IExecApprovalV2PromptHandler
    {
        public Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default) =>
            callback(request, cancellationToken);
    }

    private sealed class ApprovalHandler : IExecApprovalV2Handler
    {
        public ExecApprovalV2Code Decision { get; init; } = ExecApprovalV2Code.Allow;
        public bool RevalidationCurrent { get; init; } = true;
        public bool RevalidationFailed { get; init; }
        public int SuccessfulRevalidations { get; init; }
        public int RevalidationCallCount { get; private set; }
        public bool Throw { get; init; }
        public int CallCount { get; private set; }

        public Task<ExecApprovalV2Result> HandleAsync(
            NodeInvokeRequest request, string correlationId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Throw)
                throw new InvalidOperationException("fixture-approval-failure");
            return Task.FromResult(Decision switch
            {
                ExecApprovalV2Code.UserDenied => ExecApprovalV2Result.UserDenied("user-denied"),
                ExecApprovalV2Code.SecurityDeny => ExecApprovalV2Result.SecurityDeny("policy-denied"),
                ExecApprovalV2Code.AskDeny => ExecApprovalV2Result.AskDeny("ask-denied"),
                ExecApprovalV2Code.AllowlistMiss => ExecApprovalV2Result.AllowlistMiss("not-listed"),
                ExecApprovalV2Code.Allow => ExecApprovalV2Result.Allow(
                    new ExecApprovedExecution(SystemCommand(), null, 1000, null)),
                _ => throw new InvalidOperationException("Unsupported fixture decision.")
            });
        }

        public ValueTask<ExecApprovalRevalidationResult> RevalidateAsync(
            ExecApprovedExecution execution,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            RevalidationCallCount++;
            return ValueTask.FromResult(RevalidationCallCount <= SuccessfulRevalidations
                ? ExecApprovalRevalidationResult.Current
                : RevalidationFailed
                ? ExecApprovalRevalidationResult.NotCurrent("policy-revalidation-failed")
                : RevalidationCurrent
                    ? ExecApprovalRevalidationResult.Current
                    : ExecApprovalRevalidationResult.PolicyDenied("policy-changed"));
        }
    }

    private sealed class RecordingRunner : ICommandRunner
    {
        public string Name => "wire-test";
        public bool Throw { get; init; }
        public int CallCount { get; private set; }
        public string ResolveEffectiveShell(string? requestedShell) => "cmd";

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (Throw)
                throw new InvalidOperationException("fixture-post-dispatch-failure");
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }
}
