using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Telemetry;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NodeToolTelemetryCollection
{
    public const string Name = "Node tool telemetry";
}

[Collection(NodeToolTelemetryCollection.Name)]
public sealed class NodeToolTelemetryTests
{
    [Fact]
    public void ConstantsAndFiniteValues_AreStable()
    {
        Assert.Equal("openclaw.node.tool.invoke", NodeToolInvocation.InvokeSpanName);
        Assert.Equal("openclaw.node.tool.execute", NodeToolInvocation.ExecuteSpanName);
        Assert.Equal("openclaw.node.tool.system_run.authorize", NodeToolInvocation.SystemRunAuthorizeSpanName);
        Assert.Equal("openclaw.node.tool.system_run.run", NodeToolInvocation.SystemRunRunSpanName);
        Assert.Equal("openclaw.node.tool.invocations", NodeToolInvocation.InvocationsMetricName);
        Assert.Equal("openclaw.node.tool.duration", NodeToolInvocation.DurationMetricName);
        Assert.Equal(
            "openclaw.node.tool.system_run.approval.pipeline",
            NodeToolInvocation.ApprovalPipelineTag);
        Assert.Equal("gateway", NodeToolTransport.Gateway.ToTelemetryValue());
        Assert.Equal("mcp", NodeToolTransport.Mcp.ToTelemetryValue());
        Assert.Equal("legacy", NodeToolApprovalPipeline.Legacy.ToTelemetryValue());
        Assert.Equal("v2", NodeToolApprovalPipeline.V2.ToTelemetryValue());
        Assert.Equal("exec_policy_denied", NodeToolErrorCategory.ExecPolicyDenied.ToTelemetryValue());
        Assert.Equal("sandbox_failure", NodeToolErrorCategory.SandboxFailure.ToTelemetryValue());
        Assert.Equal(
            "fallback_shell_unapproved",
            NodeToolSandboxDenialReason.FallbackShellUnapproved.ToTelemetryValue());
    }

    [Fact]
    public void SandboxDenialReason_IsAppliedToRunAndRootWithoutFreeFormText()
    {
        using var activities = new ActivityCollector();
        var invocation = new NodeToolInvocation(NodeToolTransport.Gateway);
        invocation.SetCommand("system.run");
        invocation.SetSandboxDenialReason(NodeToolSandboxDenialReason.UnsupportedSandboxRequest);
        var run = invocation.StartChild(NodeToolInvocation.SystemRunRunSpanName);

        NodeToolInvocation.CompleteChild(
            run,
            NodeToolOutcome.Failure,
            NodeToolErrorCategory.SandboxDenied,
            NodeToolExecutionMode.Sandbox,
            sandboxDenialReason: NodeToolSandboxDenialReason.UnsupportedSandboxRequest);
        var completion = invocation.Complete(
            NodeToolOutcome.Failure,
            NodeToolErrorCategory.SandboxDenied,
            NodeToolExecutionMode.Sandbox);

        Assert.Equal(
            NodeToolSandboxDenialReason.UnsupportedSandboxRequest,
            completion!.SandboxDenialReason);
        Assert.All(
            activities.Stopped,
            activity => Assert.Equal(
                "unsupported_sandbox_request",
                activity.GetTagItem(NodeToolInvocation.SandboxDenialReasonTag)));
    }

    [Fact]
    public void Invocation_CreatesDetachedRootAndExplicitChild_AndCompletesOnce()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        using var ambient = new Activity("ambient").Start();
        var invocation = new NodeToolInvocation(NodeToolTransport.Gateway);
        invocation.SetCommand("system.run");
        var child = invocation.StartChild(NodeToolInvocation.ExecuteSpanName);
        invocation.SetApprovalPipeline(NodeToolApprovalPipeline.V2);

        NodeToolInvocation.CompleteChild(child, NodeToolOutcome.Success);
        var completion = invocation.Complete(
            NodeToolOutcome.Failure,
            NodeToolErrorCategory.CommandFailed,
            NodeToolExecutionMode.Sandbox);
        var duplicate = invocation.Complete(NodeToolOutcome.Success);

