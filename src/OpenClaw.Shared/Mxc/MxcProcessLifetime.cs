using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>Borrowed binary pipes, valid only during the protocol callback. The executor owns disposal.</summary>
public sealed record NodeSandboxProcessIo(
    Stream? StandardInput, Stream? StandardOutput, Stream? StandardError,
    Task<int> RootExit, NodeToolExecutionMode ExecutionMode,
    NodeSandboxPolicy Policy, NodeSandboxResources? Resources)
{
    private readonly List<Task> _pipeWorkers = [];
    internal Action? InputCloser { get; init; }
    internal void CloseInput()
    {
        if (InputCloser is not null) InputCloser();
        else StandardInput?.Dispose();
    }
    internal void OwnPipeWorkers(Task workers)
    {
        lock (_pipeWorkers) _pipeWorkers.Add(workers);
    }
    internal Task SettlePipeWorkersAsync()
    {
        lock (_pipeWorkers) return Task.WhenAll(_pipeWorkers);
    }
}

internal sealed record NodeSandboxIoOperation<T>(
    Task Ready, Task Settled, Action Interrupt, Func<T> Result);

/// <summary>One process/pipe lifetime for command text and fixed-worker binary protocols.</summary>
internal static class MxcProcessLifetime
{
    internal static async Task<T> RunAsync<T>(
        OwnedProcess process, NodeSandboxPolicy policy, NodeSandboxResources? resources,
        Func<NodeSandboxProcessIo, CancellationToken, NodeSandboxIoOperation<T>> startIo,
        CancellationToken stop, Action? settled = null)
    {
        using (process)
        using (var ioStop = CancellationTokenSource.CreateLinkedTokenSource(stop))
        using (var observationStop = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            var rootExit = ObserveRootExitAsync(process, observationStop.Token);
            NodeSandboxIoOperation<T>? io = null;
            try
            {
                io = startIo(new(process.StandardInput, process.StandardOutput, process.StandardError,
                    rootExit, process.Mode, policy, resources) { InputCloser = process.CloseInput }, ioStop.Token);
                await io.Ready.WaitAsync(stop).ConfigureAwait(false);
            }
            finally
            {
                // Native finalization holds the SDK control lock. Settle the non-finalizing
                // observer before either Kill or WaitAsync takes that lock.
                observationStop.Cancel();
                try { await rootExit.ConfigureAwait(false); }
                catch (OperationCanceledException) when (observationStop.IsCancellationRequested) { }
                finally
                {
                    io?.Interrupt();
                    await ioStop.CancelAsync().ConfigureAwait(false);
                    var close = Task.Run(process.CloseOutput);
                    var kill = rootExit.IsCompletedSuccessfully
                        ? Task.CompletedTask : Task.Run(process.Kill);
                    try { await Task.WhenAll(close, kill).ConfigureAwait(false); }
                    finally
                    {
                        // Retain every pipe worker even when closing, killing or waiting fails.
                        var wait = process.WaitAsync();
                        try
                        {
                            await Task.WhenAll(wait, io?.Settled ?? Task.CompletedTask,
                                io?.Ready ?? Task.CompletedTask).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (wait.IsCompletedSuccessfully && (io?.Settled.IsCompleted ?? true) &&
                                (io?.Ready.IsCompleted ?? true)) settled?.Invoke();
                        }
                    }
                }
            }
            return io.Result();
        }
    }

    private static async Task<int> ObserveRootExitAsync(OwnedProcess process, CancellationToken stop)
    {
        var pollMs = 1;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            if (process.TryGetExitCode(out var exitCode)) return exitCode;
            await Task.Delay(pollMs, stop).ConfigureAwait(false);
            pollMs = Math.Min(pollMs * 2, 50);
        }
    }

    internal abstract class OwnedProcess : IDisposable
    {
        internal abstract NodeToolExecutionMode Mode { get; }
        internal abstract Stream? StandardInput { get; }
        internal abstract Stream? StandardOutput { get; }
        internal abstract Stream? StandardError { get; }
        internal abstract bool TryGetExitCode(out int code);
        internal abstract Task WaitAsync();
        internal abstract void Kill();
        internal virtual void CloseInput() => StandardInput?.Dispose();
        internal abstract void CloseOutput();
        public abstract void Dispose();
    }

    internal sealed class NativeProcess(IMxcProcess process, IOpenClawLogger logger) : OwnedProcess
    {
        internal override NodeToolExecutionMode Mode => NodeToolExecutionMode.Sandbox;
        internal override Stream? StandardInput => process.StandardInput;
        internal override Stream? StandardOutput => process.StandardOutput;
        internal override Stream? StandardError => process.StandardError;
        internal override bool TryGetExitCode(out int code) => process.TryGetExitCode(out code);
        internal override async Task WaitAsync()
        {
            var result = await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            logger.Info($"[mxc] operation=exit applied=unknown exit={result.ExitCode} timedOut={result.TimedOut}");
        }
        internal override void Kill() => process.Kill();
        internal override void CloseOutput()
        {
            try { process.StandardOutputCloser?.Close(); }
            finally { process.StandardErrorCloser?.Close(); }
        }
        public override void Dispose()
        {
            using (process)
            using (process.StandardInput)
            using (process.StandardOutput)
            using (process.StandardError)
            using (process.StandardOutputCloser)
            using (process.StandardErrorCloser) { }
        }
    }

    internal sealed class HostProcess(Process process) : OwnedProcess
    {
        internal override NodeToolExecutionMode Mode => NodeToolExecutionMode.Host;
        internal override Stream StandardInput => process.StandardInput.BaseStream;
        internal override Stream StandardOutput => process.StandardOutput.BaseStream;
        internal override Stream StandardError => process.StandardError.BaseStream;
        internal override bool TryGetExitCode(out int code)
        {
            var exited = process.HasExited;
            code = exited ? process.ExitCode : 0;
            return exited;
        }
        internal override Task WaitAsync() => process.WaitForExitAsync(CancellationToken.None);
        // Close the owner, not its borrowed BaseStream: StreamWriter.Dispose must not flush a closed pipe.
        internal override void CloseInput() => process.StandardInput.Close();
        internal override void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        // Host asynchronous pipe reads are interrupted by ioStop, not concurrent Dispose.
        internal override void CloseOutput() { }
        public override void Dispose()
        {
            using (process)
            using (process.StandardInput)
            using (process.StandardOutput)
            using (process.StandardError) { }
        }
    }
}
