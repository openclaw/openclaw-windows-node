using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenClaw.Shared.Telemetry;

public enum NodeToolTransport
{
    Gateway,
    Mcp
}

public enum NodeToolOutcome
{
    Success,
    Failure,
    Canceled
}

public enum NodeToolErrorCategory
{
    None,
    InvalidRequest,
    UnsupportedCommand,
    NodeBusy,
    PermissionDenied,
    ExecPolicyDenied,
    CommandUnavailable,
    CapabilityUnavailable,
    SandboxDenied,
    SandboxUnavailable,
    SandboxFailure,
    CommandFailed,
    Timeout,
    CapabilityFailure,
    TransportFailure,
    InternalFailure,
    Other
}

public enum NodeToolExecutionMode
{
    Host,
    Sandbox,
    HostFallback
}

public enum NodeToolApprovalPipeline
{
    Legacy,
    V2
}

public enum NodeToolSandboxDenialReason
{
    DirectArgvUnsupported,
    CustomEnvironmentUnsupported,
    EffectiveShellChanged,
    FallbackShellUnapproved,
    UnsupportedSandboxRequest
}

public sealed record NodeToolDiagnostic(
    NodeToolErrorCategory ErrorCategory,
    NodeToolExecutionMode? ExecutionMode = null,
    NodeToolSandboxDenialReason? SandboxDenialReason = null);

public sealed record NodeToolTelemetryCompletion(
    string Command,
    NodeToolTransport Transport,
    NodeToolOutcome Outcome,
    NodeToolErrorCategory ErrorCategory,
    NodeToolExecutionMode? ExecutionMode,
    string? ErrorType,
    double DurationMilliseconds,
    NodeToolSandboxDenialReason? SandboxDenialReason = null,
    NodeToolApprovalPipeline? ApprovalPipeline = null);

public sealed record NodeToolSandboxTelemetry(
    bool Requested,
    bool? Applied,
    string? Provider = null,
    string? Technology = null,
    string? FallbackTarget = null,
    string? FallbackReason = null);

/// <summary>
/// Tracks one node-side tool invocation without depending on an OpenTelemetry SDK.
/// </summary>
public sealed class NodeToolInvocation : IDisposable
{
    public const string InvokeSpanName = "openclaw.node.tool.invoke";
    public const string ExecuteSpanName = "openclaw.node.tool.execute";
    public const string SystemRunAuthorizeSpanName = "openclaw.node.tool.system_run.authorize";
    public const string SystemRunRunSpanName = "openclaw.node.tool.system_run.run";
    public const string InvocationsMetricName = "openclaw.node.tool.invocations";
    public const string DurationMetricName = "openclaw.node.tool.duration";
    public const string LogsDroppedMetricName = "openclaw.node.tool.logs.dropped";

    public const string CommandTag = "openclaw.node.tool.name";
    public const string TransportTag = "openclaw.node.tool.transport";
    public const string ApprovalPipelineTag = "openclaw.node.tool.system_run.approval.pipeline";
    public const string SandboxRequestedTag = "openclaw.node.tool.sandbox.requested";
    public const string SandboxAppliedTag = "openclaw.node.tool.sandbox.applied";
    public const string SandboxProviderTag = "openclaw.node.tool.sandbox.provider";
    public const string SandboxTechnologyTag = "openclaw.node.tool.sandbox.technology";
    public const string SandboxDenialReasonTag = "openclaw.node.tool.sandbox.denial.reason";
    public const string SandboxFallbackTargetTag = "openclaw.node.tool.sandbox.fallback.target";
    public const string SandboxFallbackReasonTag = "openclaw.node.tool.sandbox.fallback.reason";
    public const string LogDropReasonTag = "openclaw.node.tool.log.drop.reason";

    private const string UnknownCommand = "unknown";
    private static readonly Counter<long> Invocations = OpenClawTelemetry.CreateCounter(
        InvocationsMetricName,
        unit: "{invocation}",
        description: "Number of Windows node tool invocations.");
    private static readonly Histogram<double> Duration = OpenClawTelemetry.CreateHistogram(
        DurationMetricName,
        unit: "ms",
        description: "End-to-end duration of Windows node tool invocations.");
    private static readonly Counter<long> LogsDropped = OpenClawTelemetry.CreateCounter(
        LogsDroppedMetricName,
        unit: "{log}",
        description: "Number of Windows node tool completion logs dropped before export.");

    private readonly Activity? _activity;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly NodeToolTransport _transport;
    private string _command = UnknownCommand;
    private NodeToolApprovalPipeline? _approvalPipeline;
    private NodeToolSandboxDenialReason? _sandboxDenialReason;
    private int _completed;

    public NodeToolInvocation(NodeToolTransport transport)
    {
        _transport = transport;
        _activity = OpenClawTelemetry.StartDetachedActivity(
            InvokeSpanName,
            default(ActivityContext),
            [
                OpenClawTelemetryTag.String(CommandTag, UnknownCommand),
                OpenClawTelemetryTag.String(TransportTag, transport.ToTelemetryValue())
            ],
            System.Diagnostics.ActivityKind.Server);
    }

