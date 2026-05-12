using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Implements <see cref="ISandboxExecutor"/> by spawning <c>node.exe</c> with
/// <c>tools/mxc/run-command.cjs</c>, which calls
/// <c>@microsoft/mxc-sdk.spawnSandboxFromConfig({usePty:false})</c> to run the
/// payload inside a one-shot AppContainer.
/// </summary>
public sealed class OneShotAppContainerExecutor : ISandboxExecutor
{
    public string Name => "mxc-oneshot-appc";
    public bool IsContained => true;

    private readonly MxcAvailability _availability;
    private readonly string _runCommandScriptPath;
    private readonly string _nodeExecutablePath;
    private readonly IOpenClawLogger _logger;

    /// <summary>Default cap on stdout/stderr returned to the host (4 MiB).</summary>
    public const long DefaultMaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Optional environment variable override for the Node executable used by the
    /// runner. Falls back to <c>node.exe</c> on PATH.
    /// </summary>
    public const string NodeExecutableOverrideEnvVar = "OPENCLAW_NODE_EXEC";

    public OneShotAppContainerExecutor(
        MxcAvailability availability,
        string runCommandScriptPath,
        IOpenClawLogger? logger = null,
        string? nodeExecutableOverride = null)
    {
        _availability = availability;
        _runCommandScriptPath = runCommandScriptPath;
        _logger = logger ?? NullLogger.Instance;
        _nodeExecutablePath = nodeExecutableOverride
            ?? Environment.GetEnvironmentVariable(NodeExecutableOverrideEnvVar)
            ?? "node.exe";
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default)
    {
        if (!_availability.IsAppContainerAvailable)
            throw new SandboxUnavailableException(
                _availability.UnsupportedReasons.FirstOrDefault() ?? "AppContainer unavailable");

        if (!_availability.IsWxcExecResolvable)
            throw new SandboxUnavailableException("wxc-exec.exe not found");

        if (!File.Exists(_runCommandScriptPath))
            throw new SandboxUnavailableException(
                $"run-command.cjs not found at {_runCommandScriptPath}");

        // Per-request output cap. Default applies only when the caller doesn't
        // pass one. Used to be baked at construction; that caused stale floors
        // when the user lowered SandboxMaxOutputBytes after the executor was
        // built (Math.Max(stale, new) kept the larger old value).
        var capBytes = request.MaxOutputBytes is > 0 ? request.MaxOutputBytes.Value : DefaultMaxOutputBytes;

        var bridgeRequest = new BridgeRequest(
            CapabilityCommand: request.CapabilityCommand,
            Args: request.Args,
            Policy: request.Policy,
            Cwd: request.Cwd,
            Env: request.Env,
            TimeoutMs: request.TimeoutMs,
            MaxOutputBytes: capBytes,
            WxcExecPath: _availability.WxcExecPath);

        var requestJson = JsonSerializer.Serialize(bridgeRequest, BridgeJson);

        var psi = new ProcessStartInfo
        {
            FileName = _nodeExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(_runCommandScriptPath);

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SandboxUnavailableException(
                $"Failed to start node.exe at '{_nodeExecutablePath}': {ex.Message}", ex);
        }

        // Caller-controlled timeout governs how long the bridge has to return.
        // Add a small grace so the bridge can clean up before we kill it.
        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs + 5000 : 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            cts.CancelAfter(timeoutMs);

        try
        {
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException)
        {
            // Either the caller cancelled or the timeout fired — in both cases
            // the spawned node + sandboxed payload must be killed so we don't
            // leak processes after the host gives up.
            KillProcessTree(process);
            throw;
        }

        // Envelope cap: caller-cap covers stdout AND stderr each. Allow up to
        // 2× that plus envelope/JSON overhead so a worst-case bridge response
        // (large stdout + large stderr) still fits without truncation.
        var envelopeCap = (capBytes * 2L) + (256L * 1024L);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, envelopeCap, cts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, envelopeCap, cts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // ALWAYS kill the process tree on cancellation, whether the source is
            // the caller's CancellationToken (agent abort) or our local timeout.
            // Without this the sandboxed payload keeps running after we return.
            KillProcessTree(process);

            // Distinguish caller cancel from local-timeout for the return path.
            if (!ct.IsCancellationRequested)
                timedOut = true;
            else
                throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        sw.Stop();

        if (timedOut)
        {
            return new SandboxExecutionResult(
                ExitCode: -1,
                Stdout: stdout,
                Stderr: stderr.Length > 0 ? stderr : "Sandboxed invocation timed out.",
                TimedOut: true,
                DurationMs: sw.ElapsedMilliseconds,
                ContainmentTag: "mxc",
                StructuredResult: null);
        }

        // Bridge writes a single JSON envelope to stdout on completion.
        if (TryParseBridgeResponse(stdout, out var response))
        {
            return new SandboxExecutionResult(
                ExitCode: response.ExitCode,
                Stdout: response.Stdout,
                Stderr: response.Stderr,
                TimedOut: response.TimedOut,
                DurationMs: response.DurationMs == 0 ? sw.ElapsedMilliseconds : response.DurationMs,
                ContainmentTag: response.ContainmentTag ?? "mxc",
                StructuredResult: response.StructuredResult);
        }

        // Bridge crashed or returned malformed output. Surface as a sandbox failure
        // — node-side stderr likely has the diagnostic.
        _logger.Warn($"[mxc] bridge returned malformed output ({stdout.Length} bytes); stderr={Truncate(stderr, 200)}");
        return new SandboxExecutionResult(
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            TimedOut: false,
            DurationMs: sw.ElapsedMilliseconds,
            ContainmentTag: "mxc",
            StructuredResult: null);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, long maxBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        long bytesRead = 0;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, ct); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }

            if (read == 0)
                break;

            // Approximate cap: chars × 2 bytes upper bound for UTF-16.
            bytesRead += read * 2;
            sb.Append(buffer, 0, read);
            if (bytesRead >= maxBytes)
            {
                sb.Append("\n[output truncated]");
                break;
            }
        }
        return sb.ToString();
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort */ }
    }

    private static bool TryParseBridgeResponse(string json, out BridgeResponse response)
    {
        response = default!;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            response = JsonSerializer.Deserialize<BridgeResponse>(json.Trim(), BridgeJson)!;
            return response is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static readonly JsonSerializerOptions BridgeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            // Enums must serialize as camelCase strings so @microsoft/mxc-sdk
            // (which expects "none" / "read" / "write" / "all") accepts them.
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase),
        },
    };

    private sealed record BridgeRequest(
        string CapabilityCommand,
        JsonElement Args,
        SandboxPolicy Policy,
        string? Cwd,
        IReadOnlyDictionary<string, string>? Env,
        int TimeoutMs,
        long MaxOutputBytes,
        string? WxcExecPath);

    private sealed record BridgeResponse(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        long DurationMs,
        string? ContainmentTag,
        JsonElement? StructuredResult);
}
