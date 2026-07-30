using System.Collections.Frozen;
using System.Diagnostics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared;

/// <summary>
/// Executes Windows-owned node capabilities independently of the Gateway transport.
/// </summary>
/// <remarks>
/// Both the in-process C# client and a future Rust runtime adapter use this
/// dispatcher so command routing, bounds, cancellation, telemetry, and result
/// semantics remain owned by the Windows capability host.
/// </remarks>
public sealed class NodeCapabilityDispatcher
{
    private const int MaxConcurrentInvocations = 8;

    private readonly object _eventSender;
    private readonly Func<string?> _nodeId;
    private readonly IOpenClawLogger _logger;
    private readonly List<INodeCapability> _capabilities = new();
    private FrozenDictionary<string, CommandDispatchEntry> _commandMap =
        FrozenDictionary<string, CommandDispatchEntry>.Empty;
    private readonly SemaphoreSlim _invokeSemaphore =
        new(MaxConcurrentInvocations, MaxConcurrentInvocations);
    private readonly InvocationCancellationRegistry _activeInvocations = new();

    public NodeCapabilityDispatcher(
        object eventSender,
        Func<string?> nodeId,
        IOpenClawLogger logger)
    {
        _eventSender = eventSender ?? throw new ArgumentNullException(nameof(eventSender));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<INodeCapability> Capabilities => _capabilities;

    public event EventHandler<NodeInvokeRequest>? InvokeReceived;
    public event EventHandler<NodeInvokeCompletedEventArgs>? InvokeCompleted;
    public event EventHandler<NodeToolTelemetryCompletion>? ToolTelemetryCompleted;

    public void RegisterCapability(INodeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (_capabilities.Contains(capability))
            return;

        _capabilities.Add(capability);
        RebuildCommandMap();
    }

    /// <summary>
    /// Dispatches one transport-decoded invocation without blocking the caller
    /// while the capability runs.
    /// </summary>
    public async Task DispatchAsync(
        NodeInvokeRequest request,
        Func<NodeInvokeResponse, Task> sendResponse,
        Func<string, Task> sendErrorResponse,
        CancellationToken connectionCancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sendResponse);
        ArgumentNullException.ThrowIfNull(sendErrorResponse);

        var telemetry = request.Telemetry ?? new NodeToolInvocation(NodeToolTransport.Gateway);
        request.Telemetry = telemetry;
        var dispatchEntry = Volatile.Read(ref _commandMap).GetValueOrDefault(request.Command);

        if (dispatchEntry == null)
        {
            var error = $"Command not supported: {request.Command}";
            _logger.Warn($"[NODE] No capability registered for command: {request.Command}");
            await SendFailureAndCompleteTelemetryAsync(
                telemetry,
                () => sendErrorResponse(error),
                NodeToolErrorCategory.UnsupportedCommand);
            RaiseInvokeCompleted(request, false, error, TimeSpan.Zero);
            return;
        }

        telemetry.SetCommand(dispatchEntry.CanonicalName);
        if (!_invokeSemaphore.Wait(0))
        {
            const string error = "node busy, retry";
            _logger.Warn($"[NODE] Invoke slots full, rejecting {request.Command} ({request.Id})");
            await SendFailureAndCompleteTelemetryAsync(
                telemetry,
                () => sendErrorResponse(error),
                NodeToolErrorCategory.NodeBusy);
            RaiseInvokeCompleted(request, false, error, TimeSpan.Zero);
            return;
        }

        if (!_activeInvocations.TryRegister(
                request.Id,
                connectionCancellation,
                out var invocation))
        {
            _invokeSemaphore.Release();
            const string error = "duplicate active request id";
            _logger.Warn($"[NODE] Duplicate active invoke ID: {request.Id}");
            await SendFailureAndCompleteTelemetryAsync(
                telemetry,
                () => sendErrorResponse(error),
                NodeToolErrorCategory.InvalidRequest);
            RaiseInvokeCompleted(request, false, error, TimeSpan.Zero);
            return;
        }

        _ = Task.Run(
            () => ExecuteCapabilityAsync(
                request,
                dispatchEntry.Capability,
                sendResponse,
                sendErrorResponse,
                invocation!),
            CancellationToken.None);
    }

    public bool TryCancel(string requestId) => _activeInvocations.TryCancel(requestId);