        Assert.NotNull(completion);
        Assert.Equal(NodeToolApprovalPipeline.V2, completion.ApprovalPipeline);
        Assert.Null(duplicate);
        Assert.Same(ambient, Activity.Current);

        var root = Assert.Single(activities.Stopped, a => a.OperationName == NodeToolInvocation.InvokeSpanName);
        var execute = Assert.Single(activities.Stopped, a => a.OperationName == NodeToolInvocation.ExecuteSpanName);
        Assert.Equal(default, root.ParentSpanId);
        Assert.Equal(root.TraceId, execute.TraceId);
        Assert.Equal(root.SpanId, execute.ParentSpanId);
        Assert.Equal("system.run", root.GetTagItem(NodeToolInvocation.CommandTag));
        Assert.Equal("gateway", root.GetTagItem(NodeToolInvocation.TransportTag));
        Assert.Equal("v2", root.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Null(execute.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Equal("failure", root.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("command_failed", root.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.Equal(true, root.GetTagItem(NodeToolInvocation.SandboxRequestedTag));
        Assert.Equal(true, root.GetTagItem(NodeToolInvocation.SandboxAppliedTag));
        Assert.Equal("mxc", root.GetTagItem(NodeToolInvocation.SandboxProviderTag));
        Assert.Equal(
            "windows_appcontainer",
            root.GetTagItem(NodeToolInvocation.SandboxTechnologyTag));

        Assert.Single(metrics.LongMeasurements, m => m.Name == NodeToolInvocation.InvocationsMetricName);
        Assert.Single(metrics.DoubleMeasurements, m => m.Name == NodeToolInvocation.DurationMetricName);
    }

    [Fact]
    public void InternalDiagnostics_AreNotSerialized()
    {
        using var args = JsonDocument.Parse("{}");
        var request = new NodeInvokeRequest
        {
            Id = "private-request",
            Command = "system.run",
            Args = args.RootElement.Clone(),
            Telemetry = new NodeToolInvocation(NodeToolTransport.Mcp)
        };
        var response = new NodeInvokeResponse
        {
            Ok = false,
            Error = "wire error",
            Diagnostic = new NodeToolDiagnostic(NodeToolErrorCategory.ExecPolicyDenied)
        };

        var requestJson = JsonSerializer.Serialize(request);
        var responseJson = JsonSerializer.Serialize(response);
        request.Telemetry.Dispose();

        Assert.DoesNotContain("Telemetry", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Diagnostic", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("exec_policy_denied", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemRun_NonzeroSandboxExit_EmitsNestedRunSpanAndSandboxAttributes()
    {
        using var activities = new ActivityCollector();
        using var invocation = new NodeToolInvocation(NodeToolTransport.Mcp);
        invocation.SetCommand("system.run");
        var execute = invocation.StartChild(NodeToolInvocation.ExecuteSpanName);
        var capability = new SystemCapability(NullLogger.Instance);
        capability.SetCommandRunner(new FixedCommandRunner(new CommandResult
        {
            ExitCode = 7,
            Stderr = "private stderr",
            ExecutionMode = NodeToolExecutionMode.Sandbox,
        }));
        using var args = JsonDocument.Parse("""{"command":"fail"}""");

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "system.run",
            Args = args.RootElement.Clone(),
            Telemetry = invocation,
            TelemetryParentContext = execute?.Context ?? invocation.Context,
        });
        NodeToolInvocation.CompleteChild(execute, NodeToolOutcome.Failure);

        Assert.True(response.Ok);
        Assert.Equal(NodeToolErrorCategory.CommandFailed, response.Diagnostic?.ErrorCategory);
        Assert.Equal(NodeToolExecutionMode.Sandbox, response.Diagnostic?.ExecutionMode);
        var run = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunRunSpanName);
        var executeActivity = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.ExecuteSpanName);
        Assert.Equal(executeActivity.SpanId, run.ParentSpanId);
        Assert.Equal("failure", run.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("command_failed", run.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.Equal("legacy", run.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Equal(true, run.GetTagItem(NodeToolInvocation.SandboxRequestedTag));
        Assert.Equal(true, run.GetTagItem(NodeToolInvocation.SandboxAppliedTag));
        Assert.Equal("mxc", run.GetTagItem(NodeToolInvocation.SandboxProviderTag));
        Assert.Equal(
            "windows_appcontainer",
            run.GetTagItem(NodeToolInvocation.SandboxTechnologyTag));
        Assert.DoesNotContain(
            activities.Stopped.SelectMany(activity => activity.TagObjects),
            tag => Equals(tag.Value, "private stderr"));
    }

    [Theory]
    [InlineData(ExecApprovalV2Code.SecurityDeny, NodeToolErrorCategory.ExecPolicyDenied)]
    [InlineData(ExecApprovalV2Code.AskDeny, NodeToolErrorCategory.ExecPolicyDenied)]
    [InlineData(ExecApprovalV2Code.AllowlistMiss, NodeToolErrorCategory.ExecPolicyDenied)]
    [InlineData(ExecApprovalV2Code.UserDenied, NodeToolErrorCategory.ExecPolicyDenied)]
    [InlineData(ExecApprovalV2Code.ValidationFailed, NodeToolErrorCategory.InvalidRequest)]
    [InlineData(ExecApprovalV2Code.ResolutionFailed, NodeToolErrorCategory.CommandUnavailable)]
    [InlineData(ExecApprovalV2Code.Unavailable, NodeToolErrorCategory.CapabilityUnavailable)]
    [InlineData(ExecApprovalV2Code.InternalError, NodeToolErrorCategory.InternalFailure)]
    public async Task SystemRun_V2Denials_MapToTypedDiagnostics(
        ExecApprovalV2Code code,
        NodeToolErrorCategory expectedCategory)
    {
        using var activities = new ActivityCollector();
        using var invocation = new NodeToolInvocation(NodeToolTransport.Gateway);
        invocation.SetCommand("system.run");
        var execute = invocation.StartChild(NodeToolInvocation.ExecuteSpanName);
        var capability = new SystemCapability(NullLogger.Instance);
        capability.SetV2Handler(new FixedV2Handler(CreateV2Result(code)));
        using var args = JsonDocument.Parse("""{"command":"test"}""");

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "system.run",
            Args = args.RootElement.Clone(),
            Telemetry = invocation,
            TelemetryParentContext = execute?.Context ?? invocation.Context,
        });
        NodeToolInvocation.CompleteChild(execute, NodeToolOutcome.Failure);

        Assert.False(response.Ok);
        Assert.Equal(expectedCategory, response.Diagnostic?.ErrorCategory);
        var approval = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunAuthorizeSpanName);
        var executeActivity = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.ExecuteSpanName);
        Assert.Equal(executeActivity.SpanId, approval.ParentSpanId);
        Assert.Equal(expectedCategory.ToTelemetryValue(),
            approval.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
    }

    [Fact]
    public async Task SystemRun_V2Allow_EmitsAuthorizeAndRunSpans()
    {
        using var activities = new ActivityCollector();
        using var invocation = new NodeToolInvocation(NodeToolTransport.Gateway);
        invocation.SetCommand("system.run");
        var execute = invocation.StartChild(NodeToolInvocation.ExecuteSpanName);
        var capability = new SystemCapability(NullLogger.Instance);
        capability.SetCommandRunner(new FixedCommandRunner(new CommandResult
        {
            ExitCode = 7,
            ExecutionMode = NodeToolExecutionMode.Host,
        }));
        capability.SetV2Handler(new FixedV2Handler(ExecApprovalV2Result.Allow(
            new ExecApprovedExecution([@"C:\tools\fail.exe"], null, 1000, null))));
        using var args = JsonDocument.Parse("""{"command":"ignored"}""");

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "system.run",
            Args = args.RootElement.Clone(),
            Telemetry = invocation,
            TelemetryParentContext = execute?.Context ?? invocation.Context,
        });
        NodeToolInvocation.CompleteChild(execute, NodeToolOutcome.Failure);

