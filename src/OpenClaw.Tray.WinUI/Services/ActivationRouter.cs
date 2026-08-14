using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal sealed class ActivationRouter : IAsyncDisposable
{
    private readonly string _protocolScheme;
    private readonly string _pipeName;
    private readonly object _lifecycleGate = new();
    private readonly object _dispatchGate = new();
    private readonly HashSet<DispatchOperation> _pendingDispatches = new();
    private readonly CancellationTokenSource _dispatchLifetimeCts = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private Task? _stopTask;
    private bool _acceptingDispatches = true;

    private sealed class DispatchOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<bool>? Task { get; set; }
    }

    public ActivationRouter(string protocolScheme, string pipeName)
    {
        _protocolScheme = protocolScheme;
        _pipeName = pipeName;
    }

    public ActivationPlan PlanLaunch(LaunchActivationInput input)
    {
        if (input.SetupShownDuringStartup)
            return new ActivationPlan.Ignore();

        var candidate = ResolveLaunchCandidate(input);
        return candidate == null ? new ActivationPlan.Ignore() : PlanFromUri(candidate);
    }

    public string? ResolveLaunchCandidate(LaunchActivationInput input)
    {
        if (!string.IsNullOrEmpty(input.ProtocolUri))
            return input.ProtocolUri;

        if (input.CommandLineArguments.Count > 1 && IsDeepLinkArg(input.CommandLineArguments[1]))
            return input.CommandLineArguments[1];

        return string.Equals(input.PostSetupLaunch, "chat", StringComparison.OrdinalIgnoreCase)
            ? $"{_protocolScheme}://chat"
            : null;
    }

    public ActivationPlan PlanToast(string? argument)
    {
        var arguments = ToastArguments.Parse(argument);
        var action = arguments.TryGetValue("action", out var actionValue) ? actionValue : null;
        var route = ToastActivationRouter.PlanRoute(
            action,
            key => arguments.TryGetValue(key, out var value) ? value : null);
        return route == null ? new ActivationPlan.Ignore() : new ActivationPlan.Dispatch(route);
    }

    public Task<bool> DispatchPlanAsync(
        ActivationPlan plan,
        IActivationPlanSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (!_acceptingDispatches)
                return Task.FromResult(false);

            var operation = new DispatchOperation(
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _dispatchLifetimeCts.Token));
            lock (_dispatchGate)
            {
                _pendingDispatches.Add(operation);
            }

            operation.Task = DispatchPlanTrackedAsync(plan, sink, operation);
            return operation.Task;
        }
    }

    private async Task<bool> DispatchPlanTrackedAsync(
        ActivationPlan plan,
        IActivationPlanSink sink,
        DispatchOperation operation)
    {
        try
        {
            return await DispatchPlanCoreAsync(
                plan,
                sink,
                operation.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            operation.Cancellation.Dispose();
            lock (_dispatchGate)
            {
                _pendingDispatches.Remove(operation);
            }
        }
    }

    private static async Task<bool> DispatchPlanCoreAsync(
        ActivationPlan plan,
        IActivationPlanSink sink,
        CancellationToken cancellationToken)
    {
        switch (plan)
        {
            case ActivationPlan.Dispatch dispatch:
                await sink.DispatchAsync(dispatch.Route, cancellationToken).ConfigureAwait(false);
                return true;

            case ActivationPlan.Confirm confirm:
                var confirmed = await sink.ConfirmAsync(confirm.Prompt, cancellationToken).ConfigureAwait(false);
                if (!confirmed)
                {
                    Logger.Warn($"Rejected unconfirmed deep link action: {confirm.Prompt.RedactedInput}");
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await sink.DispatchAsync(confirm.Route, cancellationToken).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    public Task StartForwardedActivationListenerAsync(IActivationPlanSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(!_acceptingDispatches, this);
            if (_listenerTask != null)
                return Task.CompletedTask;

            _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _listenerCts.Token;
            _listenerTask = Task.Run(() => RunListenerLoopAsync(sink, token), token);
        }

        return Task.CompletedTask;
    }

    private async Task RunListenerLoopAsync(IActivationPlanSink sink, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: DeepLinkSecurityPolicy.MaxIpcMessageBytes,
                    outBufferSize: 0);
                await pipe.WaitForConnectionAsync(token);
                var uri = await ReadIpcPayloadAsync(pipe, token);
                if (!string.IsNullOrEmpty(uri))
                {
                    Logger.Info($"Received deep link via IPC: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                    var plan = PlanFromUri(uri);
                    _ = ObserveForwardedDispatchAsync(
                        DispatchPlanAsync(plan, sink, token),
                        token);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Deep link server stopping (canceled)");
                break; // Normal shutdown
            }
            catch (InvalidDataException ex)
            {
                if (!token.IsCancellationRequested)
                    Logger.Warn($"Rejected deep link IPC payload: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                if (!token.IsCancellationRequested)
                    Logger.Warn($"Rejected deep link IPC payload: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Logger.Warn($"Deep link server error: {ex.Message}");
                    try { await Task.Delay(1000, token); }
                    catch (OperationCanceledException) { break; } // Expected: server cancelled, exit loop.
                    catch (Exception delayEx)
                    {
                        // Defensive: keep the loop resilient even if future code adds awaits that throw other types.
                        Logger.Debug($"ActivationRouter: Deep link server delay failed: {delayEx.GetType().Name}: {delayEx.Message}");
                        break;
                    }
                }

            }
        }
    }

    private static async Task ObserveForwardedDispatchAsync(
        Task dispatchTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await dispatchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal listener shutdown.
        }
        catch (Exception ex)
        {
            Logger.Error($"ActivationRouter: forwarded activation dispatch failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task<bool> ForwardToPrimaryAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            if (!DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(uri))
            {
                Logger.Warn($"Rejected oversized deep link before IPC forwarding: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                return false;
            }

            if (DeepLinkParser.ParseDeepLink(uri, _protocolScheme) == null)
            {
                Logger.Warn($"Rejected invalid deep link before IPC forwarding: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                return false;
            }

            var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetBytes(uri);
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            pipe.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to forward deep link: {ex.Message}");
            return false;
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_stopTask != null)
                return _stopTask;

            _acceptingDispatches = false;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            stopTask = _stopTask;
        }

        _ = CompleteStopAsync(completion);
        return stopTask;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? listenerCts;
        Task? listenerTask;
        lock (_lifecycleGate)
        {
            listenerCts = _listenerCts;
            listenerTask = _listenerTask;
        }

        try { _dispatchLifetimeCts.Cancel(); }
        catch (Exception ex) { Logger.Warn($"Shutdown: activation dispatch cancel failed: {ex.Message}"); }

        if (listenerCts != null)
        {
            try { listenerCts.Cancel(); }
            catch (Exception ex) { Logger.Warn($"Shutdown: deep link cancel failed: {ex.Message}"); }
        }

        if (listenerTask != null)
        {
            try { await listenerTask.ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn($"Shutdown: deep link server task failed to stop: {ex.Message}"); }
        }

        Task[] pendingDispatches;
        lock (_dispatchGate)
        {
            pendingDispatches = new Task[_pendingDispatches.Count];
            var index = 0;
            foreach (var operation in _pendingDispatches)
                pendingDispatches[index++] = operation.Task!;
        }

        if (pendingDispatches.Length > 0)
        {
            try { await Task.WhenAll(pendingDispatches).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_dispatchLifetimeCts.IsCancellationRequested)
            {
                // Expected: stop cancels every admitted dispatch before draining it.
            }
            catch (Exception ex) { Logger.Warn($"Shutdown: activation dispatch failed to stop: {ex.Message}"); }
        }

        lock (_lifecycleGate)
        {
            listenerCts?.Dispose();
            _listenerCts = null;
            _listenerTask = null;
        }
        _dispatchLifetimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private ActivationPlan PlanFromUri(string uri)
    {
        var result = DeepLinkParser.ParseDeepLink(uri, _protocolScheme);
        if (result == null)
        {
            var redacted = DeepLinkSecurityPolicy.RedactForLog(uri);
            Logger.Warn($"Rejected invalid deep link: {redacted}");
            return new ActivationPlan.Ignore();
        }

        var route = DeepLinkHandler.PlanRoute(uri, _protocolScheme);
        if (route == null)
            return new ActivationPlan.Ignore();

        if (!DeepLinkSecurityPolicy.RequiresConfirmation(result))
            return new ActivationPlan.Dispatch(route);

        var prompt = new ActivationConfirmation(
            DeepLinkSecurityPolicy.GetActionDisplayName(result),
            DeepLinkSecurityPolicy.RedactForLog(uri));
        return new ActivationPlan.Confirm(route, prompt);
    }

    private bool IsDeepLinkArg(string arg) => DeepLinkParser.ParseDeepLink(arg, _protocolScheme) != null;

    private static async Task<string?> ReadIpcPayloadAsync(Stream stream, CancellationToken appToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        readCts.CancelAfter(DeepLinkSecurityPolicy.IpcReadTimeout);

        var scratch = new byte[1024];
        var payload = new byte[DeepLinkSecurityPolicy.MaxIpcMessageBytes + 1];
        var totalBytes = 0;

        try
        {
            while (true)
            {
                var remaining = payload.Length - totalBytes;
                if (remaining <= 0)
                    throw new InvalidDataException("payload exceeds maximum size");

                var read = await stream.ReadAsync(
                    scratch.AsMemory(0, Math.Min(scratch.Length, remaining)),
                    readCts.Token);
                if (read == 0)
                    break;

                scratch.AsSpan(0, read).CopyTo(payload.AsSpan(totalBytes));
                totalBytes += read;
                if (totalBytes > DeepLinkSecurityPolicy.MaxIpcMessageBytes)
                    throw new InvalidDataException("payload exceeds maximum size");
            }
        }
        catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
        {
            throw new TimeoutException("timed out while reading payload");
        }

        if (totalBytes == 0)
            return null;

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload, 0, totalBytes)
                .TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("payload is not valid UTF-8", ex);
        }
    }
}
