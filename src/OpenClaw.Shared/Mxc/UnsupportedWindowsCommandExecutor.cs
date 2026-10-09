using System.Diagnostics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>Direct approved-argv compatibility execution, selected only by a positive unsupported-Windows verdict.</summary>
internal static class UnsupportedWindowsCommandExecutor
{
    internal static async Task<CommandResult> RunAsync(
        CommandRequest command, string scratch, long outputBudget, CancellationToken stop)
    {
        if (command.Env is { Count: > 0 })
            throw new NotSupportedException("Custom environment variables are not bound to command approval.");
        if (command.Argv is not { Count: > 0 })
            throw new NotSupportedException("Approved executable argv is required.");
        var image = MxcRequestBuilder.ValidateExecutable(command.Argv[0]);
        if (Path.GetExtension(image).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(image).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Batch files require an explicitly approved canonical cmd carrier.");
        var argv = command.Argv.ToArray();
        argv[0] = image;
        var line = MxcRequestBuilder.RenderArgv(argv);
        if (!string.IsNullOrWhiteSpace(command.Cwd) && !Path.IsPathFullyQualified(command.Cwd))
            throw new NotSupportedException("The approved working folder must be fully qualified.");
        var info = new ProcessStartInfo(image)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(command.Cwd) ? scratch : Path.GetFullPath(command.Cwd),
        };
        // cmd parses its raw command tail, not CommandLineToArgvW. Preserve the already-approved carrier.
        if (OpenClaw.Shared.Commands.CanonicalCmdCarrier.IsCmdExecutable(image))
            info.Arguments = line.Length > MxcRequestBuilder.Quote(image).Length
                ? line[(MxcRequestBuilder.Quote(image).Length + 1)..] : "";
        else
            foreach (var argument in argv.Skip(1)) info.ArgumentList.Add(argument);
        stop.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("The approved process could not be started.");
        Task? completion = null;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Register(() => cancelled.TrySetResult());
        try
        {
            process.StandardInput.Close();
            var output = MxcCommandRunner.CollectAsync(process.StandardOutput.BaseStream, outputBudget);
            var error = MxcCommandRunner.CollectAsync(process.StandardError.BaseStream, outputBudget);
            var wait = process.WaitForExitAsync(CancellationToken.None);
            completion = Task.WhenAll((Task)wait, output, error);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var task in new Task[] { wait, output, error })
                _ = task.ContinueWith(_ => failed.TrySetResult(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            if (await Task.WhenAny(completion, cancelled.Task, failed.Task).ConfigureAwait(false) != completion ||
                stop.IsCancellationRequested)
                await TerminateAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            return new()
            {
                Stdout = await output.ConfigureAwait(false), Stderr = await error.ConfigureAwait(false),
                ExitCode = process.ExitCode, ExecutionMode = NodeToolExecutionMode.Host,
                ErrorCategory = process.ExitCode == 0 ? NodeToolErrorCategory.None : NodeToolErrorCategory.CommandFailed,
            };
        }
        finally
        {
            // Retain the outer runner's ownership slot and scratch until host pipes and wait have completed.
            if (!process.HasExited) await TerminateAsync().ConfigureAwait(false);
            if (completion is not null)
            {
                try { await completion.ConfigureAwait(false); }
                catch (Exception) when (stop.IsCancellationRequested) { }
            }
        }

        Task TerminateAsync() => Task.Run(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally
            {
                try { process.StandardOutput.Dispose(); }
                finally { process.StandardError.Dispose(); }
            }
        });
    }
}
