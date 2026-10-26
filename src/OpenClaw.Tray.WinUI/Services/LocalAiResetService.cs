using System.Diagnostics;
using System.Text.Json;
using OpenClawTray.Presentation;
using OpenClaw.Connection.LocalAi;

namespace OpenClawTray.Services;


/// <summary>
/// Owns the tray-side half of a Local AI reset. The live runtime and any native
/// ownership receipt must be released before the isolated setup-engine process
/// can remove the managed LocalAI tree.
/// </summary>
internal sealed class LocalAiResetService
{
    /// <summary>Tray and SetupEngine CLI flag for the Local-AI-only destructive reset.</summary>
    public const string ResetLocalAiFlag = "--reset-local-ai";
    private const string ResetTimeoutEnvironmentVariable = "OPENCLAW_LOCAL_AI_RESET_TIMEOUT_SECONDS";
    private static readonly TimeSpan DefaultResetTimeout = TimeSpan.FromMinutes(2);

    private readonly ILocalAiRuntime _runtime;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _runProcess;
    private readonly Func<string?> _resolveExecutablePath;
    private readonly TimeSpan _timeout;

    /// <param name="runProcess">
    /// Runs the setup-engine child and returns its exit code. It must not complete until the
    /// child has exited: on cancellation it kills the child process tree and awaits its exit
    /// before throwing <see cref="OperationCanceledException"/>, so no destructive work can
    /// continue after the reset reports a result.
    /// </param>
    /// <param name="timeout">
    /// Per-phase reset timeout; defaults to <c>OPENCLAW_LOCAL_AI_RESET_TIMEOUT_SECONDS</c> or two minutes.
    /// </param>
    public LocalAiResetService(
        ILocalAiRuntime runtime,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runProcess = null,
        Func<string?>? resolveExecutablePath = null,
        TimeSpan? timeout = null)
    {
        _runtime = runtime;
        _runProcess = runProcess ?? RunProcessAsync;
        _resolveExecutablePath = resolveExecutablePath ?? ResolveCurrentExecutablePath;
        _timeout = timeout ?? GetResetTimeout();
    }

    /// <summary>
    /// Stops the tray-owned listener, releases native Gateway ownership through the
    /// runtime's existing guarded path, then lets the setup engine delete the managed
    /// tree. Any failure before the engine runs leaves every file and receipt in place.
    /// </summary>
    public async Task<LocalAiResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _runtime.StopAsync(cancellationToken).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            if (_runtime.HasReleasableOwnership)
                await _runtime.ReleaseOwnershipAsync(cancellationToken)
                    .WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(LocalAiResetOutcome.Cancelled, "Local AI reset was cancelled.");
        }
        catch (Exception ex)
        {
            return new(LocalAiResetOutcome.Failed, ex.Message);
        }

        var executablePath = _resolveExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
            return new(LocalAiResetOutcome.Failed, "OpenClaw tray executable could not be resolved for Local AI reset.");

        string jsonOutput = Path.Combine(Path.GetTempPath(), $"openclaw-local-ai-reset-{Guid.NewGuid():N}.json");
        using var engineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        engineCancellation.CancelAfter(_timeout);
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(ResetLocalAiFlag);
            startInfo.ArgumentList.Add("--confirm-destructive");
            startInfo.ArgumentList.Add("--json-output");
            startInfo.ArgumentList.Add(jsonOutput);
            // No WaitAsync here: abandoning the child would let it keep deleting files after
            // this method reports a result. The runner settles the child before returning.
            int exitCode = await _runProcess(startInfo, engineCancellation.Token).ConfigureAwait(false);
            return await ClassifyOutcomeAsync(exitCode, jsonOutput, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(LocalAiResetOutcome.Cancelled, "Local AI reset was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new(
                LocalAiResetOutcome.Failed,
                $"Local AI reset timed out after {_timeout.TotalSeconds:0} seconds and was stopped. Run Reset Local AI again to finish.");
        }
        catch (Exception ex)
        {
            return new(LocalAiResetOutcome.Failed, ex.Message);
        }
        finally
        {
            try { if (File.Exists(jsonOutput)) File.Delete(jsonOutput); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Killing the engine is safe: SetupRunLock is a FileShare.None handle the OS releases when
    // the process dies, and reset is re-runnable because the receipt is deleted only by the
    // last step, so a rerun re-proves Gateway ownership before touching anything.
    private static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Local AI reset process.");
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return process.ExitCode;
    }

    internal static async Task<LocalAiResetResult> ClassifyOutcomeAsync(int exitCode, string jsonOutput, CancellationToken ct)
    {
        string? message = null;
        try
        {
            await using var stream = File.OpenRead(jsonOutput);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("message", out JsonElement property) && property.ValueKind == JsonValueKind.String)
                message = property.GetString();
            if (root.TryGetProperty("outcome", out property) && property.ValueKind == JsonValueKind.String &&
                string.Equals(property.GetString(), "Cancelled", StringComparison.OrdinalIgnoreCase))
                return new(LocalAiResetOutcome.Cancelled, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            message = $"Local AI reset did not provide a usable result: {ex.Message}";
        }

        return exitCode == 0
            ? new(LocalAiResetOutcome.Success, message)
            : new(LocalAiResetOutcome.Failed, message ?? $"Local AI reset failed with exit code {exitCode}.");
    }

    private static TimeSpan GetResetTimeout()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(ResetTimeoutEnvironmentVariable), out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return DefaultResetTimeout;
    }

    private static string? ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)) return Environment.ProcessPath;
        try { return Process.GetCurrentProcess().MainModule?.FileName; }
        catch { return null; }
    }
}
