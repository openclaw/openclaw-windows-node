using System.Diagnostics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>Direct approved-argv compatibility execution, selected only by a positive unsupported-Windows verdict.</summary>
internal static class UnsupportedWindowsCommandExecutor
{
    internal static async Task<CommandResult> RunAsync(
        CommandRequest command, string scratch, CommandOutputCapture capture, CancellationToken stop)
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
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            process.StandardInput.Close();
            var output = capture.StandardOutput.CollectAsync(process.StandardOutput.BaseStream, readers.Token);
            var error = capture.StandardError.CollectAsync(process.StandardError.BaseStream, readers.Token);
            var drains = Task.WhenAll(output, error);
            var wait = process.WaitForExitAsync(CancellationToken.None);
            capture.BindRootExit(wait, () => (process.ExitCode, false));
            completion = Task.WhenAll(wait, drains);
            await CommandOutputCapture.WaitForExitOrInterruptionAsync(wait, output, error, stop).ConfigureAwait(false);
            if (wait.IsCompletedSuccessfully)
                await CommandOutputCapture.DrainAfterExitAsync(drains, stop).ConfigureAwait(false);
            else
                await TerminateAsync().ConfigureAwait(false);
            capture.StandardOutput.Interrupt();
            capture.StandardError.Interrupt();
            await readers.CancelAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
            return capture.Snapshot(NodeToolExecutionMode.Host);
        }
        finally
        {
            // Retain the outer runner's ownership slot and scratch until host pipes and wait have completed.
            try
            {
                if (!process.HasExited) await TerminateAsync().ConfigureAwait(false);
            }
            finally
            {
                capture.StandardOutput.Interrupt();
                capture.StandardError.Interrupt();
                await readers.CancelAsync().ConfigureAwait(false);
                try
                {
                    if (completion is not null) await completion.ConfigureAwait(false);
                }
                catch (Exception) when (stop.IsCancellationRequested) { }
                finally { capture.CaptureRootExit(); }
            }
        }

        Task TerminateAsync() => Task.Run(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
    }
}