    public void CancelAll() => _activeInvocations.CancelAll();

    internal void CompleteTelemetry(
        NodeToolInvocation telemetry,
        NodeToolOutcome outcome,
        NodeToolErrorCategory category,
        NodeToolExecutionMode? executionMode = null,
        Type? errorType = null)
    {
        var completion = telemetry.Complete(outcome, category, executionMode, errorType);
        if (completion == null)
            return;

        try
        {
            ToolTelemetryCompleted?.Invoke(_eventSender, completion);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[NODE] Tool telemetry completion handler failed: {ex.GetType().Name}");
        }
    }

    internal async Task SendFailureAndCompleteTelemetryAsync(
        NodeToolInvocation telemetry,
        Func<Task> send,
        NodeToolErrorCategory category)
    {
        try
        {
            await send();
            CompleteTelemetry(telemetry, NodeToolOutcome.Failure, category);
        }
        catch (Exception ex)
        {
            CompleteTelemetry(
                telemetry,
                NodeToolOutcome.Failure,
                NodeToolErrorCategory.TransportFailure,
                errorType: ex.GetType());
            throw;
        }
    }

    private void RebuildCommandMap()
    {
        var map = new Dictionary<string, CommandDispatchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in _capabilities)
        {
            foreach (var command in capability.Commands)
                map.TryAdd(command, new CommandDispatchEntry(capability, command));
        }