    public ActivityContext Context => _activity?.Context ?? default;

    public void SetCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        _command = command;
        _activity?.SetTag(CommandTag, command);
    }

    public void SetSandboxDenialReason(NodeToolSandboxDenialReason reason)
    {
        _sandboxDenialReason = reason;
        _activity?.SetTag(SandboxDenialReasonTag, reason.ToTelemetryValue());
    }

    public void SetApprovalPipeline(NodeToolApprovalPipeline pipeline)
    {
        _approvalPipeline = pipeline;
        _activity?.SetTag(ApprovalPipelineTag, pipeline.ToTelemetryValue());
    }

    public Activity? StartChild(string spanName, ActivityContext? parentContext = null)
    {
        var activity = OpenClawTelemetry.StartDetachedActivity(
            spanName,
            parentContext ?? Context,
            [
                OpenClawTelemetryTag.String(CommandTag, _command),
                OpenClawTelemetryTag.String(TransportTag, _transport.ToTelemetryValue())
            ]);
        if (_approvalPipeline.HasValue)
            activity?.SetTag(ApprovalPipelineTag, _approvalPipeline.Value.ToTelemetryValue());
        return activity;
    }

    public NodeToolTelemetryCompletion? Complete(
        NodeToolOutcome outcome,
        NodeToolErrorCategory errorCategory = NodeToolErrorCategory.None,
        NodeToolExecutionMode? executionMode = null,
        Type? errorType = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return null;

        _stopwatch.Stop();
        var errorTypeName = errorType?.FullName;
        ApplyTerminalTags(
            _activity,
            outcome,
            errorCategory,
            executionMode,
            errorTypeName,
            _sandboxDenialReason);

        var tags = CreateMetricTags(_command, _transport, outcome, errorCategory);
        OpenClawTelemetry.Add(Invocations, tags: tags);
        OpenClawTelemetry.Record(Duration, _stopwatch.Elapsed.TotalMilliseconds, tags);
        OpenClawTelemetry.StopDetachedActivity(_activity);

        return new NodeToolTelemetryCompletion(
            _command,
            _transport,
            outcome,
            errorCategory,
            executionMode,
            errorTypeName,
            _stopwatch.Elapsed.TotalMilliseconds,
            _sandboxDenialReason,
            _approvalPipeline);
    }

    public static void CompleteChild(
        Activity? activity,
        NodeToolOutcome outcome,
        NodeToolErrorCategory errorCategory = NodeToolErrorCategory.None,
        NodeToolExecutionMode? executionMode = null,
        Type? errorType = null,
        NodeToolSandboxDenialReason? sandboxDenialReason = null)
    {
        ApplyTerminalTags(
            activity,
            outcome,
            errorCategory,
            executionMode,
            errorType?.FullName,
            sandboxDenialReason);
        OpenClawTelemetry.StopDetachedActivity(activity);
    }

    public static void RecordLogDroppedQueueFull() =>
        OpenClawTelemetry.Add(
            LogsDropped,
            tags:
            [
                OpenClawTelemetryTag.String(LogDropReasonTag, "queue_full")
            ]);

    public static NodeToolSandboxTelemetry? GetSandboxTelemetry(
        NodeToolExecutionMode? executionMode,
        NodeToolErrorCategory errorCategory)
    {
        return executionMode switch
        {
            NodeToolExecutionMode.Host => new NodeToolSandboxTelemetry(
                Requested: false,
                Applied: false),
            NodeToolExecutionMode.Sandbox => new NodeToolSandboxTelemetry(
                Requested: true,
                Applied: errorCategory switch
                {
                    NodeToolErrorCategory.SandboxDenied => false,
                    NodeToolErrorCategory.SandboxUnavailable => false,
                    NodeToolErrorCategory.SandboxFailure => null,
                    _ => true,
                },
                Provider: "mxc",
                Technology: "windows_appcontainer"),
            NodeToolExecutionMode.HostFallback => new NodeToolSandboxTelemetry(
                Requested: true,
                Applied: false,
                Provider: "mxc",
                Technology: "windows_appcontainer",
                FallbackTarget: "unsandboxed",
                FallbackReason: "mxc_unavailable"),
            _ => null,
        };
    }

    public void Dispose()
    {
        Complete(NodeToolOutcome.Canceled, NodeToolErrorCategory.Other);
    }

    private static void ApplyTerminalTags(
        Activity? activity,
        NodeToolOutcome outcome,
        NodeToolErrorCategory errorCategory,
        NodeToolExecutionMode? executionMode,
        string? errorType,
        NodeToolSandboxDenialReason? sandboxDenialReason)
    {
        if (activity == null)
            return;

        activity.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), outcome.ToTelemetryValue());
        if (errorCategory != NodeToolErrorCategory.None)
            activity.SetTag(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), errorCategory.ToTelemetryValue());
        ApplySandboxTags(activity, GetSandboxTelemetry(executionMode, errorCategory));
        if (sandboxDenialReason.HasValue)
            activity.SetTag(SandboxDenialReasonTag, sandboxDenialReason.Value.ToTelemetryValue());
        if (errorType != null)
            activity.SetTag(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName(), errorType);

        activity.SetStatus(
            outcome == NodeToolOutcome.Failure ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }

    private static void ApplySandboxTags(
        Activity activity,
        NodeToolSandboxTelemetry? sandbox)
    {
        if (sandbox == null)
            return;

        activity.SetTag(SandboxRequestedTag, sandbox.Requested);
        if (sandbox.Applied.HasValue)
            activity.SetTag(SandboxAppliedTag, sandbox.Applied.Value);
        if (sandbox.Provider != null)
            activity.SetTag(SandboxProviderTag, sandbox.Provider);
        if (sandbox.Technology != null)
            activity.SetTag(SandboxTechnologyTag, sandbox.Technology);
        if (sandbox.FallbackTarget != null)
            activity.SetTag(SandboxFallbackTargetTag, sandbox.FallbackTarget);
        if (sandbox.FallbackReason != null)
            activity.SetTag(SandboxFallbackReasonTag, sandbox.FallbackReason);
    }

    private static OpenClawTelemetryTag[] CreateMetricTags(
        string command,
        NodeToolTransport transport,
        NodeToolOutcome outcome,
        NodeToolErrorCategory errorCategory) =>
    [
        OpenClawTelemetryTag.String(CommandTag, command),
        OpenClawTelemetryTag.String(TransportTag, transport.ToTelemetryValue()),
        OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, outcome.ToTelemetryValue()),
        OpenClawTelemetryTag.String(
            OpenClawTelemetryTagKey.ErrorCategory,
            errorCategory.ToTelemetryValue())
    ];
}