        Assert.True(response.Ok);
        Assert.Equal(NodeToolErrorCategory.CommandFailed, response.Diagnostic?.ErrorCategory);
        var authorize = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunAuthorizeSpanName);
        var run = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunRunSpanName);
        var executeActivity = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.ExecuteSpanName);
        Assert.Equal(executeActivity.SpanId, authorize.ParentSpanId);
        Assert.Equal(executeActivity.SpanId, run.ParentSpanId);
        Assert.Equal("success", authorize.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("v2", authorize.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Equal("failure", run.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("v2", run.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Equal(
            "command_failed",
            run.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
    }

    [Fact]
    public async Task SystemRun_V2DirectArgvGate_ReportsCapabilityUnavailable()
    {
        using var activities = new ActivityCollector();
        using var invocation = new NodeToolInvocation(NodeToolTransport.Gateway);
        invocation.SetCommand("system.run");
        var execute = invocation.StartChild(NodeToolInvocation.ExecuteSpanName);
        var capability = new SystemCapability(NullLogger.Instance);
        capability.SetCommandRunner(new DirectArgvUnsupportedRunner());
        capability.SetV2Handler(new FixedV2Handler(ExecApprovalV2Result.Allow(
            new ExecApprovedExecution([@"C:\tools\test.exe"], null, 1000, null))));
        using var args = JsonDocument.Parse("""{"command":"ignored"}""");

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "system.run",
            Args = args.RootElement.Clone(),
            Telemetry = invocation,
            TelemetryParentContext = execute?.Context ?? invocation.Context,
        });
        NodeToolInvocation.CompleteChild(execute, NodeToolOutcome.Failure);

        Assert.False(response.Ok);
        Assert.Equal(NodeToolErrorCategory.CapabilityUnavailable, response.Diagnostic?.ErrorCategory);
        var authorize = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunAuthorizeSpanName);
        Assert.Equal("failure", authorize.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("v2", authorize.GetTagItem(NodeToolInvocation.ApprovalPipelineTag));
        Assert.Equal(
            "capability_unavailable",
            authorize.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == NodeToolInvocation.SystemRunRunSpanName);
    }

    private static ExecApprovalV2Result CreateV2Result(ExecApprovalV2Code code) =>
        code switch
        {
            ExecApprovalV2Code.SecurityDeny => ExecApprovalV2Result.SecurityDeny("reason"),
            ExecApprovalV2Code.AskDeny => ExecApprovalV2Result.AskDeny("reason"),
            ExecApprovalV2Code.AllowlistMiss => ExecApprovalV2Result.AllowlistMiss("reason"),
            ExecApprovalV2Code.UserDenied => ExecApprovalV2Result.UserDenied("reason"),
            ExecApprovalV2Code.ValidationFailed => ExecApprovalV2Result.ValidationFailed("reason"),
            ExecApprovalV2Code.ResolutionFailed => ExecApprovalV2Result.ResolutionFailed("reason"),
            ExecApprovalV2Code.Unavailable => ExecApprovalV2Result.Unavailable("reason"),
            ExecApprovalV2Code.InternalError => ExecApprovalV2Result.InternalError("reason"),
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };

    private sealed class FixedCommandRunner(CommandResult result) : ICommandRunner
    {
        public string Name => "fixed";

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class DirectArgvUnsupportedRunner : IDirectArgvSupportAwareCommandRunner
    {
        public string Name => "unsupported";

        public bool CanExecuteDirectArgv() => false;

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("The direct-argv gate must prevent execution.");
    }

    private sealed class FixedV2Handler(ExecApprovalV2Result result) : IExecApprovalV2Handler
    {
        public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId) =>
            Task.FromResult(result);
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == OpenClawActivitySourceName.OpenClaw.ToTelemetryName(),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Stopped { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener;

        public MetricCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == OpenClawMeterName.OpenClaw.ToTelemetryName())
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                LongMeasurements.Add((instrument.Name, value)));
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
                DoubleMeasurements.Add((instrument.Name, value)));
            _listener.Start();
        }

        public List<(string Name, long Value)> LongMeasurements { get; } = [];
        public List<(string Name, double Value)> DoubleMeasurements { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