        Volatile.Write(
            ref _commandMap,
            map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private async Task ExecuteCapabilityAsync(
        NodeInvokeRequest request,
        INodeCapability capability,
        Func<NodeInvokeResponse, Task> sendResponse,
        Func<string, Task> sendErrorResponse,
        InvocationCancellationRegistry.InvocationCancellation invocation)
    {
        using var activeInvocation = invocation;
        var cancellationToken = activeInvocation.Token;
        var telemetry = request.Telemetry!;
        var stopwatch = Stopwatch.StartNew();
        var executeActivity = telemetry.StartChild(NodeToolInvocation.ExecuteSpanName);
        request.TelemetryParentContext = executeActivity?.Context ?? telemetry.Context;
        var capabilityStarted = false;
        var executeActivityCompleted = false;

        try
        {
            InvokeReceived?.Invoke(_eventSender, request);
            capabilityStarted = true;
            var response = await capability.ExecuteAsync(request, cancellationToken);
            response.Id = request.Id;

            if (!activeInvocation.TryComplete())
            {
                if (activeInvocation.CancelledByCaller)
                {
                    await SendCancellationResponseAndCompleteTelemetryAsync(
                        request,
                        telemetry,
                        executeActivity,
                        executeActivityCompleted,
                        sendErrorResponse,
                        stopwatch);
                }
                else
                {
                    NodeToolInvocation.CompleteChild(
                        executeActivity,
                        NodeToolOutcome.Canceled,
                        NodeToolErrorCategory.Other);
                    CompleteTelemetry(
                        telemetry,
                        NodeToolOutcome.Canceled,
                        NodeToolErrorCategory.Other);
                }
                return;
            }

            var diagnostic = response.Diagnostic;
            var outcome = diagnostic != null || !response.Ok
                ? NodeToolOutcome.Failure
                : NodeToolOutcome.Success;
            var category = diagnostic?.ErrorCategory ??
                (response.Ok ? NodeToolErrorCategory.None : NodeToolErrorCategory.CapabilityFailure);
            NodeToolInvocation.CompleteChild(
                executeActivity,
                outcome,
                category,
                diagnostic?.ExecutionMode,
                sandboxDenialReason: diagnostic?.SandboxDenialReason);
            executeActivityCompleted = true;

            try
            {
                await sendResponse(response);
                CompleteTelemetry(telemetry, outcome, category, diagnostic?.ExecutionMode);
            }
            catch (Exception sendEx)
            {
                _logger.Debug($"[NODE] Failed to deliver completed invoke {request.Id}: {sendEx.Message}");
                CompleteTelemetry(
                    telemetry,
                    NodeToolOutcome.Failure,
                    NodeToolErrorCategory.TransportFailure,
                    errorType: sendEx.GetType());
            }

            stopwatch.Stop();
            RaiseInvokeCompleted(request, response.Ok, response.Error, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (activeInvocation.CancelledByCaller)
        {
            await SendCancellationResponseAndCompleteTelemetryAsync(
                request,
                telemetry,
                executeActivity,
                executeActivityCompleted,
                sendErrorResponse,
                stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!executeActivityCompleted)
            {
                NodeToolInvocation.CompleteChild(
                    executeActivity,
                    NodeToolOutcome.Canceled,
                    NodeToolErrorCategory.Other);
            }
            CompleteTelemetry(telemetry, NodeToolOutcome.Canceled, NodeToolErrorCategory.Other);
        }
        catch (Exception ex)
        {
            if (!activeInvocation.TryComplete())
            {
                if (activeInvocation.CancelledByCaller)
                {
                    await SendCancellationResponseAndCompleteTelemetryAsync(
                        request,
                        telemetry,
                        executeActivity,
                        executeActivityCompleted,
                        sendErrorResponse,
                        stopwatch);
                }
                else
                {
                    if (!executeActivityCompleted)
                    {
                        NodeToolInvocation.CompleteChild(
                            executeActivity,
                            NodeToolOutcome.Canceled,
                            NodeToolErrorCategory.Other);
                    }
                    CompleteTelemetry(
                        telemetry,
                        NodeToolOutcome.Canceled,
                        NodeToolErrorCategory.Other);
                }
                return;
            }

            var category = capabilityStarted
                ? NodeToolErrorCategory.CapabilityFailure
                : NodeToolErrorCategory.InternalFailure;
            if (!executeActivityCompleted)
            {
                NodeToolInvocation.CompleteChild(
                    executeActivity,
                    NodeToolOutcome.Failure,
                    category,
                    errorType: ex.GetType());
            }
            _logger.Error($"Command execution failed: {request.Command}", ex);

            try
            {
                await sendErrorResponse("Command execution failed");
                CompleteTelemetry(
                    telemetry,
                    NodeToolOutcome.Failure,
                    category,
                    errorType: ex.GetType());
            }
            catch (Exception sendEx)
            {
                _logger.Debug($"[NODE] Failed to send error response for {request.Id}: {sendEx.Message}");
                CompleteTelemetry(
                    telemetry,
                    NodeToolOutcome.Failure,
                    NodeToolErrorCategory.TransportFailure,
                    errorType: sendEx.GetType());
            }

            stopwatch.Stop();
            RaiseInvokeCompleted(request, false, "Command execution failed", stopwatch.Elapsed);
        }
        finally
        {
            _invokeSemaphore.Release();
        }
    }

    private async Task SendCancellationResponseAndCompleteTelemetryAsync(
        NodeInvokeRequest request,
        NodeToolInvocation telemetry,
        Activity? executeActivity,
        bool executeActivityCompleted,
        Func<string, Task> sendErrorResponse,
        Stopwatch stopwatch)
    {
        if (!executeActivityCompleted)
        {
            NodeToolInvocation.CompleteChild(
                executeActivity,
                NodeToolOutcome.Canceled,
                NodeToolErrorCategory.Other);
        }

        try
        {
            await sendErrorResponse("cancelled");
            CompleteTelemetry(telemetry, NodeToolOutcome.Canceled, NodeToolErrorCategory.Other);
        }
        catch (Exception sendEx)
        {
            _logger.Debug($"[NODE] Failed to send cancellation response for {request.Id}: {sendEx.Message}");
            CompleteTelemetry(
                telemetry,
                NodeToolOutcome.Failure,
                NodeToolErrorCategory.TransportFailure,
                errorType: sendEx.GetType());
        }

        stopwatch.Stop();
        RaiseInvokeCompleted(request, false, "cancelled", stopwatch.Elapsed);
    }

    private void RaiseInvokeCompleted(
        NodeInvokeRequest request,
        bool ok,
        string? error,
        TimeSpan duration)
    {
        var handlers = InvokeCompleted;
        if (handlers is null)
            return;

        var args = new NodeInvokeCompletedEventArgs
        {
            RequestId = request.Id,
            Command = request.Command,
            Ok = ok,
            Error = error,
            Duration = duration,
            NodeId = _nodeId()
        };

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<NodeInvokeCompletedEventArgs>)handler)(_eventSender, args);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[NODE] InvokeCompleted subscriber " +
                    $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw: {ex.Message}");
            }
        }
    }

    private sealed record CommandDispatchEntry(
        INodeCapability Capability,
        string CanonicalName);
}