public static class NodeToolTelemetryValues
{
    public static string ToTelemetryValue(this NodeToolTransport value) =>
        value switch
        {
            NodeToolTransport.Gateway => "gateway",
            NodeToolTransport.Mcp => "mcp",
            _ => "other"
        };

    public static string ToTelemetryValue(this NodeToolOutcome value) =>
        value switch
        {
            NodeToolOutcome.Success => "success",
            NodeToolOutcome.Failure => "failure",
            NodeToolOutcome.Canceled => "canceled",
            _ => "failure"
        };

    public static string ToTelemetryValue(this NodeToolExecutionMode value) =>
        value switch
        {
            NodeToolExecutionMode.Host => "host",
            NodeToolExecutionMode.Sandbox => "sandbox",
            NodeToolExecutionMode.HostFallback => "host_fallback",
            _ => "host"
        };

    public static string ToTelemetryValue(this NodeToolApprovalPipeline value) =>
        value switch
        {
            NodeToolApprovalPipeline.Legacy => "legacy",
            NodeToolApprovalPipeline.V2 => "v2",
            _ => "legacy"
        };

    public static string ToTelemetryValue(this NodeToolSandboxDenialReason value) =>
        value switch
        {
            NodeToolSandboxDenialReason.DirectArgvUnsupported => "direct_argv_unsupported",
            NodeToolSandboxDenialReason.CustomEnvironmentUnsupported => "custom_environment_unsupported",
            NodeToolSandboxDenialReason.EffectiveShellChanged => "effective_shell_changed",
            NodeToolSandboxDenialReason.FallbackShellUnapproved => "fallback_shell_unapproved",
            NodeToolSandboxDenialReason.UnsupportedSandboxRequest => "unsupported_sandbox_request",
            _ => "unsupported_sandbox_request"
        };

    public static string ToTelemetryValue(this NodeToolErrorCategory value) =>
        value switch
        {
            NodeToolErrorCategory.None => "none",
            NodeToolErrorCategory.InvalidRequest => "invalid_request",
            NodeToolErrorCategory.UnsupportedCommand => "unsupported_command",
            NodeToolErrorCategory.NodeBusy => "node_busy",
            NodeToolErrorCategory.PermissionDenied => "permission_denied",
            NodeToolErrorCategory.ExecPolicyDenied => "exec_policy_denied",
            NodeToolErrorCategory.CommandUnavailable => "command_unavailable",
            NodeToolErrorCategory.CapabilityUnavailable => "capability_unavailable",
            NodeToolErrorCategory.SandboxDenied => "sandbox_denied",
            NodeToolErrorCategory.SandboxUnavailable => "sandbox_unavailable",
            NodeToolErrorCategory.SandboxFailure => "sandbox_failure",
            NodeToolErrorCategory.CommandFailed => "command_failed",
            NodeToolErrorCategory.Timeout => "timeout",
            NodeToolErrorCategory.CapabilityFailure => "capability_failure",
            NodeToolErrorCategory.TransportFailure => "transport_failure",
            NodeToolErrorCategory.InternalFailure => "internal_failure",
            NodeToolErrorCategory.Other => "other",
            _ => "other"
        };
}
