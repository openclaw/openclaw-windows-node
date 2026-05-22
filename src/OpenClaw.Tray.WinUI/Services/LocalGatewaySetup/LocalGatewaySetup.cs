using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Services;
#endif

namespace OpenClawTray.Services.LocalGatewaySetup;

public enum LocalGatewaySetupPhase
{
    NotStarted,
    Preflight,
    ElevationCheck,
    EnsureWslEnabled,
    CreateWslInstance,
    ConfigureWslInstance,
    InstallOpenClawCli,
    PrepareGatewayConfig,
    InstallGatewayService,
    StartGateway,
    WaitForGateway,
    MintBootstrapToken,
    PairOperator,
    CheckWindowsNodeReadiness,
    PairWindowsTrayNode,
    VerifyEndToEnd,
    Complete,
    Failed,
    Cancelled
}

public enum LocalGatewaySetupStatus
{
    Pending,
    Running,
    RequiresAdmin,
    RequiresRestart,
    Blocked,
    FailedRetryable,
    FailedTerminal,
    Complete,
    Cancelled
}

public enum LocalGatewaySetupSeverity
{
    Info,
    Warning,
    Blocking
}

public sealed record LocalGatewaySetupOptions
{
    public string DistroName { get; init; } = "OpenClawGateway";
    public string GatewayUrl { get; init; } = "ws://localhost:18789";
    public int GatewayPort { get; init; } = 18789;
    public string GatewayServiceName { get; init; } = "openclaw-gateway";
    public string BaseDistroName { get; init; } = "Ubuntu-24.04";
    public string? InstanceInstallLocation { get; init; }
    public string OpenClawInstallPrefix { get; init; } = "/opt/openclaw";
    public string OpenClawInstallVersion { get; init; } = "latest";
    public string OpenClawInstallMethod { get; init; } = "npm";
    public string OpenClawInstallerUrl { get; init; } = "https://openclaw.ai/install-cli.sh";
    public bool AllowExistingDistro { get; init; }
    public bool EnableWindowsTrayNodeByDefault { get; init; } = true;
}

public interface ILocalGatewaySetupEnvironment
{
    string? GetVariable(string name);
}

public sealed class ProcessLocalGatewaySetupEnvironment : ILocalGatewaySetupEnvironment
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed record LocalGatewaySetupRuntimeConfiguration(
    string? DistroName,
    bool AllowExistingDistro)
{
    public const string DistroNameVariable = "OPENCLAW_WSL_DISTRO_NAME";
    public const string AllowExistingDistroVariable = "OPENCLAW_WSL_ALLOW_EXISTING_DISTRO";

    public static LocalGatewaySetupRuntimeConfiguration FromEnvironment(ILocalGatewaySetupEnvironment? environment = null)
    {
        environment ??= new ProcessLocalGatewaySetupEnvironment();
        return new LocalGatewaySetupRuntimeConfiguration(
#if DEBUG || OPENCLAW_TRAY_TESTS
            NullIfWhiteSpace(environment.GetVariable(DistroNameVariable)),
#else
            null,
#endif
            IsTruthy(environment.GetVariable(AllowExistingDistroVariable)));
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record LocalGatewaySetupIssue(
    string Code,
    string Message,
    LocalGatewaySetupSeverity Severity,
    string? Detail = null);

/// <summary>
/// Issue codes that belong to the "WSL install cycle" — the warnings and
/// failures the engine emits while trying to install / approve / configure
/// the local WSL platform + distro. Listed in one place so that:
///  (a) the engine's on-entry self-heal can wipe them with a single source
///      of truth before re-running preflight, and
///  (b) the EnsureWslEnabled branches that cycle through Install / Block
///      states stay in sync — adding a new code only requires updating
///      this list, not 4 duplicated `Issues.RemoveAll` sites.
/// </summary>
internal static class WslInstallCycleIssueCodes
{
    public const string PlatformNotInstalled = "wsl_platform_not_installed";
    public const string InstallRequiresRestart = "wsl_install_requires_restart";
    public const string InstallFailed = "wsl_install_failed";
    public const string InstallElevationDeclined = "wsl_install_elevation_declined";
    public const string InstallUnavailable = "wsl_install_unavailable";
    public const string Unavailable = "wsl_unavailable";
    public const string PreflightBlocked = "preflight_blocked";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        PlatformNotInstalled,
        InstallRequiresRestart,
        InstallFailed,
        InstallElevationDeclined,
        InstallUnavailable,
        Unavailable,
        PreflightBlocked,
    };
}

public sealed record LocalGatewaySetupPhaseRecord(
    LocalGatewaySetupPhase Phase,
    LocalGatewaySetupStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc = null,
    string? Message = null);

public sealed class LocalGatewaySetupState
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public LocalGatewaySetupPhase Phase { get; set; } = LocalGatewaySetupPhase.NotStarted;
    public LocalGatewaySetupStatus Status { get; set; } = LocalGatewaySetupStatus.Pending;
    public string DistroName { get; set; } = "OpenClawGateway";
    public string GatewayUrl { get; set; } = "ws://localhost:18789";
    public bool IsLocalOnly { get; set; }
    public string? FailureCode { get; set; }
    public string? UserMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LocalGatewaySetupIssue> Issues { get; set; } = new();
    public List<LocalGatewaySetupPhaseRecord> History { get; set; } = new();

    public static LocalGatewaySetupState Create(LocalGatewaySetupOptions options)
    {
        return new LocalGatewaySetupState
        {
            DistroName = options.DistroName,
            GatewayUrl = LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(options)
        };
    }

    public void StartPhase(LocalGatewaySetupPhase phase, string? message = null)
    {
        Phase = phase;
        Status = LocalGatewaySetupStatus.Running;
        UserMessage = message;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        History.Add(new LocalGatewaySetupPhaseRecord(phase, Status, UpdatedAtUtc, Message: message));
    }

    public void CompletePhase(LocalGatewaySetupPhase phase, string? message = null)
    {
        Phase = phase;
        Status = phase == LocalGatewaySetupPhase.Complete
            ? LocalGatewaySetupStatus.Complete
            : LocalGatewaySetupStatus.Running;
        UserMessage = message;
        UpdatedAtUtc = DateTimeOffset.UtcNow;

        var index = History.FindLastIndex(x => x.Phase == phase && x.FinishedAtUtc is null);
        if (index >= 0)
        {
            var record = History[index];
            History[index] = record with { Status = Status, FinishedAtUtc = UpdatedAtUtc, Message = message ?? record.Message };
        }
    }

    public void Block(string code, string message, bool retryable = false, string? detail = null)
    {
        Phase = LocalGatewaySetupPhase.Failed;
        Status = retryable ? LocalGatewaySetupStatus.FailedRetryable : LocalGatewaySetupStatus.FailedTerminal;
        FailureCode = code;
        UserMessage = message;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        Issues.Add(new LocalGatewaySetupIssue(code, message, retryable ? LocalGatewaySetupSeverity.Warning : LocalGatewaySetupSeverity.Blocking, detail));
    }
}

public interface ILocalGatewaySetupStateStore
{
    Task<LocalGatewaySetupState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}

public sealed class LocalGatewaySetupStateStore : ILocalGatewaySetupStateStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly string _statePath;

    public LocalGatewaySetupStateStore(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray",
            "setup-state.json");
    }

    public async Task<LocalGatewaySetupState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<LocalGatewaySetupState>(stream, s_jsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // Corrupt or incompatible state file — treat as fresh start
            System.Diagnostics.Debug.WriteLine($"[SetupState] Corrupt setup-state.json at {_statePath}, deleting and starting fresh");
            try { File.Delete(_statePath); } catch { }
            return null;
        }
    }

    public async Task SaveAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, s_jsonOptions, cancellationToken);
    }
}

public sealed record WslCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed record WslDistroInfo(string Name, string State, int Version);
public sealed record WslStatusInfo(int? DefaultVersion, string? WslVersion, string? KernelVersion);

public interface IWslCommandRunner
{
    Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null);
    Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default);
    Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default);
    Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default);
    Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null);
}

public sealed class WslExeCommandRunner : IWslCommandRunner
{
    private readonly IOpenClawLogger _logger;
    private readonly ILocalGatewaySetupDiagnosticsSink _diagnostics;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _streamDrainTimeout;

    public WslExeCommandRunner(
        IOpenClawLogger? logger = null,
        TimeSpan? defaultTimeout = null,
        TimeSpan? streamDrainTimeout = null,
        ILocalGatewaySetupDiagnosticsSink? diagnostics = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _diagnostics = diagnostics ?? NullLocalGatewaySetupDiagnosticsSink.Instance;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        _streamDrainTimeout = streamDrainTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["--list", "--verbose"], cancellationToken);
        return result.Success ? ParseDistroList(result.StandardOutput) : [];
    }

    public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null) =>
        RunProcessAsync("wsl.exe", arguments, cancellationToken, environment);

    public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { "-d", name, "--" };
        args.AddRange(command);
        return RunAsync(args, cancellationToken, environment);
    }

    public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--terminate", name], cancellationToken);

    public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--unregister", name], cancellationToken);

    public static IReadOnlyList<WslDistroInfo> ParseDistroList(string output)
    {
        var distros = new List<WslDistroInfo>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace("\0", string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line[0] == '*')
                line = line[1..].TrimStart();

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                continue;

            var state = parts[^2];
            var name = string.Join(" ", parts.Take(parts.Length - 2));
            if (!string.IsNullOrWhiteSpace(name))
                distros.Add(new WslDistroInfo(name, state, version));
        }

        return distros;
    }

    public static WslStatusInfo ParseStatus(string output)
    {
        int? defaultVersion = null;
        string? wslVersion = null;
        string? kernelVersion = null;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace("\0", string.Empty).Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Default Version", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDefaultVersion))
            {
                defaultVersion = parsedDefaultVersion;
            }
            else if (key.Equals("WSL version", StringComparison.OrdinalIgnoreCase))
            {
                wslVersion = value;
            }
            else if (key.Equals("Kernel version", StringComparison.OrdinalIgnoreCase))
            {
                kernelVersion = value;
            }
        }

        return new WslStatusInfo(defaultVersion, wslVersion, kernelVersion);
    }

    private async Task<WslCommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        ApplyEnvironment(psi, environment);

        _logger.Info($"[WSL] {fileName} {string.Join(" ", RedactArguments(arguments))}");
        var commandId = _diagnostics.CommandStarted(fileName, arguments, _defaultTimeout);
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var result = new WslCommandResult(-1, string.Empty, $"Failed to start wsl.exe: {ex.Message}");
            sw.Stop();
            _diagnostics.CommandCompleted(commandId, fileName, arguments, sw.Elapsed, result, timedOut: false);
            return result;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_defaultTimeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[WSL] Failed to kill timed-out process: {ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled: kill wsl.exe and its descendants before propagating.
            // Without this, the Linux-side process tree continues running after setup
            // is aborted — issue #281 item #7.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[WSL] Failed to kill cancelled process: {ex.Message}");
            }
            sw.Stop();
            _diagnostics.CommandCompleted(
                commandId,
                fileName,
                arguments,
                sw.Elapsed,
                new WslCommandResult(-1, string.Empty, "wsl.exe cancelled"),
                timedOut: false);
            throw;
        }

        // Drain stdout/stderr with a bounded post-exit timeout. wsl.exe routinely spawns
        // descendants (wslhost.exe, distro init processes) that inherit our redirected
        // pipe handles. Even after wsl.exe itself has exited, ReadToEndAsync can hang
        // indefinitely waiting for EOF — observed as the "checking system" wizard hang
        // during PR #274 smoke testing where the gateway distro held the pipes open for
        // hours. WaitForExitAsync only governs process exit, not stream drain, so we
        // need an explicit drain bound here.
        var stdout = await DrainAsync(stdoutTask, _streamDrainTimeout, _logger, isStderr: false);
        var stderr = await DrainAsync(stderrTask, _streamDrainTimeout, _logger, isStderr: true);

        var finalResult = timedOut
            ? new WslCommandResult(-1, stdout, "wsl.exe timed out")
            : new WslCommandResult(process.ExitCode, stdout, stderr);

        sw.Stop();
        _diagnostics.CommandCompleted(commandId, fileName, arguments, sw.Elapsed, finalResult, timedOut);
        return finalResult;
    }

    internal static async Task<string> DrainAsync(Task<string> readTask, TimeSpan drainTimeout, IOpenClawLogger logger, bool isStderr)
    {
        try
        {
            if (readTask.IsCompleted)
                return await readTask;

            var winner = await Task.WhenAny(readTask, Task.Delay(drainTimeout));
            if (winner == readTask)
                return await readTask;

            logger.Warn($"[WSL] Stream drain timed out after {(int)drainTimeout.TotalSeconds}s ({(isStderr ? "stderr" : "stdout")}); descendant process likely still owns the pipe handle. Returning partial output.");
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.Warn($"[WSL] Stream drain failed ({(isStderr ? "stderr" : "stdout")}): {ex.Message}");
            return string.Empty;
        }
    }

    private static void ApplyEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return;

        var inherited = psi.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in BuildProcessEnvironment(inherited, environment))
            psi.Environment[pair.Key] = pair.Value;
    }

    public static Dictionary<string, string> BuildProcessEnvironment(
        IReadOnlyDictionary<string, string> inheritedEnvironment,
        IReadOnlyDictionary<string, string>? environment)
    {
        var result = new Dictionary<string, string>(inheritedEnvironment, StringComparer.OrdinalIgnoreCase);
        if (environment is null || environment.Count == 0)
            return result;

        foreach (var pair in environment)
            result[pair.Key] = pair.Value;

        if (environment.ContainsKey(SharedGatewayTokenEnvironment.VariableName))
            AppendWslEnvPassthrough(result, SharedGatewayTokenEnvironment.VariableName + "/u");
        if (environment.ContainsKey(OpenClawGatewayTokenEnvironment.VariableName))
            AppendWslEnvPassthrough(result, OpenClawGatewayTokenEnvironment.VariableName + "/u");

        return result;
    }

    private static void AppendWslEnvPassthrough(IDictionary<string, string> environment, string entry)
    {
        environment.TryGetValue("WSLENV", out var existing);
        var parts = string.IsNullOrWhiteSpace(existing)
            ? []
            : existing.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.Equals(entry, StringComparison.OrdinalIgnoreCase)))
            return;

        environment["WSLENV"] = string.IsNullOrWhiteSpace(existing) ? entry : existing + ":" + entry;
    }

    private static IEnumerable<string> RedactArguments(IReadOnlyList<string> arguments) =>
        SetupDiagnosticsRedactor.RedactArguments(arguments);
}

public sealed record LocalGatewayPreflightResult(
    bool CanContinue,
    bool RequiresAdmin,
    bool RequiresRestart,
    IReadOnlyList<LocalGatewaySetupIssue> Issues);

public enum SetupElevationOperation
{
    EnableWindowsSubsystemForLinux,
    EnableVirtualMachinePlatform,
    UpdateWsl
}

public sealed record SetupElevationRequest(
    SetupElevationOperation Operation,
    string Reason,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record SetupElevationResult(
    bool Success,
    bool RequiresRestart = false,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface ISetupElevationBroker
{
    IReadOnlySet<SetupElevationOperation> SupportedOperations { get; }
    Task<SetupElevationResult> ExecuteAsync(SetupElevationRequest request, CancellationToken cancellationToken = default);
}

public sealed class UnavailableSetupElevationBroker : ISetupElevationBroker
{
    public IReadOnlySet<SetupElevationOperation> SupportedOperations { get; } = new HashSet<SetupElevationOperation>();

    public Task<SetupElevationResult> ExecuteAsync(SetupElevationRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SetupElevationResult(
            false,
            ErrorCode: "elevation_broker_unavailable",
            ErrorMessage: "The OpenClaw setup elevation broker is not available."));
    }
}

public interface IPortProbe
{
    bool IsPortAvailable(int port);
}

public sealed class TcpPortProbe : IPortProbe
{
    public bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public interface ILocalGatewayPreflightProbe
{
    Task<LocalGatewayPreflightResult> RunAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public sealed class LocalGatewayPreflightProbe : ILocalGatewayPreflightProbe
{
    private readonly IWslCommandRunner _wsl;
    private readonly IPortProbe _portProbe;
    private readonly IWslPlatformProbe _platformProbe;

    public LocalGatewayPreflightProbe(IWslCommandRunner wsl, IPortProbe? portProbe = null, IWslPlatformProbe? platformProbe = null)
    {
        _wsl = wsl;
        _portProbe = portProbe ?? new TcpPortProbe();
        _platformProbe = platformProbe ?? new WslPlatformProbe(wsl);
    }

    public async Task<LocalGatewayPreflightResult> RunAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<LocalGatewaySetupIssue>();

        if (!OperatingSystem.IsWindows())
            issues.Add(new LocalGatewaySetupIssue("unsupported_os", "OpenClaw local WSL gateway setup requires Windows.", LocalGatewaySetupSeverity.Blocking));

        if (Environment.Is64BitOperatingSystem is false)
            issues.Add(new LocalGatewaySetupIssue("unsupported_architecture", "OpenClaw local WSL gateway setup requires a 64-bit Windows installation.", LocalGatewaySetupSeverity.Blocking));

        // Fast-detect "WSL platform not installed" BEFORE running wsl --status
        // / wsl --list --verbose with their default 30s timeouts. When the
        // platform is missing, those commands either return immediately with
        // the not-installed banner or hang for the full timeout depending on
        // SKU; we short-circuit either way.
        var platform = await _platformProbe.ProbeAsync(cancellationToken);
        if (platform.State == WslPlatformState.NotInstalled)
        {
            // Non-blocking: the engine's EnsureWslEnabled phase will offer to
            // install the WSL platform automatically via the elevated
            // IWslPlatformInstaller. Surfacing this as a Warning lets the
            // engine proceed past preflight; the install path then either
            // succeeds, asks for a reboot, or fails with a clearer message
            // than the generic "wsl_unavailable" we used to emit.
            issues.Add(new LocalGatewaySetupIssue(
                "wsl_platform_not_installed",
                "Windows Subsystem for Linux is not installed. OpenClaw will install it for you.",
                LocalGatewaySetupSeverity.Warning,
                Detail: platform.Detail));
        }
        else
        {
            // Re-using the probe's status result keeps us at exactly one
            // wsl --status invocation. When the probe timed out it returns
            // Unknown with StatusResult == null; we deliberately do NOT
            // fall back to a second `_wsl.RunAsync(["--status"])` here —
            // that runner uses the engine-wide 30s default timeout, which
            // would silently undo the whole point of WslPlatformProbe's
            // 5s fast-fail. Surface wsl_unavailable directly instead.
            if (platform.StatusResult is null)
            {
                issues.Add(new LocalGatewaySetupIssue(
                    "wsl_unavailable",
                    WslLogsHelp("WSL is not responding. " + (platform.Detail ?? "wsl --status timed out.")),
                    LocalGatewaySetupSeverity.Blocking,
                    Detail: platform.Detail));
            }
            else if (!platform.StatusResult.Success)
            {
                issues.Add(new LocalGatewaySetupIssue("wsl_unavailable", WslLogsHelp("WSL is not available or is blocked by policy."), LocalGatewaySetupSeverity.Blocking));
            }
            else
            {
                var status = WslExeCommandRunner.ParseStatus(platform.StatusResult.StandardOutput);
                if (status.DefaultVersion == 1)
                    issues.Add(new LocalGatewaySetupIssue("wsl_default_version_1", "The host default WSL version is WSL1. OpenClaw creates its dedicated gateway instance as WSL2.", LocalGatewaySetupSeverity.Warning));
            }
        }

        // WSL-side probes (distro enumeration + in-distro existing gateway
        // recognition) only make sense when the platform is actually present
        // and responsive — when NotInstalled (we'll install it in
        // EnsureWslEnabled) or Unknown (probe timed out), `wsl --list
        // --verbose` will hang on the 30s engine runner timeout or return
        // useless data.
        if (platform.State == WslPlatformState.Installed)
        {
            var distros = await _wsl.ListDistrosAsync(cancellationToken);
            if (!options.AllowExistingDistro && distros.Any(d => string.Equals(d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new LocalGatewaySetupIssue("distro_exists", $"A WSL distro named {options.DistroName} already exists.", LocalGatewaySetupSeverity.Blocking));

            if (distros.Any(d => d.Version == 1))
                issues.Add(new LocalGatewaySetupIssue("wsl1_present", "WSL1 distros are present. OpenClaw uses WSL2 and does not modify existing distros.", LocalGatewaySetupSeverity.Warning));
        }

        // The host-side port probe is independent of WSL state and is
        // cheap (no wsl.exe involvement) — run it unconditionally so a
        // Windows-side process already holding the gateway port is
        // caught BEFORE we sink effort into a WSL install/distro setup.
        // Without this gate-on-Installed-only, a missing-WSL user would
        // get past preflight, install WSL, install the distro, and only
        // then discover a port conflict — a much worse UX than failing
        // fast here.
        if (!_portProbe.IsPortAvailable(options.GatewayPort))
        {
            // The "this is OUR existing gateway" recovery path needs to
            // talk to WSL to confirm — only attempt it when WSL is
            // present, otherwise treat any port collision as blocking.
            if (platform.State == WslPlatformState.Installed
                && options.AllowExistingDistro
                && await IsExistingGatewayPortAsync(options, cancellationToken))
            {
                issues.Add(new LocalGatewaySetupIssue(
                    "gateway_port_already_active",
                    $"Local gateway port {options.GatewayPort} is already served by the OpenClawGateway WSL instance; setup will resume against it.",
                    LocalGatewaySetupSeverity.Warning));
            }
            else
            {
                issues.Add(new LocalGatewaySetupIssue("port_in_use", $"Local gateway port {options.GatewayPort} is already in use.", LocalGatewaySetupSeverity.Blocking));
            }
        }

        var canContinue = issues.All(x => x.Severity != LocalGatewaySetupSeverity.Blocking);
        return new LocalGatewayPreflightResult(canContinue, RequiresAdmin: false, RequiresRestart: false, issues);
    }

    private async Task<bool> IsExistingGatewayPortAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            "if [ -s /var/lib/openclaw/gateway-token ]; then",
            // TODO(aaron-token-argv-backlog): move this status probe to env auth so gateway tokens never reach argv.
            "  xargs -r " + ShellQuote(options.OpenClawInstallPrefix + "/bin/openclaw") + " gateway status --json --require-rpc --url " + ShellQuote(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(options)) + " --token </var/lib/openclaw/gateway-token",
            "else",
            "  " + ShellQuote(options.OpenClawInstallPrefix + "/bin/openclaw") + " gateway status --json --require-rpc --url " + ShellQuote(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(options)),
            "fi"
        });

        var result = await _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
        return result.Success;
    }

    private static string WslLogsHelp(string message) => message + " If WSL diagnostics are needed, follow aka.ms/wsllogs.";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public sealed record WslInstanceInstallResult(
    bool Success,
    string? InstallLocation = null,
    IReadOnlyList<string>? Warnings = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IWslInstanceInstaller
{
    Task<WslInstanceInstallResult> EnsureInstalledAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public sealed class WslStoreInstanceInstaller : IWslInstanceInstaller
{
    private readonly IWslCommandRunner _wsl;
    private readonly Action<string> _createDirectory;

    public WslStoreInstanceInstaller(IWslCommandRunner wsl, Action<string>? createDirectory = null)
    {
        _wsl = wsl;
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
    }

    public async Task<WslInstanceInstallResult> EnsureInstalledAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        var installLocation = ResolveInstallLocation(options);
        var distros = await _wsl.ListDistrosAsync(cancellationToken);
        if (distros.Any(d => string.Equals(d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase) && d.Version == 2))
        {
            if (!options.AllowExistingDistro)
            {
                return new WslInstanceInstallResult(false, installLocation, ErrorCode: "distro_exists", ErrorMessage: $"A WSL distro named {options.DistroName} already exists.");
            }

            var probe = await _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "true"], cancellationToken);
            if (probe.Success)
            {
                return new WslInstanceInstallResult(true, installLocation, ["wsl_instance_already_exists"]);
            }

            if (!IsMissingRegisteredDiskFailure(probe))
            {
                var existingDiagnostics = new List<string> { $"wsl_existing_distro_probe_exit_code={probe.ExitCode}" };
                AddDiagnosticOutput(existingDiagnostics, "wsl_existing_distro_probe_stdout", probe.StandardOutput);
                AddDiagnosticOutput(existingDiagnostics, "wsl_existing_distro_probe_stderr", probe.StandardError);
                return new WslInstanceInstallResult(
                    false,
                    installLocation,
                    existingDiagnostics,
                    "wsl_existing_distro_unavailable",
                    WslLogsHelp($"The existing {options.DistroName} WSL instance could not be started."));
            }

            var unregister = await _wsl.UnregisterDistroAsync(options.DistroName, cancellationToken);
            if (!unregister.Success)
            {
                var remainingDistros = await _wsl.ListDistrosAsync(cancellationToken);
                if (!remainingDistros.Any(d => string.Equals(d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase) && d.Version == 2))
                {
                    // Another actor may have completed the unregister even though
                    // wsl.exe returned a failure. Continue with a clean install.
                }
                else
                {
                    var unregisterDiagnostics = new List<string> { $"wsl_unregister_exit_code={unregister.ExitCode}" };
                    AddDiagnosticOutput(unregisterDiagnostics, "wsl_unregister_stdout", unregister.StandardOutput);
                    AddDiagnosticOutput(unregisterDiagnostics, "wsl_unregister_stderr", unregister.StandardError);
                    return new WslInstanceInstallResult(
                        false,
                        installLocation,
                        unregisterDiagnostics,
                        "wsl_broken_distro_unregister_failed",
                        WslLogsHelp($"The existing {options.DistroName} WSL registration points to a missing disk, but setup could not remove it."));
                }
            }
        }

        _createDirectory(installLocation);
        var install = await _wsl.RunAsync([
            "--install",
            options.BaseDistroName,
            "--name",
            options.DistroName,
            "--location",
            installLocation,
            "--no-launch",
            "--version",
            "2"], cancellationToken);

        if (install.Success)
            return new WslInstanceInstallResult(true, installLocation);

        var diagnostics = new List<string> { $"wsl_install_exit_code={install.ExitCode}" };
        AddDiagnosticOutput(diagnostics, "wsl_install_stdout", install.StandardOutput);
        AddDiagnosticOutput(diagnostics, "wsl_install_stderr", install.StandardError);
        diagnostics.Add("wsl_logs=aka.ms/wsllogs");

        // Network-failure detection: `wsl --install <distro>` downloads the
        // distro image from the Microsoft Store / Store CDN. When the
        // machine is offline (or behind a captive portal / restrictive
        // firewall) this fails with a specific class of errors that all
        // mention the network / download. Surface a dedicated failure code
        // + a friendlier user-facing message so the wizard doesn't show
        // the generic "follow aka.ms/wsllogs" rabbit-hole for a problem
        // the user can immediately diagnose (and fix) themselves.
        if (LooksLikeNetworkFailure(install))
        {
            return new WslInstanceInstallResult(
                false,
                installLocation,
                diagnostics,
                "wsl_instance_install_no_network",
                "Couldn't download Ubuntu from the Microsoft Store. Check your internet connection and try again.");
        }

        return new WslInstanceInstallResult(
            false,
            installLocation,
            diagnostics,
            "wsl_instance_install_failed",
            WslLogsHelp("Creating the OpenClaw Gateway WSL instance failed."));
    }

    /// <summary>
    /// Heuristic: does the wsl.exe output look like a download/network
    /// failure rather than a real install error? Matches common phrases from
    /// the Store CDN / Lifted-WSL HTTP error paths across English locales.
    /// We deliberately stay loose — false positives just give the user a
    /// nicer message; false negatives fall through to the generic path.
    /// </summary>
    internal static bool LooksLikeNetworkFailure(WslCommandResult result)
    {
        var output = ((result.StandardOutput ?? string.Empty) + "\n" + (result.StandardError ?? string.Empty))
            .Replace("\0", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains("Could not download", StringComparison.OrdinalIgnoreCase)
            || output.Contains("failed to download", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase)
            || output.Contains("could not resolve", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The remote name could not be resolved", StringComparison.OrdinalIgnoreCase)
            // Microsoft Store-specific HRESULT for offline / no-route
            || output.Contains("0x80072EFD", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x80072EFE", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x80072EE2", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x80072EE7", StringComparison.OrdinalIgnoreCase)
            // WININET_E_CANNOT_CONNECT
            || output.Contains("INET_E_CANNOT_CONNECT", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveInstallLocation(LocalGatewaySetupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.InstanceInstallLocation))
            return options.InstanceInstallLocation;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray",
            "wsl",
            options.DistroName);
    }

    private static void AddDiagnosticOutput(List<string> diagnostics, string name, string value)
    {
        var sanitized = SanitizeForDiagnostic(value);
        if (!string.IsNullOrWhiteSpace(sanitized))
            diagnostics.Add($"{name}={sanitized}");
    }

    internal static bool IsMissingRegisteredDiskFailure(WslCommandResult result)
    {
        var output = $"{result.StandardOutput}\n{result.StandardError}".Replace("\0", string.Empty, StringComparison.Ordinal);
        var pathNotFound = output.Contains("ERROR_PATH_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The system cannot find the path specified", StringComparison.OrdinalIgnoreCase);
        if (!pathNotFound)
            return false;

        return output.Contains("Failed to attach disk", StringComparison.OrdinalIgnoreCase)
            || output.Contains("CreateInstance/MountDisk", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ext4.vhdx", StringComparison.OrdinalIgnoreCase);
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";

    private static string SanitizeForDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Apply both passes: SecretRedactor catches key=value patterns (e.g. gateway-token=...),
        // TokenSanitizer catches raw token formats (64-char hex, long base64url) that can appear
        // in subprocess error output when a CLI tool echoes its own arguments.
        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(value)).Replace("\0", string.Empty).Trim();
        const int maxLength = 2000;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...<truncated>";
    }
}

public sealed record WslInstanceConfigurationResult(
    bool Success,
    IReadOnlyList<string>? Warnings = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Detail = null);

public interface IWslInstanceConfigurator
{
    Task<WslInstanceConfigurationResult> ConfigureAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public sealed class WslFirstBootConfigurator : IWslInstanceConfigurator
{
    private readonly IWslCommandRunner _wsl;

    public WslFirstBootConfigurator(IWslCommandRunner wsl)
    {
        _wsl = wsl;
    }

    public async Task<WslInstanceConfigurationResult> ConfigureAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        if (options.AllowExistingDistro && await IsAlreadyConfiguredAsync(options, cancellationToken))
            return new WslInstanceConfigurationResult(true, ["wsl_instance_already_configured"]);

        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            "if ! id -u openclaw >/dev/null 2>&1; then useradd --create-home --shell /bin/bash openclaw; fi",
            "install -d -m 0755 -o openclaw -g openclaw /home/openclaw/.openclaw",
            "install -d -m 0755 -o openclaw -g openclaw " + ShellQuote(options.OpenClawInstallPrefix),
            "install -d -m 0755 -o openclaw -g openclaw /var/lib/openclaw",
            "install -d -m 0755 -o openclaw -g openclaw /var/log/openclaw",
            "cat >/etc/wsl.conf <<'EOF'",
            "[boot]",
            "systemd=true",
            "",
            "[automount]",
            "enabled=false",
            "mountFsTab=false",
            "",
            "[interop]",
            "enabled=false",
            "appendWindowsPath=false",
            "",
            "[user]",
            "default=openclaw",
            "",
            "[time]",
            "useWindowsTimezone=true",
            "EOF",
            "cat >/etc/wsl-distribution.conf <<'EOF'",
            "[oobe]",
            "defaultName=openclaw",
            "EOF",
            "loginctl enable-linger openclaw || true",
            "chown -R openclaw:openclaw /home/openclaw/.openclaw " + ShellQuote(options.OpenClawInstallPrefix) + " /var/lib/openclaw /var/log/openclaw"
        });

        var configure = await _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "bash", "-lc", script], cancellationToken);
        if (!configure.Success)
        {
            var detail = $"exit={configure.ExitCode}; stderr={SanitizeForDiagnostic(configure.StandardError)}; stdout={SanitizeForDiagnostic(configure.StandardOutput)}";
            return new WslInstanceConfigurationResult(
                false,
                ErrorCode: "wsl_firstboot_config_failed",
                ErrorMessage: WslLogsHelp($"Failed to configure the OpenClaw WSL instance (exit {configure.ExitCode})."),
                Detail: detail);
        }

        var warnings = new List<string>();
        var setDefaultUser = await _wsl.RunAsync(["--manage", options.DistroName, "--set-default-user", "openclaw"], cancellationToken);
        if (!setDefaultUser.Success)
            warnings.Add("wsl_manage_set_default_user_failed");

        var terminate = await _wsl.TerminateDistroAsync(options.DistroName, cancellationToken);
        if (!terminate.Success)
        {
            return new WslInstanceConfigurationResult(
                false,
                warnings,
                "wsl_instance_restart_failed",
                WslLogsHelp("Failed to restart the OpenClaw WSL instance after writing WSL configuration."));
        }

        return new WslInstanceConfigurationResult(true, warnings);
    }

    private async Task<bool> IsAlreadyConfiguredAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        // The awk probe is section-aware: it verifies each setting is present in the correct
        // section, and checks all seven expected keys (including the two added in the wsl.conf
        // template update: mountFsTab=false and [time] useWindowsTimezone=true).
        // A distro configured with the old template (missing these keys) returns exit 1,
        // triggering a full reconfigure.
        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            "id -u openclaw >/dev/null",
            "test -d /home/openclaw/.openclaw",
            "test -d " + ShellQuote(options.OpenClawInstallPrefix),
            "awk 'BEGIN{sec=\"\"}" +
                " /^\\[/{sec=substr($0,2,index($0,\"]\")-2)}" +
                " sec==\"boot\"&&$0==\"systemd=true\"{b=1}" +
                " sec==\"automount\"&&$0==\"enabled=false\"{ae=1}" +
                " sec==\"automount\"&&$0==\"mountFsTab=false\"{af=1}" +
                " sec==\"interop\"&&$0==\"enabled=false\"{ie=1}" +
                " sec==\"interop\"&&$0==\"appendWindowsPath=false\"{ip=1}" +
                " sec==\"user\"&&$0==\"default=openclaw\"{u=1}" +
                " sec==\"time\"&&$0==\"useWindowsTimezone=true\"{t=1}" +
                " END{exit !(b&&ae&&af&&ie&&ip&&u&&t)}' /etc/wsl.conf"
        });

        var probe = await _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "bash", "-lc", script], cancellationToken);
        return probe.Success;
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Local copy of the diagnostic sanitizer pattern used elsewhere in this file —
    /// strips NUL bytes, runs SecretRedactor + TokenSanitizer, and truncates so the
    /// failure detail can be safely surfaced to the wizard UI without leaking tokens.
    /// </summary>
    private static string SanitizeForDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(value)).Replace("\0", string.Empty).Trim();
        const int maxLength = 600;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...<truncated>";
    }
}

public sealed record OpenClawLinuxInstallerEvent(string? Event, string? Phase, string? Message, string RawLine);

public sealed record OpenClawLinuxInstallResult(
    bool Success,
    IReadOnlyList<OpenClawLinuxInstallerEvent>? Events = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Detail = null);

public interface IOpenClawLinuxInstaller
{
    Task<OpenClawLinuxInstallResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public sealed class OpenClawInstallCliLinuxInstaller : IOpenClawLinuxInstaller
{
    private readonly IWslCommandRunner _wsl;

    public OpenClawInstallCliLinuxInstaller(IWslCommandRunner wsl)
    {
        _wsl = wsl;
    }

    public async Task<OpenClawLinuxInstallResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        await StopExistingGatewayServiceAsync(options, cancellationToken);

        var script = string.Join(" ", new[]
        {
            "set -euo pipefail;",
            "curl -fsSL --proto '=https' --tlsv1.2",
            ShellQuote(options.OpenClawInstallerUrl),
            "|",
            "OPENCLAW_PREFIX=" + ShellQuote(options.OpenClawInstallPrefix),
            "OPENCLAW_INSTALL_METHOD=" + ShellQuote(options.OpenClawInstallMethod),
            "OPENCLAW_VERSION=" + ShellQuote(options.OpenClawInstallVersion),
            "SHARP_IGNORE_GLOBAL_LIBVIPS=1",
            "bash -s -- --json --prefix",
            ShellQuote(options.OpenClawInstallPrefix),
            "--version",
            ShellQuote(options.OpenClawInstallVersion),
            "--no-onboard"
        });

        var install = await _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
        var events = ParseInstallerEvents(install.StandardOutput);
        if (!install.Success)
        {
            var detail = BuildCommandDiagnostic("openclaw_install", install);
            return new OpenClawLinuxInstallResult(false, events, "openclaw_linux_install_failed", "The upstream OpenClaw Linux installer failed.", detail);
        }

        var version = await _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", options.OpenClawInstallPrefix + "/bin/openclaw", "--version"], cancellationToken);
        if (!version.Success)
        {
            var detail = BuildCommandDiagnostic("openclaw_cli_verify", version);
            return new OpenClawLinuxInstallResult(false, events, "openclaw_cli_verify_failed", "The OpenClaw CLI was installed, but the installed binary could not be verified.", detail);
        }

        // Best-effort: expose the openclaw CLI on the default PATH so engineers can run
        // `openclaw ...` from any WSL shell (login or `wsl bash -c`) without typing the
        // /opt/openclaw/bin/ prefix. /usr/local/bin is on every shell's default PATH and
        // is the same pattern packaging/wsl-rootfs/.../prepare-rootfs.sh uses for `uv`.
        // Failure here does not fail the install — tray/service code still uses the
        // absolute path; only the human-facing convenience is lost.
        var symlinkResult = await EnsureOpenClawOnPathAsync(options, cancellationToken);
        if (symlinkResult.ExitCode != 0)
        {
#if !OPENCLAW_TRAY_TESTS
            Logger.Warn($"Best-effort openclaw PATH symlink step failed (exit {symlinkResult.ExitCode}). User will need to use the full /opt/openclaw/bin/openclaw path inside WSL.");
#endif
        }

        return new OpenClawLinuxInstallResult(true, events);
    }

    private Task<WslCommandResult> EnsureOpenClawOnPathAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        var binaryPath = options.OpenClawInstallPrefix + "/bin/openclaw";
        var binaryQ = ShellQuote(binaryPath);
        // We deliberately only manage /usr/local/bin/openclaw when (a) it doesn't exist
        // yet, (b) it's a broken symlink, or (c) it's already a symlink that points at
        // the same binary. We do NOT clobber a regular file (a user may have hand-installed
        // a different openclaw build there) and we do NOT redirect a symlink that points
        // somewhere else (a developer may have aimed it at a debug build on purpose).
        //
        // IMPORTANT: every shell `$variable` reference in the script body is escaped as
        // `\$variable`. wsl.exe performs Windows-style env-var substitution on its argument
        // string before invoking bash, so an unescaped `$expected` would be replaced with
        // the (empty) Windows env var of the same name and bash would never see the
        // assignment. Backslash-escaping prevents wsl.exe substitution while still letting
        // bash expand the variable normally. The same escape pattern is required in every
        // multi-line script that shares variables across lines.
        var script = string.Join("\n", new[]
        {
            "set +e",
            "target=/usr/local/bin/openclaw",
            "expected=" + binaryQ,
            "if [ ! -x \"\\$expected\" ]; then exit 0; fi",
            "if [ ! -e \"\\$target\" ] && [ ! -L \"\\$target\" ]; then",
            "  ln -sf \"\\$expected\" \"\\$target\"",
            "  exit \\$?",
            "fi",
            "if [ -L \"\\$target\" ]; then",
            "  current=\\$(readlink -- \"\\$target\" 2>/dev/null)",
            "  if [ \"\\$current\" = \"\\$expected\" ]; then exit 0; fi",
            "  # Broken symlink — the resolved path doesn't exist. Safe to re-point.",
            "  if [ ! -e \"\\$target\" ]; then",
            "    ln -sf \"\\$expected\" \"\\$target\"",
            "    exit \\$?",
            "  fi",
            "  # Symlink points to a different live target — leave it alone (user customization).",
            "  exit 0",
            "fi",
            "# Regular file or other non-symlink — do not clobber.",
            "exit 0"
        });
        return _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "bash", "-lc", script], cancellationToken);
    }

    public static IReadOnlyList<OpenClawLinuxInstallerEvent> ParseInstallerEvents(string output)
    {
        var events = new List<OpenClawLinuxInstallerEvent>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var redactedLine = SecretRedactor.Redact(line);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                events.Add(new OpenClawLinuxInstallerEvent(
                    TryGetString(root, "event"),
                    TryGetString(root, "phase"),
                    SecretRedactor.Redact(TryGetString(root, "message") ?? string.Empty),
                    redactedLine));
            }
            catch (JsonException)
            {
                events.Add(new OpenClawLinuxInstallerEvent(null, null, redactedLine, redactedLine));
            }
        }

        return events;
    }

    private Task<WslCommandResult> StopExistingGatewayServiceAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        const string serviceName = "openclaw-gateway.service";
        var script = string.Join("\n", new[]
        {
            "set +e",
            "systemctl --user stop " + serviceName + " >/dev/null 2>&1",
            "systemctl --user reset-failed " + serviceName + " >/dev/null 2>&1"
        });
        return _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            return property.GetString();
        return null;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string BuildCommandDiagnostic(string prefix, WslCommandResult result) => DiagnosticFormatter.Build(prefix, result);
}

public sealed record GatewayServiceOperationResult(
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Detail = null);

public sealed record GatewayConfigurationResult(
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Detail = null);

public interface IGatewayConfigurationPreparer
{
    Task<GatewayConfigurationResult> PrepareAsync(LocalGatewaySetupOptions options, string sharedGatewayToken, CancellationToken cancellationToken = default);
}

public sealed class OpenClawCliGatewayConfigurationPreparer : IGatewayConfigurationPreparer
{
    private readonly IWslCommandRunner _wsl;

    public OpenClawCliGatewayConfigurationPreparer(IWslCommandRunner wsl)
    {
        _wsl = wsl;
    }

    public async Task<GatewayConfigurationResult> PrepareAsync(LocalGatewaySetupOptions options, string sharedGatewayToken, CancellationToken cancellationToken = default)
    {
        var openClaw = ShellQuote(options.OpenClawInstallPrefix + "/bin/openclaw");
        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            "umask 077",
            ": \"${OPENCLAW_SHARED_GATEWAY_TOKEN:?missing shared gateway token}\"",
            "printf '%s' \"$OPENCLAW_SHARED_GATEWAY_TOKEN\" >/var/lib/openclaw/gateway-token",
            openClaw + " config set gateway.mode local",
            openClaw + " config set gateway.port " + options.GatewayPort.ToString(CultureInfo.InvariantCulture) + " --strict-json",
            openClaw + " config set gateway.auth.mode token",
            "xargs -r " + openClaw + " config set gateway.auth.token </var/lib/openclaw/gateway-token",
            // Suppress restart-required reloads triggered by config writes the V2 setup
            // wizard makes mid-flow. With the default "hybrid" mode, the gateway-side
            // wizard at src/wizard/setup.ts:750 commits the wizard's collected snapshot
            // (gateway.bind / gateway.tailscale.* / gateway.controlUi.* /
            // auth.profiles.*) — those paths fall under the catch-all restart rule
            // in src/gateway/config-reload-plan.ts (line 126: { prefix: "gateway",
            // kind: "restart" }) and would fire a service restart (WS close 1012,
            // restartExpectedMs: 1500). That cancels the in-flight wizard.next mid-
            // step and forces the operator to re-walk the entire wizard with no
            // memory of the previous answers.
            //
            // Setting reload.mode=hot makes the watcher LOG-and-IGNORE restart-required
            // changes (src/gateway/config-reload.ts:274-281: "config reload requires
            // gateway restart; hot mode ignoring") while still allowing legitimate
            // hot reloads (channels/hooks/plugins). The gateway already has the right
            // bind/auth/port from this PrepareGatewayConfig phase, so suppressing the
            // mid-wizard restart loses nothing: the wizard's writes for those paths
            // are no-ops in terms of running gateway state.
            //
            // gateway.reload itself is { prefix: "gateway.reload", kind: "none" } in
            // the reload-plan rules, so this write does not itself trigger a reload.
            // The watcher is not yet active when this script runs (StartGateway phase
            // hasn't started), so even the earlier writes above don't notify.
            openClaw + " config set gateway.reload.mode hot",
            openClaw + " config validate"
        });

        var environment = new Dictionary<string, string>
        {
            [SharedGatewayTokenEnvironment.VariableName] = sharedGatewayToken
        };
        var result = await _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken, environment);
        return result.Success
            ? new GatewayConfigurationResult(true)
            : new GatewayConfigurationResult(false, "gateway_config_prepare_failed", "Failed to prepare upstream OpenClaw gateway configuration.", DiagnosticFormatter.Build("gateway_config_prepare", result));
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public interface IGatewayServiceManager
{
    Task<GatewayServiceOperationResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
    Task<GatewayServiceOperationResult> StartAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public sealed class OpenClawCliGatewayServiceManager : IGatewayServiceManager
{
    private readonly IWslCommandRunner _wsl;

    public OpenClawCliGatewayServiceManager(IWslCommandRunner wsl)
    {
        _wsl = wsl;
    }

    public async Task<GatewayServiceOperationResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        // First, defensively stop any pre-existing openclaw-gateway service
        // that may be left over from a previous setup attempt. Without this,
        // a "Try again" run from a fresh state (the engine self-heal resets
        // FailedRetryable to NotStarted) hits the port-conflict probe below
        // because the prior run's gateway is still bound to options.GatewayPort.
        // This is idempotent on a clean install (the stop succeeds vacuously
        // when nothing is running).
        await StopOpenClawGatewayServiceAsync(options, cancellationToken);

        // W2a: If something else is already bound to the gateway port inside WSL, surface
        // an actionable error up front instead of letting `gateway install` or the 90s
        // status poll fail with an opaque message.
        var portConflict = await ProbePortConflictAsync(options, cancellationToken);
        if (portConflict is not null)
        {
            return new GatewayServiceOperationResult(
                false,
                "gateway_port_in_use",
                $"Local gateway port {options.GatewayPort} is already in use inside the {options.DistroName} distro.",
                portConflict);
        }

        await ResetFailedServiceStateAsync(options, cancellationToken);
        var result = await RunOpenClawAsync(options, ["gateway", "install", "--force", "--port", options.GatewayPort.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        if (result.Success)
            return new GatewayServiceOperationResult(true);

        var firstFailure = DiagnosticFormatter.Build("gateway_service_install", result);
        if (IsSystemdStartLimitFailure(result))
        {
            await ResetFailedServiceStateAsync(options, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var retry = await RunOpenClawAsync(options, ["gateway", "install", "--force", "--port", options.GatewayPort.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            if (retry.Success)
                return new GatewayServiceOperationResult(true);

            return new GatewayServiceOperationResult(false, "gateway_service_install_failed", "Failed to install the upstream OpenClaw gateway service.", firstFailure + Environment.NewLine + DiagnosticFormatter.Build("gateway_service_install_retry", retry));
        }

        return new GatewayServiceOperationResult(false, "gateway_service_install_failed", "Failed to install the upstream OpenClaw gateway service.", firstFailure);
    }

    public async Task<GatewayServiceOperationResult> StartAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        // W2b: fast-path — if the gateway is already responding on its URL, declare
        // success without re-issuing `gateway start`. Stops a stale failure from a
        // previous wizard attempt from gating progress when the gateway is in fact
        // healthy. Wrap in a short timeout: the probe is a best-effort optimisation, so
        // a hang on the inner socket connect must not block the start path. We fall
        // back to the regular `gateway start` flow on timeout.
        var earlyStatus = await TryEarlyStatusProbeAsync(options, cancellationToken);
        if (earlyStatus is { Success: true })
            return new GatewayServiceOperationResult(true);

        var start = await RunOpenClawAsync(options, ["gateway", "start"], cancellationToken);
        if (!start.Success)
        {
            // W2d: include start stderr/stdout + service journal so the user (and us in
            // diagnostics) can see *why* the service refused to come up rather than the
            // generic "Failed to start" wrapper.
            var startDetail = DiagnosticFormatter.Build("gateway_service_start", start);
            var journal = await CaptureServiceJournalAsync(options, cancellationToken);
            var detail = string.IsNullOrWhiteSpace(journal) ? startDetail : startDetail + Environment.NewLine + journal;
            return new GatewayServiceOperationResult(false, "gateway_service_start_failed", WslLogsHelp("Failed to start the upstream OpenClaw gateway service."), detail);
        }

        WslCommandResult? lastStatus = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStatus = await RunStatusWithTokenAsync(options, cancellationToken);
            if (lastStatus.Success)
                return new GatewayServiceOperationResult(true);

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        // W2d: bubble up service status + recent journal lines on the timeout path too.
        var statusDiagnostic = lastStatus is null ? null : DiagnosticFormatter.Build("gateway_service_status", lastStatus);
        var timeoutJournal = await CaptureServiceJournalAsync(options, cancellationToken);
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(statusDiagnostic)) detailParts.Add(statusDiagnostic!);
        if (!string.IsNullOrWhiteSpace(timeoutJournal)) detailParts.Add(timeoutJournal);
        var combinedDetail = detailParts.Count == 0 ? null : string.Join(Environment.NewLine, detailParts);

        return new GatewayServiceOperationResult(
            false,
            "gateway_service_status_failed",
            WslLogsHelp("The OpenClaw gateway service started, but did not report ready status."),
            combinedDetail);
    }

    /// <summary>
    /// Runs the W2b fast-path early-status probe with a short timeout so a hang in the
    /// gateway status socket connect can't block the start path. Returns null on
    /// timeout (caller treats this as "not healthy, proceed with regular start").
    /// </summary>
    private async Task<WslCommandResult?> TryEarlyStatusProbeAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        const int probeTimeoutSeconds = 5;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(probeTimeoutSeconds));
        try
        {
            return await RunStatusWithTokenAsync(options, probeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Probe timed out or outer ct cancelled. Re-throw only if the OUTER
            // cancellation fired; otherwise swallow so StartAsync proceeds.
            if (cancellationToken.IsCancellationRequested)
                throw;
            return null;
        }
    }

    /// <summary>
    /// Best-effort probe that returns a sanitized diagnostic when the gateway port is
    /// bound by a process that is not the OpenClaw gateway service. Returns null when
    /// the port is free, when only the gateway owns it, or when probing failed (so we
    /// don't block install on probe noise).
    /// </summary>
    private async Task<string?> ProbePortConflictAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        var port = options.GatewayPort.ToString(CultureInfo.InvariantCulture);
        // NOTE: shell `$variable` references in the script body must be escaped as `\$`
        // because wsl.exe performs Windows-style env-var substitution on its argument
        // string before bash receives it; an unescaped `$out` would be replaced with
        // the (empty) Windows env var of the same name. See EnsureOpenClawOnPathAsync.
        var script = string.Join("\n", new[]
        {
            "set +e",
            // `ss -ltnp` lists listening TCP sockets with owning process names in the
            // canonical form `users:(("PROGNAME",pid=PID,fd=FD))`. Filter to lines that
            // actually have a `users:` info section (so blank/malformed lines don't fool
            // the check), then require EVERY remaining line to be openclaw-owned — that
            // way a mixed scenario where the gateway and a foreign process share the
            // port (SO_REUSEPORT, separate v4/v6 listeners) is still flagged as a
            // conflict.
            "out=\\$(ss -H -ltnp 'sport = :" + port + "' 2>/dev/null)",
            "if [ -z \"\\$out\" ]; then",
            "  exit 0",
            "fi",
            // Strip lines that don't carry a users:(("...",pid=...)) section — ss omits
            // it when running unprivileged or for kernel sockets, and a missing program
            // name should be treated as a conflict for safety.
            "with_users=\\$(printf '%s\\n' \"\\$out\" | grep -E 'users:\\(\\(' || true)",
            "without_users=\\$(printf '%s\\n' \"\\$out\" | grep -vE 'users:\\(\\(' | grep -vE '^[[:space:]]*\\$' || true)",
            "if [ -n \"\\$without_users\" ]; then",
            "  printf '%s\\n' \"\\$out\"",
            "  exit 2",
            "fi",
            // Among lines that do have users:(, the gateway is recognised by the quoted
            // program name openclaw* — match users:(("openclaw" or users:(("openclaw-…
            "non_openclaw=\\$(printf '%s\\n' \"\\$with_users\" | grep -vE 'users:\\(\\(\"openclaw[^\"]*\",' || true)",
            "if [ -n \"\\$non_openclaw\" ]; then",
            "  printf '%s\\n' \"\\$out\"",
            "  exit 2",
            "fi",
            "exit 0"
        });

        var result = await _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "bash", "-lc", script], cancellationToken);
        if (result.ExitCode == 0)
            return null;
        if (result.ExitCode != 2)
        {
            // Probe itself failed (locked-down distro, missing `ss`, etc.). Log so the
            // observability gap is visible in tray logs, but don't block install — the
            // existing 90 s status poll remains the safety net.
#if !OPENCLAW_TRAY_TESTS
            Logger.Debug($"[LocalGatewaySetup] Port-conflict probe degraded for port {options.GatewayPort} (exit {result.ExitCode}); continuing without explicit conflict detection.");
#endif
            return null;
        }

        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(result.StandardOutput ?? string.Empty)).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"Port {options.GatewayPort} is in use by another process inside {options.DistroName}."
            : $"Port {options.GatewayPort} is in use by another process inside {options.DistroName}. ss output: {sanitized}";
    }

    private async Task<string?> CaptureServiceJournalAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        const string serviceName = "openclaw-gateway.service";
        const string truncationSuffix = "…<truncated>";
        const int maxJournalChars = 8192;
        // Wrap each diagnostic command in `timeout` so that a missing user systemd / D-Bus
        // session (which is common when the distro hasn't fully booted) doesn't stall the
        // failure path indefinitely. SIGKILL after 6 s is hard enough to break any hang.
        var script = string.Join("\n", new[]
        {
            "set +e",
            "echo '== systemctl status =='",
            "timeout --signal=KILL 5 systemctl --user status " + serviceName + " --no-pager 2>&1 | tail -n 40",
            "echo '== journalctl --user-unit -n 100 =='",
            "timeout --signal=KILL 5 journalctl --user-unit " + serviceName + " --no-pager -n 100 2>&1",
            "exit 0"
        });

        var result = await _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(result.StandardOutput ?? string.Empty)).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            return null;
        if (sanitized.Length > maxJournalChars)
        {
            // Reserve room for the truncation marker so the total stays <= maxJournalChars,
            // and back off one code unit if the cut lands on a UTF-16 high surrogate so we
            // don't emit an orphan surrogate (gateway logs can contain emoji in service
            // names / file paths).
            var cut = maxJournalChars - truncationSuffix.Length;
            if (cut < 0) cut = 0;
            if (cut > 0 && cut < sanitized.Length && char.IsHighSurrogate(sanitized[cut - 1])) cut -= 1;
            sanitized = sanitized[..cut] + truncationSuffix;
        }
        return "gateway_service_journal=" + sanitized;
    }

    private Task<WslCommandResult> RunOpenClawAsync(LocalGatewaySetupOptions options, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-d", options.DistroName, "-u", "openclaw", "--", options.OpenClawInstallPrefix + "/bin/openclaw" };
        args.AddRange(arguments);
        return _wsl.RunAsync(args, cancellationToken);
    }

    private Task<WslCommandResult> RunStatusWithTokenAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            // TODO(aaron-token-argv-backlog): move this status probe to env auth so gateway tokens never reach argv.
            "xargs -r " + ShellQuote(options.OpenClawInstallPrefix + "/bin/openclaw")
                + " gateway status --json --require-rpc --url "
                + ShellQuote(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(options))
                + " --token </var/lib/openclaw/gateway-token"
        });
        return _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
    }

    private Task<WslCommandResult> ResetFailedServiceStateAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        const string serviceName = "openclaw-gateway.service";
        var script = string.Join("\n", new[]
        {
            "set +e",
            "systemctl --user reset-failed " + serviceName + " >/dev/null 2>&1"
        });
        return _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
    }

    /// <summary>
    /// Defensively stop any pre-existing openclaw-gateway service before the
    /// install/port-conflict probe. A previous setup run (or a tray restart
    /// mid-flow) can leave the service running and bound to the gateway
    /// port; without a pre-stop the user sees "port already in use" on Try
    /// again and has no path forward except `wsl --shutdown` from a
    /// terminal. Idempotent — `systemctl stop` of a non-running service
    /// returns 0 silently.
    /// </summary>
    private Task<WslCommandResult> StopOpenClawGatewayServiceAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken)
    {
        const string serviceName = "openclaw-gateway.service";
        var script = string.Join("\n", new[]
        {
            "set +e",
            "systemctl --user stop " + serviceName + " >/dev/null 2>&1",
            "systemctl --user reset-failed " + serviceName + " >/dev/null 2>&1",
            // Brief settle so the kernel actually releases the bound port
            // before the caller probes it. systemctl stop returns when the
            // process is signaled, not when the socket is fully torn down.
            "sleep 1"
        });
        return _wsl.RunAsync(["-d", options.DistroName, "-u", "openclaw", "--", "bash", "-lc", script], cancellationToken);
    }

    private static bool IsSystemdStartLimitFailure(WslCommandResult result)
    {
        var output = (result.StandardOutput ?? string.Empty) + "\n" + (result.StandardError ?? string.Empty);
        return output.Contains("start-limit-hit", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Start request repeated too quickly", StringComparison.OrdinalIgnoreCase)
            || output.Contains("systemctl restart failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

internal static partial class SecretRedactor
{
    [GeneratedRegex("(?i)(setup[_-]?code|bootstrap[_-]?token|device[_-]?token|gateway[_-]?token|auth[_-]?token|private[_-]?key(?:base64)?|public[_-]?key(?:base64)?|secret)(['\\\"\\s:=]+)([^\\s,'\\\"}]+)")]
    private static partial Regex SecretValueRegex();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return SecretValueRegex().Replace(value, "$1$2<redacted>");
    }
}

internal static class DiagnosticFormatter
{
    public static string Build(string prefix, WslCommandResult result)
    {
        var diagnostics = new List<string> { $"{prefix}_exit_code={result.ExitCode}" };
        AddOutput(diagnostics, $"{prefix}_stdout", result.StandardOutput);
        AddOutput(diagnostics, $"{prefix}_stderr", result.StandardError);
        return string.Join(Environment.NewLine, diagnostics);
    }

    private static void AddOutput(List<string> diagnostics, string name, string value)
    {
        var sanitized = SanitizeForDiagnostic(value);
        if (!string.IsNullOrWhiteSpace(sanitized))
            diagnostics.Add($"{name}={sanitized}");
    }

    private static string SanitizeForDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Apply both passes: SecretRedactor catches key=value patterns (e.g. gateway-token=...),
        // TokenSanitizer catches raw token formats (64-char hex, long base64url) that can appear
        // in subprocess error output when a CLI tool echoes its own arguments.
        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(value)).Replace("\0", string.Empty).Trim();
        const int maxLength = 2000;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...<truncated>";
    }
}

public sealed record LocalGatewayHealthResult(bool Success, string? Error = null);

public sealed record LocalGatewayEndpointResolutionResult(
    bool Success,
    string GatewayUrl,
    string? Error = null);

public interface ILocalGatewayHealthProbe
{
    Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default);
}

public sealed class LocalGatewayHealthProbe : ILocalGatewayHealthProbe
{
    public async Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await GatewayHealthCheck.TestAsync(gatewayUrl, token: null);
            if (result.Success)
                return new LocalGatewayHealthResult(true);

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return new LocalGatewayHealthResult(false, WslLogsHelp("Gateway did not become healthy."));
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
}

public interface ILocalGatewayEndpointResolver
{
    Task<LocalGatewayEndpointResolutionResult> ResolveAsync(
        LocalGatewaySetupOptions options,
        string currentGatewayUrl,
        ILocalGatewayHealthProbe healthProbe,
        IWslCommandRunner wsl,
        CancellationToken cancellationToken = default);
}

public sealed class LocalGatewayEndpointResolver : ILocalGatewayEndpointResolver
{
    public Task<LocalGatewayEndpointResolutionResult> ResolveAsync(
        LocalGatewaySetupOptions options,
        string currentGatewayUrl,
        ILocalGatewayHealthProbe healthProbe,
        IWslCommandRunner wsl,
        CancellationToken cancellationToken = default)
    {
        return ResolveLoopbackAsync(options, healthProbe, cancellationToken);
    }

    public static string BuildLoopbackGatewayUrl(LocalGatewaySetupOptions options)
    {
        var scheme = Uri.TryCreate(options.GatewayUrl, UriKind.Absolute, out var uri) ? uri.Scheme : "ws";
        return $"{scheme}://localhost:{options.GatewayPort}";
    }

    private static async Task<LocalGatewayEndpointResolutionResult> ResolveLoopbackAsync(LocalGatewaySetupOptions options, ILocalGatewayHealthProbe healthProbe, CancellationToken cancellationToken)
    {
        var gatewayUrl = BuildLoopbackGatewayUrl(options);
        var result = await healthProbe.WaitForHealthyAsync(gatewayUrl, cancellationToken);
        return result.Success
            ? new LocalGatewayEndpointResolutionResult(true, gatewayUrl)
            : new LocalGatewayEndpointResolutionResult(false, gatewayUrl, result.Error ?? WslLogsHelp("Gateway did not become healthy."));
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
}

public sealed record ProvisioningResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null);

public interface IOperatorPairingService
{
    Task<ProvisioningResult> PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}

public sealed record BootstrapTokenResult(
    bool Success,
    string? BootstrapToken = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IBootstrapTokenProvider
{
    Task<BootstrapTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}

public interface IBootstrapTokenProvisioner
{
    Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}
public enum SharedGatewayTokenSource
{
    Generated,
    PreservedFromWsl
}

public sealed record SharedGatewayTokenResult(
    bool Success,
    string? Token = null,
    SharedGatewayTokenSource? Source = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record SharedGatewayProvisioningResult(
    bool Success,
    string? Token = null,
    SharedGatewayTokenSource? Source = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Detail = null);

public interface ISharedGatewayTokenProvider
{
    Task<SharedGatewayTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}

public interface ISharedGatewayTokenProvisioner
{
    Task<SharedGatewayProvisioningResult> ProvisionAsync(LocalGatewaySetupState state, LocalGatewaySetupOptions options, CancellationToken cancellationToken = default);
}

public static class SharedGatewayTokenEnvironment
{
    public const string VariableName = "OPENCLAW_SHARED_GATEWAY_TOKEN";
}

public static class OpenClawGatewayTokenEnvironment
{
    public const string VariableName = "OPENCLAW_GATEWAY_TOKEN";
}

public interface IWindowsTrayNodeProvisioner
{
    Task<ProvisioningResult> CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
    Task<ProvisioningResult> PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
}

public interface ILocalGatewaySetupSettings
{
    string GatewayUrl { get; set; }
    string Token { get; set; }
    string BootstrapToken { get; set; }
    bool UseSshTunnel { get; set; }
    bool EnableNodeMode { get; set; }
    void Save();
}

public sealed class SettingsManagerLocalGatewaySetupSettings : ILocalGatewaySetupSettings
{
    private readonly SettingsManager _settings;
    private readonly OpenClaw.Connection.GatewayRegistry? _registry;

    public SettingsManagerLocalGatewaySetupSettings(SettingsManager settings, OpenClaw.Connection.GatewayRegistry? registry = null)
    {
        _settings = settings;
        _registry = registry;
    }

    public string GatewayUrl { get => _settings.GatewayUrl; set => _settings.GatewayUrl = value; }
    public string Token { get; set; } = "";
    public string BootstrapToken { get; set; } = "";
    public bool UseSshTunnel { get => _settings.UseSshTunnel; set => _settings.UseSshTunnel = value; }
    public bool EnableNodeMode { get => _settings.EnableNodeMode; set => _settings.EnableNodeMode = value; }

    public void Save()
    {
        _settings.Save();

        // Sync credentials to GatewayRegistry (source of truth for connection architecture)
        if (_registry != null && !string.IsNullOrWhiteSpace(GatewayUrl))
        {
            var existing = _registry.FindByUrl(GatewayUrl);
            var recordId = existing?.Id ?? System.Guid.NewGuid().ToString();
            var record = new OpenClaw.Connection.GatewayRecord
            {
                Id = recordId,
                Url = GatewayUrl,
                SharedGatewayToken = !string.IsNullOrWhiteSpace(Token) ? Token : existing?.SharedGatewayToken,
                BootstrapToken = !string.IsNullOrWhiteSpace(BootstrapToken) ? BootstrapToken : existing?.BootstrapToken,
                IsLocal = true,
            };
            _registry.AddOrUpdate(record);
            _registry.SetActive(recordId);
            _registry.Save();
        }
    }
}

public sealed class DeferredBootstrapTokenProvisioner : IBootstrapTokenProvisioner
{
    public Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProvisioningResult(true));
}

public interface IWindowsNodeConnector
{
    Task ConnectAsync(string gatewayUrl, string token, string? bootstrapToken, CancellationToken cancellationToken = default);
}

// NodeServiceWindowsNodeConnector (direct NodeService.ConnectAsync delegate, no
// diagnostics tee) has been removed — all node pairing now flows through
// ConnectionManagerWindowsNodeConnector → GatewayConnectionManager.EnsureNodeConnectedAsync.
// See docs/node-connection-architecture.md and easy-button-gateway-comms-audit.md.

/// <summary>
/// Identity for an OS process, captured for safe PID re-identification.
/// </summary>
/// <remarks>
/// We compare both the process name (best-effort sanity check) and the high-resolution
/// <see cref="StartTimeUtc"/> because PIDs can be recycled. If a previous keepalive
/// exited and Windows handed its PID to an unrelated process, the start time will
/// differ — preventing <see cref="WslDistroKeepAlive.Stop(string, IOpenClawLogger?)"/>
/// from killing the wrong process.
/// </remarks>
public sealed record KeepAliveProcessIdentity(string Name, DateTime StartTimeUtc);

public sealed record KeepAliveSpawnResult(int Pid, KeepAliveProcessIdentity Identity);

internal interface IWslKeepAliveProcessHost
{
    KeepAliveSpawnResult Spawn(string distroName, IOpenClawLogger? logger = null);
    bool TryGetProcessIdentity(int pid, out KeepAliveProcessIdentity identity);
    void Kill(int pid);
}

internal sealed class DefaultWslKeepAliveProcessHost : IWslKeepAliveProcessHost
{
    public static readonly DefaultWslKeepAliveProcessHost Instance = new();

    public KeepAliveSpawnResult Spawn(string distroName, IOpenClawLogger? logger = null)
    {
        // The keepalive must outlive the tray. Packaged (MSIX/Centennial) WinUI apps
        // run inside a job object that by default kills child processes when the
        // parent exits — Process.Start alone would not survive an MSIX tray exit. Try
        // CreateProcess with CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS first; if
        // the host job forbids breakaway (ERROR_ACCESS_DENIED), fall back to a plain
        // Process.Start so the keepalive still arms while the tray runs.
        ValidateDistroNameForCommandLine(distroName);

        var wslPath = ResolveWslPath();
        var commandLine = BuildCommandLine(wslPath, distroName);
        var pid = Win32DetachedSpawn.TryStart(wslPath, commandLine, breakaway: true, out var firstError);
        if (pid is null && firstError == Win32DetachedSpawn.ErrorAccessDenied)
        {
            logger?.Warn("[WslKeepAlive] CREATE_BREAKAWAY_FROM_JOB denied; retrying detached keepalive without breakaway.");
            pid = Win32DetachedSpawn.TryStart(wslPath, commandLine, breakaway: false, out var secondError);
            if (pid is null)
                logger?.Warn($"[WslKeepAlive] Detached CreateProcess without breakaway failed (Win32={secondError}); falling back to Process.Start.");
        }
        else if (pid is null)
        {
            logger?.Warn($"[WslKeepAlive] Detached CreateProcess with breakaway failed (Win32={firstError}); falling back to Process.Start.");
        }

        if (pid is null)
        {
            return SpawnViaProcessFallback(wslPath, distroName);
        }

        return new KeepAliveSpawnResult(pid.Value, CaptureIdentity(pid.Value));
    }

    public bool TryGetProcessIdentity(int pid, out KeepAliveProcessIdentity identity)
    {
        identity = default!;
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            identity = new KeepAliveProcessIdentity(SafeProcessName(process), SafeStartTimeUtc(process));
            return true;
        }
        catch (ArgumentException) { return false; }     // No such PID
        catch (InvalidOperationException) { return false; } // Process exited between calls
        catch (System.ComponentModel.Win32Exception) { return false; } // Access denied / handle invalidated
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string ResolveWslPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory))
            return "wsl.exe";
        return Path.Combine(systemDirectory, "wsl.exe");
    }

    private static void ValidateDistroNameForCommandLine(string distroName)
    {
        // OpenClaw controls the production distro name ("OpenClawGateway"). Keep the
        // manual CreateProcess command line deliberately conservative so embedded
        // quotes/backslashes cannot alter argv parsing for a detached child process.
        if (!Regex.IsMatch(distroName, @"^[A-Za-z0-9._-]+$"))
            throw new ArgumentException($"WSL distro name '{distroName}' contains unsupported characters.", nameof(distroName));
    }

    private static string BuildCommandLine(string wslPath, string distroName)
    {
        return $"{QuoteWindowsArgument(wslPath)} -d {QuoteWindowsArgument(distroName)} -u openclaw -- sleep 2147483647";
    }

    private static string QuoteWindowsArgument(string value)
    {
        // This helper is intentionally minimal because distroName is allowlisted and
        // the executable path comes from System32. It still escapes quotes defensively.
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private KeepAliveSpawnResult SpawnViaProcessFallback(string wslPath, string distroName)
    {
        // Used only when CreateProcess fails outright (not for the breakaway-denied
        // case, which is handled above). Process.Start is more permissive about path
        // resolution and error reporting.
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = wslPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            ArgumentList = { "-d", distroName, "-u", "openclaw", "--", "sleep", "2147483647" }
        }) ?? throw new InvalidOperationException("Process.Start returned null for wsl.exe keepalive.");

        var pid = process.Id;
        var name = SafeProcessName(process);
        var startUtc = SafeStartTimeUtc(process);
        process.Dispose();
        return new KeepAliveSpawnResult(pid, new KeepAliveProcessIdentity(name, startUtc));
    }

    private static KeepAliveProcessIdentity CaptureIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new KeepAliveProcessIdentity(SafeProcessName(process), SafeStartTimeUtc(process));
        }
        catch (ArgumentException)
        {
            // Process exited between CreateProcess and GetProcessById. Fall back to a
            // best-effort identity so the marker is still written; next adoption will
            // fail naturally and we'll respawn.
            return new KeepAliveProcessIdentity(string.Empty, DateTime.UtcNow);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new KeepAliveProcessIdentity(string.Empty, DateTime.UtcNow);
        }
    }

    private static string SafeProcessName(Process process)
    {
        try { return process.ProcessName; }
        catch (InvalidOperationException) { return string.Empty; }
        catch (System.ComponentModel.Win32Exception) { return string.Empty; }
    }

    /// <summary>
    /// Reads <see cref="Process.StartTime"/> defensively. If <c>wsl.exe</c> exits between
    /// <see cref="Process.Start"/> and this call (e.g., the distro is unhealthy), the
    /// property can throw <see cref="InvalidOperationException"/>. Falling back to
    /// <c>UtcNow</c> means adoption may fail on the very next launch — the worst case is
    /// one redundant <c>sleep</c> spawn, which is preferable to crashing the spawn path.
    /// </summary>
    private static DateTime SafeStartTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { return DateTime.UtcNow; }
        catch (System.ComponentModel.Win32Exception) { return DateTime.UtcNow; }
    }
}

/// <summary>
/// Minimal P/Invoke wrapper around <c>CreateProcessW</c> so the keepalive can request
/// <see cref="CREATE_BREAKAWAY_FROM_JOB"/> — required to survive MSIX/packaged tray
/// exit, where the host job otherwise kills child processes.
/// </summary>
internal static class Win32DetachedSpawn
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint DETACHED_PROCESS = 0x00000008;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    public const int ErrorAccessDenied = 5;

    /// <summary>
    /// Spawns <paramref name="commandLine"/> with <c>DETACHED_PROCESS | CREATE_NO_WINDOW</c>
    /// (optionally adding <c>CREATE_BREAKAWAY_FROM_JOB</c>). Returns the new PID on
    /// success, or <see langword="null"/> on failure with the Win32 error in
    /// <paramref name="win32Error"/>.
    /// </summary>
    public static int? TryStart(string applicationName, string commandLine, bool breakaway, out int win32Error)
    {
        win32Error = 0;
        var si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
        var pi = default(PROCESS_INFORMATION);

        // CreateProcess may mutate the command-line buffer in place.
        var commandLineBuffer = new StringBuilder(commandLine);

        var flags = DETACHED_PROCESS | CREATE_NO_WINDOW;
        if (breakaway) flags |= CREATE_BREAKAWAY_FROM_JOB;

        var ok = CreateProcessW(
            lpApplicationName: applicationName,
            lpCommandLine: commandLineBuffer,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: flags,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: null,
            lpStartupInfo: ref si,
            lpProcessInformation: out pi);

        if (!ok)
        {
            win32Error = Marshal.GetLastWin32Error();
            return null;
        }

        // We don't need the process/thread handles; close them so we don't leak.
        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        return (int)pi.dwProcessId;
    }
}

/// <summary>
/// Testable startup arming loop for the local WSL gateway keepalive. The tray calls
/// this from a fire-and-forget task at launch; tests pass fake probe/ensure delegates
/// so timeout/failure behavior never depends on a real WSL installation.
/// </summary>
internal static class WslKeepAliveStartupArmer
{
    public static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60)
    ];

    public static async Task ArmAsync(
        string distroName,
        Func<CancellationToken, Task<WslCommandResult>> probeAsync,
        Action ensureStarted,
        Action<string> warn,
        TimeSpan[]? retryDelays = null,
        TimeSpan? probeTimeout = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        if (string.IsNullOrWhiteSpace(distroName))
            return;

        retryDelays ??= DefaultRetryDelays;
        probeTimeout ??= TimeSpan.FromSeconds(5);
        delayAsync ??= Task.Delay;

        for (var attempt = 0; attempt < retryDelays.Length; attempt++)
        {
            if (retryDelays[attempt] > TimeSpan.Zero)
                await delayAsync(retryDelays[attempt]);

            using var cts = new CancellationTokenSource(probeTimeout.Value);
            WslCommandResult result;
            try
            {
                result = await probeAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                warn($"[WslKeepAlive] wsl --list probe cancelled/timed out (attempt {attempt + 1}/{retryDelays.Length}); will retry.");
                continue;
            }

            if (!result.Success)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                warn($"[WslKeepAlive] wsl --list failed (exit {result.ExitCode}, attempt {attempt + 1}/{retryDelays.Length}): {SecretRedactor.Redact(detail)}");
                continue;
            }

            var distros = WslExeCommandRunner.ParseDistroList(result.StandardOutput);
            if (!distros.Any(d => d.Name.Equals(distroName, StringComparison.OrdinalIgnoreCase)))
            {
                warn($"[WslKeepAlive] Configured local-gateway distro '{distroName}' not present; skipping keepalive.");
                return;
            }

            ensureStarted();
            return;
        }

        warn("[WslKeepAlive] wsl --list never succeeded; spawning keepalive defensively.");
        ensureStarted();
    }
}

/// <summary>
/// Manages the WSL VM keepalive process for the local-gateway distro. Spawns a
/// detached <c>wsl.exe -d &lt;distro&gt; -u openclaw -- sleep 2147483647</c> that
/// keeps the WSL2 VM running independently of the tray's lifetime. A marker file
/// at <c>%LOCALAPPDATA%\OpenClawTray\wsl-keepalive\&lt;distro&gt;.json</c>
/// (overridable via <c>OPENCLAW_TRAY_LOCALAPPDATA_DIR</c>) records the spawned
/// PID + start time so subsequent tray launches can adopt the existing keepalive
/// instead of spawning duplicates.
/// </summary>
public static class WslDistroKeepAlive
{
    private const string MarkerDirectoryName = "wsl-keepalive";
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    // Test seams.
    internal static IWslKeepAliveProcessHost? __TestHost;
    internal static string? __TestMarkerDirectory;

    internal static void __ResetForTests()
    {
        __TestHost = null;
        __TestMarkerDirectory = null;
    }

    /// <summary>
    /// Ensures a keepalive process is running for <paramref name="distroName"/>. Adopts
    /// an existing keepalive (recorded via marker file) when the PID is still alive and
    /// its start time matches the marker; otherwise spawns a new one.
    /// Non-throwing — failures are logged.
    /// </summary>
    public static void EnsureStarted(string distroName, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(distroName))
            return;

        lock (s_lock)
        {
            var host = __TestHost ?? DefaultWslKeepAliveProcessHost.Instance;
            var markerPath = GetMarkerPath(distroName);

            try
            {
                if (TryReadMarker(markerPath, out var marker)
                    && string.Equals(marker.DistroName, distroName, StringComparison.OrdinalIgnoreCase)
                    && host.TryGetProcessIdentity(marker.Pid, out var identity)
                    && IdentityMatchesMarker(identity, marker))
                {
                    logger?.Info($"Adopted existing WSL keepalive for {distroName} (PID {marker.Pid}).");
                    return;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to inspect WSL keepalive marker for {distroName}: {ex.Message}");
                // Fall through to spawn fresh.
            }

            try
            {
                var spawn = host.Spawn(distroName, logger);
                var pid = spawn.Pid;
                try
                {
                    WriteMarker(markerPath, new KeepAliveMarker(distroName, pid, spawn.Identity.StartTimeUtc, spawn.Identity.Name));
                }
                catch (Exception writeEx)
                {
                    // Transactional invariant: a spawned keepalive without a marker is
                    // an untracked orphan. Future tray launches cannot adopt it (no
                    // marker), Stop/StopAll cannot reach it, and it will outlive
                    // distro repair/remove. Kill the just-spawned PID before
                    // surfacing the failure.
                    logger?.Warn($"Marker write failed after spawn for {distroName}; killing orphan PID {pid}: {writeEx.Message}");
                    try { host.Kill(pid); } catch { /* best-effort cleanup */ }
                    throw;
                }
                logger?.Info($"Started WSL keepalive process for {distroName} (PID {pid}).");
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to start WSL keepalive process for {distroName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops the keepalive for <paramref name="distroName"/> if the marker file
    /// references a process whose identity (name + start time) still matches.
    /// If the PID has been recycled to an unrelated process, only the marker is
    /// deleted — we never kill an unverified PID.
    /// </summary>
    public static void Stop(string distroName, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(distroName))
            return;

        lock (s_lock)
        {
            var host = __TestHost ?? DefaultWslKeepAliveProcessHost.Instance;
            var markerPath = GetMarkerPath(distroName);
            if (!TryReadMarker(markerPath, out var marker))
            {
                if (File.Exists(markerPath))
                {
                    logger?.Warn($"WSL keepalive marker for {distroName} is malformed; deleting marker only.");
                    TryDeleteMarker(markerPath);
                }
                return;
            }

            try
            {
                if (host.TryGetProcessIdentity(marker.Pid, out var identity) && IdentityMatchesMarker(identity, marker))
                {
                    host.Kill(marker.Pid);
                    logger?.Info($"Stopped WSL keepalive for {distroName} (PID {marker.Pid}).");
                }
                else
                {
                    logger?.Info($"WSL keepalive marker for {distroName} did not match a live process (PID {marker.Pid}); removing marker only.");
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to stop WSL keepalive for {distroName}: {ex.Message}");
            }
            finally
            {
                TryDeleteMarker(markerPath);
            }
        }
    }

    /// <summary>
    /// Stops keepalives for every recorded marker. Best-effort; used by uninstall.
    /// </summary>
    public static void StopAll(IOpenClawLogger? logger = null)
    {
        string? markerDir;
        lock (s_lock)
        {
            markerDir = ResolveMarkerDirectory();
        }

        if (markerDir is null || !Directory.Exists(markerDir))
            return;

        // Materialize before iterating: Stop() deletes each marker file as it processes
        // it, and FindNextFile semantics on Windows can skip entries when the directory
        // mutates mid-enumeration.
        var markerFiles = Directory.EnumerateFiles(markerDir, "*.json").ToList();
        foreach (var file in markerFiles)
        {
            try
            {
                if (TryReadMarkerFromPath(file, out var marker))
                {
                    Stop(marker.DistroName, logger);
                }
                else if (File.Exists(file))
                {
                    logger?.Warn($"WSL keepalive marker {file} is malformed; deleting marker only.");
                    TryDeleteMarker(file);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to stop keepalive recorded in {file}: {ex.Message}");
            }
        }
    }

    private static bool IdentityMatchesMarker(KeepAliveProcessIdentity identity, KeepAliveMarker marker)
    {
        // Name comparison is a best-effort sanity check ("wsl") — Spawn captures whatever
        // the OS reports, which may be empty if the process exited before ProcessName was
        // resolved. Start time is the authoritative anti-PID-recycle check; allow a small
        // tolerance for clock-resolution rounding through serialization.
        if (!string.IsNullOrEmpty(marker.ProcessName)
            && !string.IsNullOrEmpty(identity.Name)
            && !string.Equals(identity.Name, marker.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var delta = (identity.StartTimeUtc - marker.StartTimeUtc).Duration();
        // Generous tolerance: DateTime.ToUniversalTime + JSON serialization can round to
        // 100-tick (10us) increments depending on the kind, and on slow disks the live
        // process's reported StartTime can drift slightly from the value cached at spawn.
        // 500ms is well below any plausible PID-recycle window (Windows PID reuse takes
        // at least seconds in practice) so we don't lose anti-recycle protection.
        return delta <= TimeSpan.FromMilliseconds(500);
    }

    private static string? ResolveMarkerDirectory()
    {
        if (!string.IsNullOrWhiteSpace(__TestMarkerDirectory))
            return __TestMarkerDirectory;

        var root = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            return null;

        return Path.Combine(root, "OpenClawTray", MarkerDirectoryName);
    }

    private static string GetMarkerPath(string distroName)
    {
        var dir = ResolveMarkerDirectory()
            ?? throw new InvalidOperationException("Could not resolve LocalApplicationData for WSL keepalive marker.");
        var safe = SanitizeForFileName(distroName);
        return Path.Combine(dir, safe + ".json");
    }

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return builder.ToString();
    }

    private static bool TryReadMarker(string markerPath, out KeepAliveMarker marker)
        => TryReadMarkerFromPath(markerPath, out marker);

    private static bool TryReadMarkerFromPath(string markerPath, out KeepAliveMarker marker)
    {
        marker = default!;
        try
        {
            if (!File.Exists(markerPath)) return false;
            var json = File.ReadAllText(markerPath);
            var parsed = JsonSerializer.Deserialize<KeepAliveMarker>(json, s_jsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.DistroName) || parsed.Pid <= 0)
                return false;
            marker = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void WriteMarker(string markerPath, KeepAliveMarker marker)
    {
        var directory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Atomic write: serialize to a temp file then atomically replace the destination
        // (overwrite handles the case where another tray instance won the race and wrote
        // first — see hanselman review #1). NTFS guarantees File.Move with overwrite is
        // atomic on the same volume.
        var tempPath = markerPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(marker, s_jsonOptions));
        File.Move(tempPath, markerPath, overwrite: true);
    }

    private static void TryDeleteMarker(string markerPath)
    {
        try { if (File.Exists(markerPath)) File.Delete(markerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record KeepAliveMarker(
    string DistroName,
    int Pid,
    DateTime StartTimeUtc,
    string ProcessName);

public enum GatewayOperatorConnectionStatus
{
    Connected,
    PairingRequired,
    AuthFailed,
    Timeout,
    Failed
}

public sealed record GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus Status, string? ErrorMessage = null, string? PairingRequestId = null);

public interface IGatewayOperatorConnector
{
    Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default);
    Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default);
}

// OpenClawGatewayOperatorConnector (direct OpenClawGatewayClient instantiation, no
// diagnostics tee) has been removed — all operator pairing now flows through
// ConnectionManagerOperatorConnector → GatewayConnectionManager. See
// docs/node-connection-architecture.md and easy-button-gateway-comms-audit.md.

public sealed class WslGatewayCliSharedGatewayTokenProvider : ISharedGatewayTokenProvider
{
    private static readonly Regex s_safeHexTokenRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IWslCommandRunner _wsl;

    public WslGatewayCliSharedGatewayTokenProvider(IWslCommandRunner wsl)
    {
        _wsl = wsl;
    }

    public async Task<SharedGatewayTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        var read = await _wsl.RunAsync(
            ["-d", state.DistroName, "--", "bash", "-lc", "cat /var/lib/openclaw/gateway-token 2>/dev/null"],
            cancellationToken);
        var existing = (read.StandardOutput ?? string.Empty).Trim();
        if (read.Success && s_safeHexTokenRegex.IsMatch(existing))
            return new SharedGatewayTokenResult(true, existing, SharedGatewayTokenSource.PreservedFromWsl);

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(bytes).ToLowerInvariant();
        return new SharedGatewayTokenResult(true, token, SharedGatewayTokenSource.Generated);
    }
}

public sealed class SettingsSharedGatewayTokenProvisioner : ISharedGatewayTokenProvisioner
{
    private readonly ILocalGatewaySetupSettings _settings;
    private readonly ISharedGatewayTokenProvider _tokenProvider;
    private readonly IGatewayConfigurationPreparer _gatewayConfigurationPreparer;

    public SettingsSharedGatewayTokenProvisioner(
        ILocalGatewaySetupSettings settings,
        ISharedGatewayTokenProvider tokenProvider,
        IGatewayConfigurationPreparer gatewayConfigurationPreparer)
    {
        _settings = settings;
        _tokenProvider = tokenProvider;
        _gatewayConfigurationPreparer = gatewayConfigurationPreparer;
    }

    public async Task<SharedGatewayProvisioningResult> ProvisionAsync(LocalGatewaySetupState state, LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        var minted = await _tokenProvider.MintAsync(state, cancellationToken);
        if (!minted.Success || string.IsNullOrWhiteSpace(minted.Token))
        {
            return new SharedGatewayProvisioningResult(
                false,
                ErrorCode: minted.ErrorCode ?? "shared_gateway_token_missing",
                ErrorMessage: minted.ErrorMessage ?? "Gateway shared token could not be prepared.");
        }

        var prepared = await _gatewayConfigurationPreparer.PrepareAsync(options, minted.Token!, cancellationToken);
        if (!prepared.Success)
        {
            return new SharedGatewayProvisioningResult(
                false,
                minted.Token,
                minted.Source,
                prepared.ErrorCode ?? "gateway_config_prepare_failed",
                prepared.ErrorMessage ?? "Failed to prepare OpenClaw Gateway configuration.",
                prepared.Detail);
        }

        _settings.Token = minted.Token!;
        _settings.Save();
        return new SharedGatewayProvisioningResult(true, minted.Token, minted.Source);
    }
}

public sealed class SettingsBootstrapTokenProvisioner : IBootstrapTokenProvisioner
{
    private readonly ILocalGatewaySetupSettings _settings;
    private readonly IBootstrapTokenProvider _bootstrapTokenProvider;

    public SettingsBootstrapTokenProvisioner(ILocalGatewaySetupSettings settings, IBootstrapTokenProvider bootstrapTokenProvider)
    {
        _settings = settings;
        _bootstrapTokenProvider = bootstrapTokenProvider;
    }

    public async Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_settings.BootstrapToken))
            return new ProvisioningResult(true);

        var minted = await _bootstrapTokenProvider.MintAsync(state, cancellationToken);
        if (!minted.Success || string.IsNullOrWhiteSpace(minted.BootstrapToken))
        {
            return new ProvisioningResult(
                false,
                minted.ErrorCode ?? "bootstrap_token_missing",
                minted.ErrorMessage ?? "Gateway did not return a bootstrap token.");
        }

        _settings.BootstrapToken = minted.BootstrapToken;
        _settings.Save();
        return new ProvisioningResult(true);
    }
}

public sealed class SettingsOperatorPairingService : IOperatorPairingService
{
    private readonly ILocalGatewaySetupSettings _settings;
    private readonly IGatewayOperatorConnector? _connector;
    private readonly IPendingDeviceApprover? _pendingApprover;

    public SettingsOperatorPairingService(SettingsManager settings, IGatewayOperatorConnector? connector = null, IPendingDeviceApprover? pendingApprover = null)
        : this(new SettingsManagerLocalGatewaySetupSettings(settings), connector, pendingApprover)
    {
    }

    public SettingsOperatorPairingService(ILocalGatewaySetupSettings settings, IGatewayOperatorConnector? connector = null, IPendingDeviceApprover? pendingApprover = null)
    {
        _settings = settings;
        _connector = connector;
        _pendingApprover = pendingApprover;
    }

    public async Task<ProvisioningResult> PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        var credential = ResolveCredential();
        if (credential is null)
        {
            return new ProvisioningResult(
                false,
                "operator_credential_missing",
                "A gateway token or bootstrap token is required before the tray can pair as an operator.");
        }

        _settings.GatewayUrl = state.GatewayUrl;
        _settings.UseSshTunnel = false;
        _settings.Save();

        if (_connector == null)
            return new ProvisioningResult(true);

        var result = await _connector.ConnectAsync(state.GatewayUrl, credential.Value, credential.IsBootstrapToken, cancellationToken);

        // Fresh bootstrap-token connects keep the historical --latest approval path.
        // Fresh standard local-loopback connects may request operator.admin, so they must approve
        // the exact structured requestId returned by the failed connect. Missing/malformed requestId
        // fails closed by skipping auto-approval and surfacing PairingRequired below.
        if (result.Status == GatewayOperatorConnectionStatus.PairingRequired
            && _pendingApprover != null
            && LocalGatewayApprover.IsLocalGateway(state.GatewayUrl)
            && (credential.IsBootstrapToken || result.PairingRequestId is not null))
        {
            var approval = credential.IsBootstrapToken
                ? await _pendingApprover.ApproveLatestAsync(state, cancellationToken)
                : await _pendingApprover.ApproveExplicitAsync(state, result.PairingRequestId!, cancellationToken);
            if (!approval.Success)
            {
                return new ProvisioningResult(
                    false,
                    approval.ErrorCode ?? "operator_pending_approval_failed",
                    approval.ErrorMessage ?? "Local gateway pending pairing approval failed.");
            }

            result = await _connector.ConnectAsync(state.GatewayUrl, credential.Value, credential.IsBootstrapToken, cancellationToken);
        }

        if (result.Status != GatewayOperatorConnectionStatus.Connected)
        {
            return result.Status switch
            {
                GatewayOperatorConnectionStatus.PairingRequired => new ProvisioningResult(false, "operator_pairing_required", result.ErrorMessage),
                GatewayOperatorConnectionStatus.AuthFailed => new ProvisioningResult(false, "operator_auth_failed", result.ErrorMessage),
                GatewayOperatorConnectionStatus.Timeout => new ProvisioningResult(false, "operator_pairing_timeout", result.ErrorMessage),
                _ => new ProvisioningResult(false, "operator_pairing_failed", result.ErrorMessage ?? "Operator pairing failed.")
            };
        }

        if (credential.IsBootstrapToken)
        {
            var reconnectResult = await _connector.ConnectWithStoredDeviceTokenAsync(state.GatewayUrl, cancellationToken);
            if (reconnectResult.Status != GatewayOperatorConnectionStatus.Connected)
            {
                return reconnectResult.Status switch
                {
                    GatewayOperatorConnectionStatus.PairingRequired => new ProvisioningResult(false, "operator_reconnect_pairing_required", reconnectResult.ErrorMessage),
                    GatewayOperatorConnectionStatus.AuthFailed => new ProvisioningResult(false, "operator_reconnect_auth_failed", reconnectResult.ErrorMessage),
                    GatewayOperatorConnectionStatus.Timeout => new ProvisioningResult(false, "operator_reconnect_timeout", reconnectResult.ErrorMessage),
                    _ => new ProvisioningResult(false, "operator_reconnect_failed", reconnectResult.ErrorMessage ?? "Operator reconnect with stored device token failed.")
                };
            }

            _settings.Save();
        }

        return new ProvisioningResult(true);
    }

    private ResolvedOperatorCredential? ResolveCredential()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token))
            return new ResolvedOperatorCredential(_settings.Token, false);

        if (!string.IsNullOrWhiteSpace(_settings.BootstrapToken))
            return new ResolvedOperatorCredential(_settings.BootstrapToken, true);

        return null;
    }

    private sealed record ResolvedOperatorCredential(string Value, bool IsBootstrapToken);
}

public sealed class WslGatewayCliBootstrapTokenProvider : IBootstrapTokenProvider
{
    private readonly IWslCommandRunner _wsl;
    private readonly string _commandName;

    public WslGatewayCliBootstrapTokenProvider(IWslCommandRunner wsl, string commandName = "openclaw")
    {
        _wsl = wsl;
        _commandName = commandName;
    }

    public async Task<BootstrapTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        var script = string.Join(" ", new[]
        {
            "set -euo pipefail;",
            "if [ -f /var/lib/openclaw/gateway.env ]; then set -a; . /var/lib/openclaw/gateway.env; set +a; fi;",
            "exec",
            ShellQuote(_commandName),
            "qr",
            "--json",
            "--url",
            ShellQuote(state.GatewayUrl)
        });
        var result = await _wsl.RunInDistroAsync(state.DistroName, ["bash", "-lc", script], cancellationToken);
        if (!result.Success)
            return new BootstrapTokenResult(false, ErrorCode: "bootstrap_token_command_failed", ErrorMessage: "Gateway bootstrap-token command failed.");

        return ParseQrJson(result.StandardOutput);
    }

    public static BootstrapTokenResult ParseQrJson(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (!TryGetString(root, "bootstrapToken", out var token)
                && !TryGetString(root, "bootstrap_token", out token)
                && !TryGetString(root, "token", out token))
            {
                if (!TryGetString(root, "setupCode", out var setupCode)
                    && !TryGetString(root, "setup_code", out setupCode))
                {
                    return new BootstrapTokenResult(false, ErrorCode: "bootstrap_token_missing", ErrorMessage: "Gateway QR output did not include a bootstrap token or setup code.");
                }

                var decoded = SetupCodeDecoder.Decode(setupCode);
                if (!decoded.Success)
                    return new BootstrapTokenResult(false, ErrorCode: "setup_code_invalid", ErrorMessage: decoded.Error ?? "Gateway setup code could not be decoded.");

                if (string.IsNullOrWhiteSpace(decoded.Token))
                    return new BootstrapTokenResult(false, ErrorCode: "bootstrap_token_missing", ErrorMessage: "Gateway setup code did not include a bootstrap token.");

                token = decoded.Token;
            }

            return new BootstrapTokenResult(true, token, TryGetExpiry(root));
        }
        catch (JsonException ex)
        {
            return new BootstrapTokenResult(false, ErrorCode: "bootstrap_token_json_invalid", ErrorMessage: ex.Message);
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static DateTimeOffset? TryGetExpiry(JsonElement root)
    {
        foreach (var name in new[] { "expiresAtMs", "expires_at_ms" })
        {
            if (root.TryGetProperty(name, out var property) && property.TryGetInt64(out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        foreach (var name in new[] { "expiresAt", "expires_at", "expires", "expiry" })
        {
            if (root.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public sealed record PendingDeviceApprovalResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// Approves the most-recent pending device pairing request on a local-loopback gateway by
/// invoking <c>openclaw devices approve --latest</c> via the gateway CLI inside WSL.
/// Used during operator bootstrap pairing where the same user is both operator and approver.
/// </summary>
public interface IPendingDeviceApprover
{
    Task<PendingDeviceApprovalResult> ApproveLatestAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default);
    Task<PendingDeviceApprovalResult> ApproveExplicitAsync(LocalGatewaySetupState state, string requestId, CancellationToken cancellationToken = default);
}

public sealed class WslGatewayCliPendingDeviceApprover : IPendingDeviceApprover
{
    // Bug 1 part 5 (CLI v2026.5.3-1): Bostick-11 Round-3 verification (commit 05f7be0)
    // proved the retry from part 4 IS firing but BOTH attempts of stage 1 still exit
    // non-zero with EMPTY stderr. The same script, when invoked manually via
    // `wsl -- bash -lc <script>` from PowerShell against the engine's exact post-failure
    // gateway state, returns exit 0 with valid preview JSON. So the failure is in the
    // engine's invocation context, not the script.
    //
    // Leading hypothesis: the embedded `"$(cat /var/lib/openclaw/gateway-token)"` shell
    // substitution gets mangled when .NET's `ProcessStartInfo.ArgumentList` quoting
    // forwards the script through `wsl.exe` to `bash -lc` — the embedded double-quotes
    // around the substitution interact badly with .NET's MSVCRT-style escaping and/or
    // wsl.exe's own argv re-encoding, leaving bash with an empty/malformed `--token`
    // argument and causing the CLI to silently exit non-zero. Manual PowerShell
    // invocations don't reproduce because PowerShell's own tokenization differs.
    //
    // Fix: read the gateway token via a SEPARATE `wsl ... cat` call (no embedded quotes,
    // no shell substitution), then interpolate it into the approve script as a
    // single-quoted shell literal. The approve script body now contains:
    //   - NO `$(...)` shell substitution
    //   - NO `"` characters at all
    // so there's nothing for .NET / wsl.exe argv encoding to mangle.
    //
    // Also surfaces STDOUT (in addition to stderr) for both stage-1 attempts and stage 2,
    // so if some other invocation-context issue is still at play the next regression is
    // diagnosable from `setup-state.json` alone.
    //
    // See Bostick-11 Round-3 (`bostick-bug1-reverify.md` "Path B re-drive — Round 3").
    public const int MaxStderrSurfaceLength = 1024;
    public const int MaxStdoutSurfaceLength = 1024;
    private static readonly TimeSpan DefaultStage1RetryDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// When the gateway restarts mid-PairWindowsTrayNode (which can happen 10-15s after
    /// operator pairing — gateway flushes config and restarts the service), the WSL CLI
    /// briefly cannot reach the gateway and exits non-zero with stderr containing
    /// "1006 abnormal closure" / "gateway closed" / "Gateway not yet ready". The original
    /// 750ms single-retry isn't enough for the gateway to come back. When we detect this
    /// pattern in both initial attempts, we keep retrying with exponential backoff up to
    /// this cap so the WSL approve flow survives a transient gateway restart instead of
    /// failing with the misleading operator_pending_approval_failed code.
    /// </summary>
    private static readonly TimeSpan[] GatewayDownRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    /// <summary>
    /// Patterns in CLI stderr that indicate the gateway is transiently down (typically
    /// mid-restart) rather than a real pairing/auth failure.
    /// </summary>
    private static readonly string[] GatewayDownStderrPatterns =
    [
        "1006 abnormal closure",
        "gateway closed",
        "Gateway not yet ready",
        "Could not start the CLI",
    ];

    private readonly IWslCommandRunner _wsl;
    private readonly string _commandName;
    private readonly TimeSpan _stage1RetryDelay;

    public WslGatewayCliPendingDeviceApprover(IWslCommandRunner wsl, string commandName = "openclaw")
        : this(wsl, commandName, DefaultStage1RetryDelay)
    {
    }

    public WslGatewayCliPendingDeviceApprover(IWslCommandRunner wsl, string commandName, TimeSpan stage1RetryDelay)
    {
        _wsl = wsl;
        _commandName = commandName;
        _stage1RetryDelay = stage1RetryDelay;
    }

    public async Task<PendingDeviceApprovalResult> ApproveLatestAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        // Bug 1 part 3 (CLI v2026.5.3-1): `openclaw devices approve --latest --json` is a
        // PREVIEW/inspection-only operation on this CLI version. It returns a JSON payload
        // (`{ "selected": {...}, "approvalState": {...}, "approveCommand": "openclaw devices
        // approve <requestId> --json", "requiresAuthFlags": {...} }`) and exits 0 without
        // ever invoking `device.pair.approve` or mutating `paired.json`. To actually
        // approve, the CLI requires a follow-up call with the explicit requestId.
        //
        // Source: src/cli/devices-cli.ts (commit aef38de) — the `usingImplicitSelection`
        // branch (set when `--latest` or no requestId is supplied) writes the preview JSON
        // and `return`s before reaching `approvePairingWithFallback`. Only an explicit
        // requestId argument bypasses the preview gate.
        //
        // We therefore run two stages in the same approver call:
        //   Stage 1 — discover: `openclaw devices approve --latest --json --token '<TOK>'`
        //     parses `selected.requestId` from the preview JSON.
        //   Stage 2 — commit:   `openclaw devices approve <requestId> --json --token '<TOK>'`
        //     actually calls `device.pair.approve` (or the local pairing fallback) and
        //     mutates `paired.json`.
        //
        // Bug 1 part 5: the gateway token is read via a SEPARATE `cat` invocation and
        // passed to approve through OPENCLAW_GATEWAY_TOKEN, so it never lands in the
        // `bash -lc` script literal or the child openclaw argv.
        //
        // Stage 1 is retried ONCE on first failure (Bug 1 part 4 race) with a small
        // backoff so the second attempt benefits from any internal operator pairing the
        // failed first attempt provoked as a side effect.
        //
        // We continue to drop `--url` (Bug 1 part 2 / CLI ensureExplicitGatewayAuth guard).
        var tokenResult = await ReadGatewayTokenAsync(state, cancellationToken);
        if (!tokenResult.Success)
        {
            return new PendingDeviceApprovalResult(false, tokenResult.ErrorCode, tokenResult.ErrorMessage);
        }
        var token = tokenResult.Token!;
        var tokenEnvironment = BuildGatewayTokenEnvironment(token);

        var stage1 = await RunStage1WithRetryAsync(state, tokenEnvironment, cancellationToken);

        // Bug 1 part 6 (CLI v2026.5.3-1): `devices approve --latest --json` returns
        // exit code 1 DETERMINISTICALLY in preview mode even on the happy path. The
        // CLI uses exit-1 to signal "preview only, no actual approve performed"; the
        // valid preview JSON on stdout (with `selected.requestId`, `approveCommand`,
        // `requiresAuthFlags`) IS the success signal. Confirmed by Bostick-11 Round-4
        // smoking-gun capture + manual stage-2 reproduction (`bostick-bug1-reverify.md`,
        // "Path B re-drive — Round 4"). We therefore parse the stdout JSON FIRST and
        // only fall through to a structured stage-1 failure when no usable preview
        // shape can be extracted. Exit code remains the secondary signal: it gates the
        // failure branch (stderr-vs-no-pending-entries discrimination) only after the
        // primary parseable-preview check has failed.
        var preview = ParsePreviewJson(stage1.Result.StandardOutput);
        if (!preview.Success)
        {
            if (!stage1.Result.Success)
            {
                return BuildStage1Failure(stage1.FirstResult, stage1.Result);
            }
            return new PendingDeviceApprovalResult(false, preview.ErrorCode, preview.ErrorMessage);
        }

        var requestId = preview.RequestId!;
        if (!IsSafeRequestId(requestId))
        {
            return new PendingDeviceApprovalResult(
                false,
                "operator_pending_approval_failed",
                $"Local gateway preview returned an unsafe requestId: {requestId}");
        }

        var stage2 = await _wsl.RunInDistroAsync(
            state.DistroName,
            ["bash", "-lc", BuildCommitScript(requestId)],
            cancellationToken,
            tokenEnvironment);
        if (!stage2.Success)
        {
            return BuildStage2Failure(stage2);
        }

        return ParseApproveJson(stage2.StandardOutput);
    }

    public async Task<PendingDeviceApprovalResult> ApproveExplicitAsync(LocalGatewaySetupState state, string requestId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeRequestId(requestId))
        {
            return new PendingDeviceApprovalResult(
                false,
                "operator_pending_approval_failed",
                $"Local gateway pairing requestId was unsafe: {requestId}");
        }

        var tokenResult = await ReadGatewayTokenAsync(state, cancellationToken);
        if (!tokenResult.Success)
        {
            return new PendingDeviceApprovalResult(false, tokenResult.ErrorCode, tokenResult.ErrorMessage);
        }

        var stage2 = await _wsl.RunInDistroAsync(
            state.DistroName,
            ["bash", "-lc", BuildCommitScript(requestId)],
            cancellationToken,
            BuildGatewayTokenEnvironment(tokenResult.Token!));
        if (!stage2.Success)
        {
            return BuildStage2Failure(stage2);
        }

        return ParseApproveJson(stage2.StandardOutput);
    }

    private async Task<Stage1Outcome> RunStage1WithRetryAsync(LocalGatewaySetupState state, IReadOnlyDictionary<string, string> tokenEnvironment, CancellationToken cancellationToken)
    {
        var first = await _wsl.RunInDistroAsync(
            state.DistroName,
            ["bash", "-lc", BuildPreviewScript()],
            cancellationToken,
            tokenEnvironment);
        // Bug 1 part 6: treat exit-0 OR a parseable preview JSON as stage-1 success.
        // CLI v2026.5.3-1 returns exit 1 from `--latest --json` on the happy preview
        // path (deterministic — see ApproveLatestAsync comment), so a non-zero exit
        // alone is no longer sufficient to trigger the retry. Without this check, the
        // common success path would always burn the 750ms retry delay before advancing.
        if (first.Success || ParsePreviewJson(first.StandardOutput).Success)
        {
            return new Stage1Outcome(first, FirstResult: null);
        }

        // Bug 1 part 4: the failed first call may itself have caused the gateway to
        // auto-pair the in-distro internal Linux operator as a side effect. A second
        // invocation made shortly after typically succeeds. Wait briefly, then retry once.
        if (_stage1RetryDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_stage1RetryDelay, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return new Stage1Outcome(first, FirstResult: first);
            }
        }

        var second = await _wsl.RunInDistroAsync(
            state.DistroName,
            ["bash", "-lc", BuildPreviewScript()],
            cancellationToken,
            tokenEnvironment);
        if (second.Success || ParsePreviewJson(second.StandardOutput).Success)
        {
            return new Stage1Outcome(second, FirstResult: first);
        }

        // Bug: gateway-restart mid-PairWindowsTrayNode (manual test 2026-05-16) — when the
        // gateway emits a 1012 service-restart shortly after operator pairing, both initial
        // stage-1 attempts can hit a still-restarting gateway and fail with stderr like:
        //   [openclaw] Reason: gateway closed (1006 abnormal closure (no close frame))
        // The 750ms retry window isn't enough for the gateway to come back. If we recognize
        // this pattern in both attempts, keep retrying with exponential backoff so the
        // engine doesn't misreport this as operator_pending_approval_failed.
        if (LooksLikeGatewayDown(first) && LooksLikeGatewayDown(second))
        {
            var last = second;
            foreach (var delay in GatewayDownRetryDelays)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return new Stage1Outcome(last, FirstResult: first);
                }

                var attempt = await _wsl.RunInDistroAsync(
                    state.DistroName,
                    ["bash", "-lc", BuildPreviewScript()],
                    cancellationToken,
                    tokenEnvironment);
                if (attempt.Success || ParsePreviewJson(attempt.StandardOutput).Success)
                {
                    return new Stage1Outcome(attempt, FirstResult: first);
                }
                last = attempt;
                if (!LooksLikeGatewayDown(attempt))
                {
                    // Stopped looking like a transient gateway-down failure — bail and let
                    // the caller surface this attempt's stderr verbatim.
                    return new Stage1Outcome(last, FirstResult: first);
                }
            }
            return new Stage1Outcome(last, FirstResult: first);
        }

        return new Stage1Outcome(second, FirstResult: first);
    }

    /// <summary>
    /// Heuristic match for "the gateway is down right now, retry will probably succeed"
    /// — see <see cref="GatewayDownStderrPatterns"/>.
    /// </summary>
    internal static bool LooksLikeGatewayDown(WslCommandResult result)
    {
        if (result.Success) return false;
        var stderr = result.StandardError ?? string.Empty;
        if (stderr.Length == 0) return false;
        foreach (var pattern in GatewayDownStderrPatterns)
        {
            if (stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Bug 1 part 5: read the gateway token via a separate, simple <c>cat</c>
    /// invocation. Returns <c>operator_pending_approval_failed</c> with diagnostics
    /// when the file is missing, empty, unreadable, or non-canonical.
    /// </summary>
    internal async Task<TokenReadResult> ReadGatewayTokenAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
    {
        var read = await _wsl.RunInDistroAsync(
            state.DistroName,
            ["bash", "-lc", "cat /var/lib/openclaw/gateway-token"],
            cancellationToken);
        if (!read.Success)
        {
            var stderr = TruncateStderr(read.StandardError) ?? "(no stderr)";
            return TokenReadResult.Failure(
                "operator_pending_approval_failed",
                "Local gateway pending pairing approval CLI failed (token-read stage). "
                + $"exit={read.ExitCode} stderr={stderr}");
        }
        var token = (read.StandardOutput ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            return TokenReadResult.Failure(
                "operator_pending_approval_failed",
                "Local gateway pending pairing approval CLI failed (token-read stage): token file empty.");
        }
        // RD review item #4: only canonical 64-char lowercase hex gateway tokens may reach
        // OPENCLAW_GATEWAY_TOKEN, matching the sanitizer's bare-token redaction shape.
        if (!IsCanonicalGatewayToken(token))
        {
            return TokenReadResult.Failure(
                "operator_pending_approval_failed",
                "Local gateway pending pairing approval CLI failed (token-read stage): token is not canonical 64-character lowercase hex.");
        }
        return TokenReadResult.Ok(token);
    }

    private static IReadOnlyDictionary<string, string> BuildGatewayTokenEnvironment(string token) => new Dictionary<string, string>
    {
        [OpenClawGatewayTokenEnvironment.VariableName] = token
    };

    private static PendingDeviceApprovalResult BuildStage1Failure(WslCommandResult? firstAttempt, WslCommandResult lastAttempt)
    {
        // Bug 1 part 4/5 diagnosability: surface BOTH stderr AND stdout from BOTH attempts
        // so future regressions in this race-prone area do not require digging into tray.log.
        // Each stream is independently truncated to 1 KB. Suffixes are only appended when
        // their content is non-empty AND distinct from attempt 1 (no duplication).
        const string baseMessage = "Local gateway pending pairing approval CLI failed (preview stage).";
        var sb = new StringBuilder(baseMessage);

        var firstErr = TruncateStderr(firstAttempt?.StandardError);
        var firstOut = TruncateStdout(firstAttempt?.StandardOutput);
        var lastErr = TruncateStderr(lastAttempt.StandardError);
        var lastOut = TruncateStdout(lastAttempt.StandardOutput);

        if (firstAttempt != null)
        {
            sb.Append(" stage1.attempt1.exit=").Append(firstAttempt.ExitCode);
        }
        if (!string.IsNullOrEmpty(firstErr))
        {
            sb.Append(" stage1.attempt1.stderr=").Append(firstErr);
        }
        if (!string.IsNullOrEmpty(firstOut))
        {
            sb.Append(" stage1.attempt1.stdout=").Append(firstOut);
        }

        sb.Append(" stage1.attempt2.exit=").Append(lastAttempt.ExitCode);
        if (!string.IsNullOrEmpty(lastErr) && !string.Equals(lastErr, firstErr, StringComparison.Ordinal))
        {
            sb.Append(" stage1.attempt2.stderr=").Append(lastErr);
        }
        if (!string.IsNullOrEmpty(lastOut) && !string.Equals(lastOut, firstOut, StringComparison.Ordinal))
        {
            sb.Append(" stage1.attempt2.stdout=").Append(lastOut);
        }

        return new PendingDeviceApprovalResult(false, "operator_pending_approval_failed", sb.ToString());
    }

    private static PendingDeviceApprovalResult BuildStage2Failure(WslCommandResult result)
    {
        // Bug 1 part 5: also surface stdout for stage-2 failure so a CLI that writes
        // structured JSON errors to stdout in `--json` mode is observable.
        var stderr = TruncateStderr(result.StandardError);
        var stdout = TruncateStdout(result.StandardOutput);
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
        {
            return new PendingDeviceApprovalResult(
                false,
                "operator_pending_approval_failed",
                $"Local gateway pending pairing approval CLI failed (commit stage). stage2.exit={result.ExitCode}");
        }
        var sb = new StringBuilder("Local gateway pending pairing approval CLI failed (commit stage).");
        sb.Append(" stage2.exit=").Append(result.ExitCode);
        if (!string.IsNullOrEmpty(stderr)) sb.Append(" stage2.stderr=").Append(stderr);
        if (!string.IsNullOrEmpty(stdout)) sb.Append(" stage2.stdout=").Append(stdout);
        // Backwards-compatible shape: when only stderr is present, also keep the bare
        // stderr in ErrorMessage so the existing failure-shape consumers continue to read it.
        if (!string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
        {
            return new PendingDeviceApprovalResult(false, "operator_pending_approval_failed", stderr);
        }
        return new PendingDeviceApprovalResult(false, "operator_pending_approval_failed", sb.ToString());
    }

    public static string? TruncateStderr(string? stderr) => TruncateStream(stderr, MaxStderrSurfaceLength);
    public static string? TruncateStdout(string? stdout) => TruncateStream(stdout, MaxStdoutSurfaceLength);

    private static string? TruncateStream(string? value, int cap)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(value.Trim()));
        if (sanitized.Length <= cap) return sanitized;
        return sanitized.Substring(0, cap) + "…[truncated]";
    }

    internal static bool IsCanonicalGatewayToken(string token) =>
        Regex.IsMatch(token, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    internal readonly record struct Stage1Outcome(WslCommandResult Result, WslCommandResult? FirstResult);

    internal readonly record struct TokenReadResult(bool Success, string? Token, string? ErrorCode, string? ErrorMessage)
    {
        public static TokenReadResult Ok(string token) => new(true, token, null, null);
        public static TokenReadResult Failure(string code, string message) => new(false, null, code, message);
    }

    internal string BuildPreviewScript() => string.Join(" ", new[]
    {
        "set -euo pipefail;",
        "if [ -f /var/lib/openclaw/gateway.env ]; then set -a; . /var/lib/openclaw/gateway.env; set +a; fi;",
        @": ""${OPENCLAW_GATEWAY_TOKEN:?missing gateway token}"";",
        "exec",
        ShellQuoteScalar(_commandName),
        "devices",
        "approve",
        "--latest",
        "--json"
    });

    internal string BuildCommitScript(string requestId) => string.Join(" ", new[]
    {
        "set -euo pipefail;",
        "if [ -f /var/lib/openclaw/gateway.env ]; then set -a; . /var/lib/openclaw/gateway.env; set +a; fi;",
        @": ""${OPENCLAW_GATEWAY_TOKEN:?missing gateway token}"";",
        "exec",
        ShellQuoteScalar(_commandName),
        "devices",
        "approve",
        ShellQuoteScalar(requestId),
        "--json"
    });

    /// <summary>
    /// Parse the v2026.5.3-1 `devices approve --latest --json` preview payload and extract
    /// the pending requestId for the stage-2 commit call. Returns a structured failure with
    /// <c>no_pending_entries</c> when the preview indicates nothing approvable.
    /// </summary>
    public static PreviewParseResult ParsePreviewJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return PreviewParseResult.Failure("no_pending_entries", "No pending device pairing requests to approve.");
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PreviewParseResult.Failure("no_pending_entries", "Preview JSON was not an object.");
            }

            // Explicit `ok:false` legacy shape — surface as approval failure.
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var legacyMsg = root.TryGetProperty("error", out var legacyErr) && legacyErr.ValueKind == JsonValueKind.String
                    ? legacyErr.GetString()
                    : "Local gateway preview reported failure.";
                return PreviewParseResult.Failure("operator_pending_approval_failed", legacyMsg);
            }

            // v2026.5.3-1 preview shape: { "selected": { "requestId": "...", ... }, ... }
            if (root.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Object
                && selected.TryGetProperty("requestId", out var reqId) && reqId.ValueKind == JsonValueKind.String)
            {
                var id = reqId.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return PreviewParseResult.SuccessWith(id!);
                }
            }

            // Tolerate an older flat shape some CLI builds may have used: { "requestId": "..." }.
            if (root.TryGetProperty("requestId", out var rootReqId) && rootReqId.ValueKind == JsonValueKind.String)
            {
                var id = rootReqId.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return PreviewParseResult.SuccessWith(id!);
                }
            }

            return PreviewParseResult.Failure("no_pending_entries", "Preview JSON had no selected.requestId.");
        }
        catch (JsonException)
        {
            // Plain-text non-JSON output (e.g. older CLI / "No pending device pairing
            // requests to approve" on stderr-but-stdout-empty edge cases). Treat as no
            // pending entries so the engine surfaces a structured failure rather than
            // silently succeeding.
            return PreviewParseResult.Failure("no_pending_entries", "Preview output was not JSON; assuming no pending entries.");
        }
    }

    public static PendingDeviceApprovalResult ParseApproveJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new PendingDeviceApprovalResult(true);

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False)
            {
                var msg = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : "Local gateway approval reported failure.";
                return new PendingDeviceApprovalResult(false, "operator_pending_approval_failed", msg);
            }

            return new PendingDeviceApprovalResult(true);
        }
        catch (JsonException)
        {
            // Plain-text success output from older CLI versions; treat exit-0 as success.
            return new PendingDeviceApprovalResult(true);
        }
    }

    private static bool IsSafeRequestId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        var first = value[0];
        if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || (first >= '0' && first <= '9')))
            return false;

        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c == '-' || c == '_' || c == '.' || c == ':';
            if (!ok) return false;
        }
        return true;
    }

    private static string ShellQuoteScalar(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public sealed record PreviewParseResult(bool Success, string? RequestId, string? ErrorCode, string? ErrorMessage)
{
    public static PreviewParseResult SuccessWith(string requestId) => new(true, requestId, null, null);
    public static PreviewParseResult Failure(string code, string? message) => new(false, null, code, message);
}

public sealed class SettingsWindowsTrayNodeProvisioner : IWindowsTrayNodeProvisioner
{
    /// <summary>
    /// Default wait between approve and the single retry connect after a
    /// role-upgrade PAIRING_REQUIRED. Gives the gateway a moment to
    /// register the just-issued approval before the node re-connects.
    /// </summary>
    public static readonly TimeSpan DefaultPairRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILocalGatewaySetupSettings _settings;
    private readonly IWindowsNodeConnector? _connector;
    private readonly IPendingDeviceApprover? _pendingApprover;
    private readonly TimeSpan _pairRetryDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public SettingsWindowsTrayNodeProvisioner(SettingsManager settings, IWindowsNodeConnector? connector = null, IPendingDeviceApprover? pendingApprover = null)
        : this(new SettingsManagerLocalGatewaySetupSettings(settings), connector, pendingApprover)
    {
    }

    public SettingsWindowsTrayNodeProvisioner(
        ILocalGatewaySetupSettings settings,
        IWindowsNodeConnector? connector = null,
        IPendingDeviceApprover? pendingApprover = null,
        TimeSpan? pairRetryDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _settings = settings;
        _connector = connector;
        _pendingApprover = pendingApprover;
        var delay = pairRetryDelay ?? DefaultPairRetryDelay;
        _pairRetryDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public Task<ProvisioningResult> CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Token)
            && string.IsNullOrWhiteSpace(_settings.BootstrapToken)
            && !HasStoredNodeDeviceToken())
        {
            return Task.FromResult(new ProvisioningResult(
                false,
                "windows_node_credential_missing",
                "A gateway token, bootstrap token, or stored node device token is required before enabling the Windows tray node."));
        }

        return Task.FromResult(new ProvisioningResult(true));
    }

    public async Task<ProvisioningResult> PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
    {
        var readiness = await CheckReadinessAsync(state, cancellationToken);
        if (!readiness.Success)
            return readiness;

        _settings.GatewayUrl = state.GatewayUrl;
        _settings.UseSshTunnel = false;
        _settings.EnableNodeMode = true;
        _settings.Save();

        if (_connector != null)
        {
            try
            {
                await _connector.ConnectAsync(state.GatewayUrl, _settings.Token, _settings.BootstrapToken, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Bug 3 (2026-05-05): on a fresh local-loopback gateway, the Phase 14
                // node-role connect arrives at the gateway as `reason=role-upgrade`
                // (the device is approved as `operator` from Phase 12 but is now asking
                // for the additional `node` role). The gateway parks the request on the
                // pending-pairing list and the connect attempt times out with
                // `windows_node_pairing_failed`. There is no auto-approve handler on
                // this path upstream — the canonical mechanism is the same
                // `openclaw devices approve` flow Phase 12 uses for the operator
                // pending. On loopback the tray user IS the approver, so we drive the
                // approval automatically via the same approver wired in for Phase 12,
                // then retry the connect once. Bostick-11 Round-5 evidence:
                // `bostick-bug1-reverify.md` "## Path B re-drive — Round 5",
                // requestId `a80b5dbe-9ad2-4a32-baa9-d7d93aeb50dc`, isRepair=true.
                if (_pendingApprover != null && LocalGatewayApprover.IsLocalGateway(state.GatewayUrl))
                {
                    // On a fresh local-loopback gateway the Phase 14 node-role
                    // connect arrives as `reason=role-upgrade` (device is
                    // approved as `operator` from Phase 12 and is now asking
                    // for the additional `node` role). The gateway parks the
                    // request on the pending-pairing list and the connector
                    // surfaces a PairingRequired-fast-fail (when SuppressNodeAutoApprove
                    // is set by the V2 host) so we can drive the canonical
                    // approval via the WSL CLI device-approver — same flow
                    // Phase 12 used for the operator pending — then retry the
                    // connect once. The connector's IGatewayConnectionManager
                    // already has SuppressNodeAutoApprove engaged during V2
                    // onboarding, which (together with the fast-fail) means
                    // a single approve+retry consistently lands before the
                    // gateway's internal ~10s pending-approval timer fires.
                    var approval = await _pendingApprover.ApproveLatestAsync(state, cancellationToken);
                    if (!approval.Success)
                    {
                        return new ProvisioningResult(
                            false,
                            approval.ErrorCode ?? "windows_node_pending_approval_failed",
                            approval.ErrorMessage ?? "Local gateway pending role-upgrade approval failed.");
                    }

                    // Small settle window so the gateway commits the freshly-
                    // approved role-upgrade before the retry handshake.
                    try
                    {
                        await _delayAsync(_pairRetryDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }

                    try
                    {
                        await _connector.ConnectAsync(state.GatewayUrl, _settings.Token, _settings.BootstrapToken, cancellationToken);
                        return new ProvisioningResult(true);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception retryEx)
                    {
                        return new ProvisioningResult(false, "windows_node_pairing_failed", retryEx.Message);
                    }
                }
                else
                {
                    return new ProvisioningResult(false, "windows_node_pairing_failed", ex.Message);
                }
            }
        }

        return new ProvisioningResult(true);
    }

    private static bool HasStoredNodeDeviceToken()
    {
        var dataPath = Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray");

        return WindowsNodeClient.HasStoredNodeDeviceToken(dataPath);
    }
}

public sealed class LocalGatewaySetupEngine
{
    private readonly LocalGatewaySetupOptions _options;
    private readonly ILocalGatewaySetupStateStore _stateStore;
    private readonly ILocalGatewayPreflightProbe _preflight;
    private readonly IWslCommandRunner _wsl;
    private readonly IWslInstanceInstaller _wslInstanceInstaller;
    private readonly IWslInstanceConfigurator _wslInstanceConfigurator;
    private readonly IOpenClawLinuxInstaller _openClawLinuxInstaller;
    private readonly IGatewayConfigurationPreparer _gatewayConfigurationPreparer;
    private readonly ISharedGatewayTokenProvisioner? _sharedGatewayTokenProvisioner;
    private readonly IGatewayServiceManager _gatewayServiceManager;
    private readonly ILocalGatewayHealthProbe _healthProbe;
    private readonly ILocalGatewayEndpointResolver _endpointResolver;
    private readonly IBootstrapTokenProvisioner _bootstrapTokenProvisioner;
    private readonly IOperatorPairingService _operatorPairing;
    private readonly IWindowsTrayNodeProvisioner _windowsTrayNode;
    private readonly IWslPlatformInstaller? _wslPlatformInstaller;
    private readonly IOpenClawLogger _logger;
    private readonly ILocalGatewaySetupDiagnosticsSink _diagnostics;

    /// <summary>
    /// Set to true when this run's EnsureWslEnabled phase performed a fresh
    /// `wsl --install --no-distribution` (InstalledNoRestart outcome). Used
    /// by later phases (notably ConfigureWslInstance) to recognise that any
    /// downstream WSL failure may actually be a "needs reboot" symptom of
    /// the fresh platform install rather than a true configuration error.
    /// Gated by <see cref="LooksLikePostInstallKernelIssue"/> so unrelated
    /// configure errors (script bugs, missing packages) don't get a
    /// spurious "restart your PC" recommendation.
    /// </summary>
    private bool _wslPlatformJustInstalled;

    /// <summary>
    /// Pattern-matches a configure-phase diagnostic detail for signatures
    /// that indicate the WSL kernel / lifted-WSL service hasn't fully
    /// picked up a fresh platform install. When we see one of these, AND
    /// we know we just installed WSL in this session, recommend a reboot.
    /// For unrelated configure errors (script syntax, missing packages,
    /// permissions inside the distro) we keep the generic message so the
    /// user doesn't get a misleading "reboot to fix" suggestion.
    /// Strict match — round-2 review noted that defaulting to true on
    /// null/empty Detail (or matching the bare substring "kernel") was
    /// too permissive and re-introduced exactly the false-positive the
    /// gate is supposed to eliminate, so we now require a positive
    /// kernel/host-compute signature and return false for blank Detail.
    /// </summary>
    internal static bool LooksLikePostInstallKernelIssue(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;
        var d = detail.Replace("\0", string.Empty);
        return d.Contains("WslRegisterDistribution", StringComparison.OrdinalIgnoreCase)
            || d.Contains("Wsl/Service", StringComparison.OrdinalIgnoreCase)
            || d.Contains("HCS_E_", StringComparison.OrdinalIgnoreCase)
            || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || d.Contains("WSL kernel", StringComparison.OrdinalIgnoreCase)
            || d.Contains("WSL2 kernel", StringComparison.OrdinalIgnoreCase)
            || d.Contains("kernel component", StringComparison.OrdinalIgnoreCase)
            || d.Contains("kernel image", StringComparison.OrdinalIgnoreCase)
            || d.Contains("vmcompute", StringComparison.OrdinalIgnoreCase)
            || d.Contains("LxssManager", StringComparison.OrdinalIgnoreCase)
            || d.Contains("0x80370102", StringComparison.OrdinalIgnoreCase)   // virtual machine could not be started
            || d.Contains("instance is corrupted", StringComparison.OrdinalIgnoreCase)
            || d.Contains("WSL2 requires", StringComparison.OrdinalIgnoreCase)
            || d.Contains("class not registered", StringComparison.OrdinalIgnoreCase);
    }

    public event Action<LocalGatewaySetupState>? StateChanged;

    /// <summary>
    /// True only while the engine is executing Phase 14 (PairWindowsTrayNode) — i.e. the
    /// node-role <see cref="IWindowsTrayNodeProvisioner.PairAsync"/> call against the
    /// loopback gateway. Bug #2 (manual test 2026-05-05): the loopback gateway parks the
    /// node-role connect as <c>PairingStatus.Pending</c> for ~100ms before
    /// <see cref="SettingsWindowsTrayNodeProvisioner"/>'s pending-approver auto-approves
    /// it. <see cref="App.OnPairingStatusChanged"/> consults this flag to suppress the
    /// "copy pairing command" toast for that transient blip — manual ConnectionPage-driven
    /// pairings call <c>App.ShowPairingPendingNotification</c> directly and bypass the
    /// event path entirely, so the suppression scope is exactly the Phase 14 window.
    /// </summary>
    public bool IsAutoPairingWindowsNode => _isAutoPairingWindowsNode != 0;

    private int _isAutoPairingWindowsNode; // 0 = false, 1 = true (Interlocked semantics)

    /// <summary>
    /// Pure decision helper for the App-level "copy pairing command" toast suppression.
    /// Extracted for unit testability — App.OnPairingStatusChanged delegates here. Returns
    /// true ONLY when the engine reports it is mid-Phase-14 AND the status is Pending; all
    /// other status values (Paired, Rejected, etc.) and all states without an active
    /// auto-pair window are not suppressed.
    /// </summary>
    public static bool ShouldSuppressPairingPendingNotification(
        LocalGatewaySetupEngine? engine,
        OpenClaw.Shared.PairingStatus status)
        => status == OpenClaw.Shared.PairingStatus.Pending
           && engine?.IsAutoPairingWindowsNode == true;

    public LocalGatewaySetupEngine(
        LocalGatewaySetupOptions options,
        ILocalGatewaySetupStateStore stateStore,
        ILocalGatewayPreflightProbe preflight,
        IWslCommandRunner wsl,
        ILocalGatewayHealthProbe healthProbe,
        IBootstrapTokenProvisioner bootstrapTokenProvisioner,
        IOperatorPairingService operatorPairing,
        IWindowsTrayNodeProvisioner windowsTrayNode,
        IOpenClawLogger? logger = null,
        IWslInstanceInstaller? wslInstanceInstaller = null,
        IWslInstanceConfigurator? wslInstanceConfigurator = null,
        IOpenClawLinuxInstaller? openClawLinuxInstaller = null,
        IGatewayConfigurationPreparer? gatewayConfigurationPreparer = null,
        IGatewayServiceManager? gatewayServiceManager = null,
        ILocalGatewayEndpointResolver? endpointResolver = null,
        ISharedGatewayTokenProvisioner? sharedGatewayTokenProvisioner = null,
        IWslPlatformInstaller? wslPlatformInstaller = null,
        ILocalGatewaySetupDiagnosticsSink? diagnosticsSink = null)
    {
        _options = options;
        _stateStore = stateStore;
        _preflight = preflight;
        _wsl = wsl;
        _wslInstanceInstaller = wslInstanceInstaller ?? new WslStoreInstanceInstaller(wsl);
        _wslInstanceConfigurator = wslInstanceConfigurator ?? new WslFirstBootConfigurator(wsl);
        _openClawLinuxInstaller = openClawLinuxInstaller ?? new OpenClawInstallCliLinuxInstaller(wsl);
        _gatewayConfigurationPreparer = gatewayConfigurationPreparer ?? new OpenClawCliGatewayConfigurationPreparer(wsl);
        _sharedGatewayTokenProvisioner = sharedGatewayTokenProvisioner;
        _gatewayServiceManager = gatewayServiceManager ?? new OpenClawCliGatewayServiceManager(wsl);
        _healthProbe = healthProbe;
        _endpointResolver = endpointResolver ?? new LocalGatewayEndpointResolver();
        _bootstrapTokenProvisioner = bootstrapTokenProvisioner;
        _operatorPairing = operatorPairing;
        _windowsTrayNode = windowsTrayNode;
        _wslPlatformInstaller = wslPlatformInstaller;
        _logger = logger ?? NullLogger.Instance;
        _diagnostics = diagnosticsSink ?? NullLocalGatewaySetupDiagnosticsSink.Instance;
    }

    public async Task<LocalGatewaySetupState> RunLocalOnlyAsync(CancellationToken cancellationToken = default)
    {
        var runStopwatch = Stopwatch.StartNew();
        var state = await _stateStore.LoadAsync(cancellationToken) ?? LocalGatewaySetupState.Create(_options);
        state.RunId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(state.InstallId))
            state.InstallId = Guid.NewGuid().ToString("N");
        state.DistroName = _options.DistroName;
        state.GatewayUrl = LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(_options);

        // Round-2 fix: reset the per-run "we just installed WSL" flag at the
        // top of every RunLocalOnlyAsync. Engines created by the factory are
        // typically single-use, but nothing in the API contract prevents a
        // caller from invoking RunLocalOnlyAsync twice on the same instance.
        // Without this reset, a stale flag from a prior run would cause
        // unrelated configure failures on the second run to be misclassified
        // as "needs reboot".
        _wslPlatformJustInstalled = false;

        // Self-heal persisted non-running statuses on entry. Without this,
        // RunPhaseAsync's `Status is Pending or Running` guard short-circuits
        // every phase on a relaunch / Try-again and the engine silently
        // replays the cached failure forever. Cases covered:
        //
        //   * RequiresRestart  — original M4 (post-reboot resume)
        //   * FailedRetryable  — the "Try again" button currently has no
        //     effect without this (OnRetryRequested doesn't touch the state
        //     file), so the user sees the same error after every retry.
        //   * FailedTerminal / Blocked / Cancelled / RequiresAdmin — the
        //     user explicitly relaunched setup; reset to a fresh evaluation
        //     so preflight gets a chance to re-classify the host.
        //
        // Complete stays as-is so we don't redo a successful setup. Pending
        // / Running stay as-is (fresh / in-flight).
        if (state.Status is not LocalGatewaySetupStatus.Pending
            and not LocalGatewaySetupStatus.Running
            and not LocalGatewaySetupStatus.Complete)
        {
            _logger.Info($"[Engine] Previous run ended in Status={state.Status} (FailureCode={state.FailureCode ?? "<null>"}); resetting state so the wizard can re-evaluate the host.");
            state.Status = LocalGatewaySetupStatus.Pending;
            state.Phase = LocalGatewaySetupPhase.NotStarted;
            state.FailureCode = null;
            state.UserMessage = null;
            // Wipe any WSL-install-cycle issues. Preflight will repopulate
            // state.Issues with fresh signals on its next run.
            state.Issues.RemoveAll(i => WslInstallCycleIssueCodes.All.Contains(i.Code));
        }

        _diagnostics.RunStarted(state, _options);

        // Bound the distro existence check the same way preflight bounds its
        // own wsl --status probe. On a host without WSL, the underlying
        // wsl --list --verbose hangs for the engine's 30s default timeout,
        // showing up to the user as a "blank wizard" pause between the
        // "Set up locally" click and any UI feedback. The platform probe
        // takes ~1s and tells us we can skip the distro probe entirely
        // (no WSL platform → no possible distro).
        bool distroExists;
        {
            // Construct an ad-hoc platform probe sharing the engine's wsl
            // runner — same 5s short-timeout default as preflight uses. The
            // engine doesn't (yet) hold a probe field of its own; we keep
            // this local until/unless another phase needs it.
            var platformProbe = new WslPlatformProbe(_wsl);
            var platform = await platformProbe.ProbeAsync(cancellationToken);
            distroExists = platform.State == WslPlatformState.Installed
                ? await HasDistroAsync(cancellationToken)
                : false;
        }
        var allowExistingDistroForRun = ShouldAllowExistingDistroForRun(state, distroExists, _options.AllowExistingDistro);
        var preflightOptions = _options with { AllowExistingDistro = allowExistingDistroForRun };

        await RunPhaseAsync(state, LocalGatewaySetupPhase.Preflight, "Checking your PC", async () =>
        {
            var result = await _preflight.RunAsync(preflightOptions, cancellationToken);
            state.Issues = result.Issues.ToList();
            if (!result.CanContinue)
            {
                state.Block("preflight_blocked", "This PC is not ready for local WSL gateway setup.");
                return false;
            }

            if (result.RequiresRestart)
            {
                state.Status = LocalGatewaySetupStatus.RequiresRestart;
                state.UserMessage = "Restart required before OpenClaw local gateway setup can continue.";
                return false;
            }

            if (result.RequiresAdmin)
            {
                state.Status = LocalGatewaySetupStatus.RequiresAdmin;
                state.UserMessage = "Administrator approval is required before setup can continue.";
                return false;
            }

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.EnsureWslEnabled, "Checking WSL support", async () =>
        {
            if (state.Status != LocalGatewaySetupStatus.Running)
                return false;

            // If preflight flagged WSL platform as missing, install it now.
            // The issue is non-blocking (Warning severity) precisely so we
            // can reach this phase and run the elevated install rather than
            // dead-ending the wizard with "This PC is not ready".
            var missing = state.Issues.FirstOrDefault(i =>
                string.Equals(i.Code, "wsl_platform_not_installed", StringComparison.OrdinalIgnoreCase));
            if (missing is null)
                return true;

            if (_wslPlatformInstaller is null)
            {
                state.Block(
                    "wsl_install_unavailable",
                    "Windows Subsystem for Linux is not installed and OpenClaw cannot install it automatically in this build. Run `wsl --install` from an elevated terminal, then retry.",
                    retryable: true);
                return false;
            }

            state.UserMessage = "Installing Windows Subsystem for Linux\u2026";
            StateChanged?.Invoke(state);

            // Brief pause before raising UAC so the user has a chance to read
            // the "we're about to ask for permission to install WSL" hint the
            // wizard surfaced. Without this, the UAC popup appears in the
            // same frame the page transitions in and users instinctively
            // dismiss it before reading the context. Skippable via env var
            // for automated tests / power users.
#if !OPENCLAW_TRAY_TESTS
            var skipPause = Environment.GetEnvironmentVariable("OPENCLAW_SKIP_WSL_INSTALL_PROMPT_PAUSE");
            if (!(skipPause is "1" or "true" or "TRUE" or "yes" or "YES"))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
#endif

            WslPlatformInstallResult installResult;
            try
            {
                installResult = await _wslPlatformInstaller.InstallAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[WslInstall] InstallAsync threw: {ex.Message}");
                state.Block(
                    "wsl_install_failed",
                    "Couldn't install Windows Subsystem for Linux: " + ex.Message,
                    retryable: true);
                return false;
            }

            switch (installResult.Outcome)
            {
                case WslPlatformInstallOutcome.InstalledNoRestart:
                    // Drop the warning so the engine doesn't re-trigger the
                    // install path on a retry, and continue on.
                    state.Issues.RemoveAll(i => string.Equals(i.Code, WslInstallCycleIssueCodes.PlatformNotInstalled, StringComparison.OrdinalIgnoreCase));
                    state.UserMessage = "Windows Subsystem for Linux installed.";
                    _wslPlatformJustInstalled = true;
                    return true;

                case WslPlatformInstallOutcome.InstalledRequiresRestart:
                    // Set Status=RequiresRestart (not Failed) so the stage map
                    // does not paint the "Check system" row as a hard red-X.
                    // We DO set FailureCode here, intentionally — that's the
                    // hook for the post-reboot self-heal at the top of
                    // RunLocalOnlyAsync. (The preflight RequiresRestart branch
                    // sets neither FailureCode nor an Issue; the self-heal
                    // covers both shapes by keying off Status only.)
                    state.UserMessage = "Windows Subsystem for Linux was installed. Restart your PC, then reopen OpenClaw to continue setup.";
                    state.Status = LocalGatewaySetupStatus.RequiresRestart;
                    state.FailureCode = WslInstallCycleIssueCodes.InstallRequiresRestart;
                    state.Issues.RemoveAll(i => string.Equals(i.Code, WslInstallCycleIssueCodes.PlatformNotInstalled, StringComparison.OrdinalIgnoreCase));
                    state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    return false;

                case WslPlatformInstallOutcome.UserDeclinedElevation:
                    state.Issues.RemoveAll(i => string.Equals(i.Code, WslInstallCycleIssueCodes.PlatformNotInstalled, StringComparison.OrdinalIgnoreCase));
                    state.Block(
                        WslInstallCycleIssueCodes.InstallElevationDeclined,
                        installResult.ErrorMessage ?? "Administrator approval is required to install Windows Subsystem for Linux.",
                        retryable: true,
                        detail: installResult.Detail);
                    return false;

                case WslPlatformInstallOutcome.Failed:
                default:
                    state.Issues.RemoveAll(i => string.Equals(i.Code, WslInstallCycleIssueCodes.PlatformNotInstalled, StringComparison.OrdinalIgnoreCase));
                    state.Block(
                        WslInstallCycleIssueCodes.InstallFailed,
                        installResult.ErrorMessage ?? "Couldn't install Windows Subsystem for Linux. Try again, or run `wsl --install` from an elevated terminal.",
                        retryable: true,
                        detail: installResult.Detail);
                    return false;
            }
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.CreateWslInstance, "Creating OpenClaw Gateway WSL instance", async () =>
        {
            var installOptions = _options with { AllowExistingDistro = allowExistingDistroForRun };
            var result = await _wslInstanceInstaller.EnsureInstalledAsync(installOptions, cancellationToken);
            if (!result.Success)
            {
                var detail = string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(detail))
                    _logger.Warn($"WSL instance install diagnostics: {SecretRedactor.Redact(detail)}");

                // Symmetric with the ConfigureWslInstance remap below: a
                // fresh WSL platform install can leave the kernel / lifted-
                // WSL service in a half-initialised state where the
                // *instance* install (`wsl --install <distro>`) trips the
                // same HCS / WslRegisterDistribution / 0x80370102 family
                // of errors that get fixed by a reboot. Without this branch,
                // users hitting those errors here would see
                // "wsl_instance_install_failed" ("Couldn't download Ubuntu")
                // and have no idea a reboot would fix it.
                var failureCode = _wslPlatformJustInstalled && LooksLikePostInstallKernelIssue(detail)
                    ? "wsl_firstboot_config_failed_after_install"
                    : (result.ErrorCode ?? "wsl_instance_install_failed");
                var failureMessage = _wslPlatformJustInstalled && LooksLikePostInstallKernelIssue(detail)
                    ? "Couldn't create the OpenClaw WSL instance. Windows Subsystem for Linux was just installed in this session — restart your PC, then reopen OpenClaw to continue setup."
                    : (result.ErrorMessage ?? "Failed to create the OpenClaw Gateway WSL instance.");

                state.Block(failureCode, failureMessage, retryable: true, detail: detail);
                return false;
            }

            foreach (var warning in result.Warnings ?? Array.Empty<string>())
                _logger.Warn($"WSL instance install warning: {SecretRedactor.Redact(warning)}");

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.ConfigureWslInstance, "Configuring OpenClaw Gateway WSL instance", async () =>
        {
            var result = await _wslInstanceConfigurator.ConfigureAsync(_options, cancellationToken);
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    _logger.Warn($"WSL instance configuration diagnostics: {SecretRedactor.Redact(result.Detail!)}");

                // If we just installed the WSL platform in this session AND
                // the configure error's signature looks like a kernel/lifted-
                // WSL platform issue (rather than an unrelated bash/systemd/
                // package problem), remap to the "restart your PC" failure
                // code. Microsoft's own guidance is to reboot after
                // `wsl --install`. The stderr-signature gate avoids telling
                // the user to reboot when the underlying cause is something
                // a reboot won't fix (e.g., curl pulling a script, missing
                // package, a script syntax error) — that wastes their time
                // and erodes trust.
                var failureCode = _wslPlatformJustInstalled && LooksLikePostInstallKernelIssue(result.Detail)
                    ? "wsl_firstboot_config_failed_after_install"
                    : (result.ErrorCode ?? "wsl_instance_config_failed");
                var failureMessage = _wslPlatformJustInstalled && LooksLikePostInstallKernelIssue(result.Detail)
                    ? "Couldn't configure the OpenClaw WSL instance. Windows Subsystem for Linux was just installed in this session — restart your PC, then reopen OpenClaw to continue setup."
                    : (result.ErrorMessage ?? "Failed to configure the OpenClaw Gateway WSL instance.");

                state.Block(failureCode, failureMessage, retryable: true, detail: result.Detail);
                return false;
            }

            foreach (var warning in result.Warnings ?? Array.Empty<string>())
                _logger.Warn($"WSL instance configuration warning: {SecretRedactor.Redact(warning)}");

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.InstallOpenClawCli, "Installing OpenClaw inside WSL", async () =>
        {
            var result = await _openClawLinuxInstaller.InstallAsync(_options, cancellationToken);
            foreach (var installerEvent in result.Events ?? Array.Empty<OpenClawLinuxInstallerEvent>())
                _diagnostics.InstallerEvent(LocalGatewaySetupPhase.InstallOpenClawCli, installerEvent);
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    _logger.Warn($"OpenClaw Linux installer diagnostics: {SecretRedactor.Redact(result.Detail)}");
                state.Block(result.ErrorCode ?? "openclaw_linux_install_failed", result.ErrorMessage ?? "The upstream OpenClaw Linux installer failed.", retryable: true, detail: result.Detail);
                return false;
            }

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.PrepareGatewayConfig, "Preparing OpenClaw Gateway configuration", async () =>
        {
            SharedGatewayProvisioningResult result;
            if (_sharedGatewayTokenProvisioner is null)
            {
                var prepared = await _gatewayConfigurationPreparer.PrepareAsync(_options, string.Empty, cancellationToken);
                result = new SharedGatewayProvisioningResult(prepared.Success, ErrorCode: prepared.ErrorCode, ErrorMessage: prepared.ErrorMessage, Detail: prepared.Detail);
            }
            else
            {
                result = await _sharedGatewayTokenProvisioner.ProvisionAsync(state, _options, cancellationToken);
            }

            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    _logger.Warn($"Gateway configuration diagnostics: {SecretRedactor.Redact(result.Detail)}");
                state.Block(result.ErrorCode ?? "gateway_config_prepare_failed", result.ErrorMessage ?? "Failed to prepare OpenClaw Gateway configuration.", retryable: true, detail: result.Detail);
                return false;
            }

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.InstallGatewayService, "Installing OpenClaw Gateway service", async () =>
        {
            var result = await _gatewayServiceManager.InstallAsync(_options, cancellationToken);
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    _logger.Warn($"Gateway service install diagnostics: {SecretRedactor.Redact(result.Detail)}");
                state.Block(result.ErrorCode ?? "gateway_service_install_failed", result.ErrorMessage ?? "Failed to install the OpenClaw Gateway service.", retryable: true, detail: result.Detail);
                return false;
            }

            return true;
        }, cancellationToken);

        await RunGatewayCliStartPhaseAsync(state, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.WaitForGateway, "Waiting for OpenClaw Gateway", async () =>
        {
            var result = await _endpointResolver.ResolveAsync(_options, state.GatewayUrl, _healthProbe, _wsl, cancellationToken);
            if (!result.Success)
            {
                state.Block("gateway_unhealthy", result.Error ?? WslLogsHelp("Gateway did not become healthy."), retryable: true);
                return false;
            }

            state.GatewayUrl = result.GatewayUrl;
            return true;
        }, cancellationToken);

        await RunProvisioningPhaseAsync(state, LocalGatewaySetupPhase.MintBootstrapToken, "Generating setup code", () => _bootstrapTokenProvisioner.MintAsync(state, cancellationToken), cancellationToken);
        await RunProvisioningPhaseAsync(state, LocalGatewaySetupPhase.PairOperator, "Pairing tray operator", () => _operatorPairing.PairAsync(state, cancellationToken), cancellationToken);

        if (_options.EnableWindowsTrayNodeByDefault)
        {
            await RunProvisioningPhaseAsync(state, LocalGatewaySetupPhase.CheckWindowsNodeReadiness, "Checking Windows node readiness", () => _windowsTrayNode.CheckReadinessAsync(state, cancellationToken), cancellationToken);
            // Bug #2 (manual test 2026-05-05): bracket the Phase 14 node-role PairAsync
            // exactly. The loopback gateway emits a transient PairingStatus.Pending event
            // before our pending-approver auto-approves; App.OnPairingStatusChanged
            // observes IsAutoPairingWindowsNode==true via this flag and suppresses the
            // "copy pairing command" toast for that blip only. Scope is the await above
            // the Pending event source (WindowsNodeClient → NodeService is synchronous on
            // HandleRequestError), so try/finally around the await is race-safe.
            await RunProvisioningPhaseAsync(state, LocalGatewaySetupPhase.PairWindowsTrayNode, "Pairing Windows tray node", async () =>
            {
                System.Threading.Interlocked.Exchange(ref _isAutoPairingWindowsNode, 1);
                try
                {
                    return await _windowsTrayNode.PairAsync(state, cancellationToken);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _isAutoPairingWindowsNode, 0);
                }
            }, cancellationToken);
        }

        await RunPhaseAsync(state, LocalGatewaySetupPhase.VerifyEndToEnd, "Verifying local gateway", () => Task.FromResult(state.Status == LocalGatewaySetupStatus.Running), cancellationToken);

        if (state.Status == LocalGatewaySetupStatus.Running)
        {
            state.IsLocalOnly = true;
            state.CompletePhase(LocalGatewaySetupPhase.Complete, "Local OpenClaw gateway is ready.");
            await SaveAndPublishAsync(state, cancellationToken);
        }

        runStopwatch.Stop();
        _diagnostics.RunCompleted(state, runStopwatch.Elapsed);
        await _diagnostics.FlushAsync(TimeSpan.FromSeconds(2), cancellationToken);
        return state;
    }

    internal static bool ShouldAllowExistingDistroForRun(
        LocalGatewaySetupState state,
        bool distroExists,
        bool configuredAllowExistingDistro)
    {
        if (configuredAllowExistingDistro)
            return true;

        if (!distroExists)
            return false;

        if (state.Phase == LocalGatewaySetupPhase.NotStarted
            || IsCreateOrLater(state.Phase))
        {
            return true;
        }

        return state.Phase == LocalGatewaySetupPhase.Failed
            && state.FailureCode == "preflight_blocked"
            && state.Issues.Any(issue => string.Equals(issue.Code, "distro_exists", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunGatewayCliStartPhaseAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
    {
        await RunPhaseAsync(state, LocalGatewaySetupPhase.StartGateway, "Starting OpenClaw Gateway", async () =>
        {
            var result = await _gatewayServiceManager.StartAsync(_options, cancellationToken);
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    _logger.Warn($"Gateway service start diagnostics: {SecretRedactor.Redact(result.Detail)}");
                state.Block(result.ErrorCode ?? "gateway_service_start_failed", result.ErrorMessage ?? WslLogsHelp("Failed to start OpenClaw Gateway."), retryable: true, detail: result.Detail);
                return false;
            }

            WslDistroKeepAlive.EnsureStarted(_options.DistroName, _logger);
            return true;
        }, cancellationToken);
    }

    private async Task RunProvisioningPhaseAsync(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message, Func<Task<ProvisioningResult>> action, CancellationToken cancellationToken)
    {
        await RunPhaseAsync(state, phase, message, async () =>
        {
            var result = await action();
            if (!result.Success)
            {
                state.Block(result.ErrorCode ?? "provisioning_failed", result.ErrorMessage ?? message, retryable: true);
                return false;
            }

            return true;
        }, cancellationToken);
    }

    private async Task RunPhaseAsync(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message, Func<Task<bool>> action, CancellationToken cancellationToken)
    {
        if (state.Status is not LocalGatewaySetupStatus.Pending and not LocalGatewaySetupStatus.Running)
            return;

        var phaseStopwatch = Stopwatch.StartNew();
        state.StartPhase(phase, message);
        _diagnostics.PhaseStarted(state, phase, message);
        await SaveAndPublishAsync(state, cancellationToken);
        bool completed;
        try
        {
            completed = await action();
        }
        catch (OperationCanceledException)
        {
            state.Status = LocalGatewaySetupStatus.Cancelled;
            state.UserMessage = "Setup was cancelled.";
            // Persist cancelled state so restarts don't resume from stale Running phase
            try { await _stateStore.SaveAsync(state, CancellationToken.None); } catch { }
            StateChanged?.Invoke(state);
            phaseStopwatch.Stop();
            _diagnostics.PhaseCompleted(state, phase, message, phaseStopwatch.Elapsed);
            await _diagnostics.FlushAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Local gateway setup phase {phase} failed.", ex);
            var retryable = ex is not (UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException);
            state.Block($"{phase.ToString().ToLowerInvariant()}_failed", ex.Message, retryable: retryable, detail: SecretRedactor.Redact(ex.ToString()));
            phaseStopwatch.Stop();
            _diagnostics.PhaseCompleted(state, phase, message, phaseStopwatch.Elapsed);
            await SaveAndPublishAsync(state, cancellationToken);
            await _diagnostics.FlushAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return;
        }

        if (completed && state.Status == LocalGatewaySetupStatus.Running)
        {
            state.CompletePhase(phase, message);
            phaseStopwatch.Stop();
            _diagnostics.PhaseCompleted(state, phase, message, phaseStopwatch.Elapsed);
            await SaveAndPublishAsync(state, cancellationToken);
        }
        else if (!completed)
        {
            phaseStopwatch.Stop();
            _diagnostics.PhaseCompleted(state, phase, message, phaseStopwatch.Elapsed);
            await SaveAndPublishAsync(state, cancellationToken);
            if (state.Status is LocalGatewaySetupStatus.FailedRetryable or LocalGatewaySetupStatus.FailedTerminal or LocalGatewaySetupStatus.Blocked)
                await _diagnostics.FlushAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task SaveAndPublishAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
    {
        await _stateStore.SaveAsync(state, cancellationToken);
        StateChanged?.Invoke(state);
    }

    private async Task<bool> HasDistroAsync(CancellationToken cancellationToken)
    {
        var distros = await _wsl.ListDistrosAsync(cancellationToken);
        return distros.Any(d => string.Equals(d.Name, _options.DistroName, StringComparison.OrdinalIgnoreCase) && d.Version == 2);
    }

    private static bool IsCreateOrLater(LocalGatewaySetupPhase phase)
    {
        return phase is LocalGatewaySetupPhase.CreateWslInstance
            or LocalGatewaySetupPhase.ConfigureWslInstance
            or LocalGatewaySetupPhase.InstallOpenClawCli
            or LocalGatewaySetupPhase.PrepareGatewayConfig
            or LocalGatewaySetupPhase.InstallGatewayService
            or LocalGatewaySetupPhase.StartGateway
            or LocalGatewaySetupPhase.WaitForGateway
            or LocalGatewaySetupPhase.MintBootstrapToken
            or LocalGatewaySetupPhase.PairOperator
            or LocalGatewaySetupPhase.CheckWindowsNodeReadiness
            or LocalGatewaySetupPhase.PairWindowsTrayNode
            or LocalGatewaySetupPhase.VerifyEndToEnd
            or LocalGatewaySetupPhase.Complete;
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
}

public sealed record LocalGatewayLifecycleResult(
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? Steps = null);

public sealed record LocalGatewayRemoveRequest(
    bool ConfirmRemove,
    bool ClearLocalCredentials,
    bool PreserveRelayRegistration = true);

public interface ILocalGatewayLifecycleManager
{
    Task<LocalGatewayLifecycleResult> RepairAsync(CancellationToken cancellationToken = default);
    Task<LocalGatewayLifecycleResult> RemoveAsync(LocalGatewayRemoveRequest request, CancellationToken cancellationToken = default);
}

public sealed class LocalGatewayLifecycleManager : ILocalGatewayLifecycleManager
{
    private readonly LocalGatewaySetupOptions _options;
    private readonly IWslCommandRunner _wsl;
    private readonly ILocalGatewayHealthProbe _healthProbe;
    private readonly ILocalGatewaySetupSettings? _settings;
    private readonly IOpenClawLogger? _logger;
    private readonly ILocalGatewaySetupDiagnosticsSink _diagnostics;

    public LocalGatewayLifecycleManager(
        LocalGatewaySetupOptions options,
        IWslCommandRunner wsl,
        ILocalGatewayHealthProbe healthProbe,
        ILocalGatewaySetupSettings? settings = null,
        IOpenClawLogger? logger = null,
        ILocalGatewaySetupDiagnosticsSink? diagnosticsSink = null)
    {
        _options = options;
        _wsl = wsl;
        _healthProbe = healthProbe;
        _settings = settings;
        _logger = logger;
        _diagnostics = diagnosticsSink ?? NullLocalGatewaySetupDiagnosticsSink.Instance;
    }

    public async Task<LocalGatewayLifecycleResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        var lifecycleStopwatch = Stopwatch.StartNew();
        _diagnostics.LifecycleStarted("repair");
        var steps = new List<string>();
        var distros = await _wsl.ListDistrosAsync(cancellationToken);
        if (!distros.Any(d => d.Name.Equals(_options.DistroName, StringComparison.OrdinalIgnoreCase) && d.Version == 2))
        {
            _diagnostics.LifecycleStep("repair", "distro_present", success: false, "distro_missing", $"The OpenClaw WSL distro '{_options.DistroName}' was not found.");
            return await CompleteLifecycleAsync("repair", Fail("distro_missing", $"The OpenClaw WSL distro '{_options.DistroName}' was not found.", steps), lifecycleStopwatch, cancellationToken);
        }

        // Tear down any stale keepalive before terminating; we'll spawn a fresh one
        // after the gateway becomes healthy. Without this, the old keepalive lingers
        // pointing at a now-restarted VM but is no longer tracked by our marker.
        WslDistroKeepAlive.Stop(_options.DistroName, _logger);
        steps.Add("keepalive_stopped");
        _diagnostics.LifecycleStep("repair", "keepalive_stopped", success: true);

        await _wsl.TerminateDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_terminated");
        _diagnostics.LifecycleStep("repair", "distro_terminated", success: true);

        var daemonReload = await RunInDistroAsRootAsync(["systemctl", "daemon-reload"], cancellationToken);
        steps.Add("daemon_reloaded");
        if (!daemonReload.Success)
        {
            _diagnostics.LifecycleStep("repair", "daemon_reloaded", success: false, "daemon_reload_failed", "Failed to reload OpenClaw Gateway systemd units.");
            return await CompleteLifecycleAsync("repair", Fail("daemon_reload_failed", "Failed to reload OpenClaw Gateway systemd units.", steps), lifecycleStopwatch, cancellationToken);
        }
        _diagnostics.LifecycleStep("repair", "daemon_reloaded", success: true);

        var gateway = await RestartGatewayServiceAsync(steps, cancellationToken);
        if (!gateway.Success)
            return await CompleteLifecycleAsync("repair", gateway, lifecycleStopwatch, cancellationToken);

        var health = await _healthProbe.WaitForHealthyAsync(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(_options), cancellationToken);
        steps.Add("gateway_health_checked");
        if (!health.Success)
        {
            _diagnostics.LifecycleStep("repair", "gateway_health_checked", success: false, "gateway_unhealthy", health.Error ?? WslLogsHelp("Gateway did not become healthy after repair."));
            return await CompleteLifecycleAsync("repair", Fail("gateway_unhealthy", health.Error ?? WslLogsHelp("Gateway did not become healthy after repair."), steps), lifecycleStopwatch, cancellationToken);
        }
        _diagnostics.LifecycleStep("repair", "gateway_health_checked", success: true);

        // Re-arm the keepalive so the VM stays up after repair completes, even if the
        // tray that triggered repair exits before the next OnLaunched hook runs.
        WslDistroKeepAlive.EnsureStarted(_options.DistroName, _logger);
        steps.Add("keepalive_started");
        _diagnostics.LifecycleStep("repair", "keepalive_started", success: true);

        return await CompleteLifecycleAsync("repair", new LocalGatewayLifecycleResult(true, Steps: steps), lifecycleStopwatch, cancellationToken);
    }

    public async Task<LocalGatewayLifecycleResult> RemoveAsync(LocalGatewayRemoveRequest request, CancellationToken cancellationToken = default)
    {
        var lifecycleStopwatch = Stopwatch.StartNew();
        _diagnostics.LifecycleStarted("remove");
        var steps = new List<string>();
        if (!request.ConfirmRemove)
        {
            _diagnostics.LifecycleStep("remove", "confirmation_required", success: false, "confirmation_required", "Removing the local OpenClaw Gateway requires explicit confirmation.");
            return await CompleteLifecycleAsync("remove", Fail("confirmation_required", "Removing the local OpenClaw Gateway requires explicit confirmation.", steps), lifecycleStopwatch, cancellationToken);
        }

        // Stop the keepalive before terminating so the marker file does not survive
        // distro removal and confuse a future install with the same name.
        WslDistroKeepAlive.Stop(_options.DistroName, _logger);
        steps.Add("keepalive_stopped");
        _diagnostics.LifecycleStep("remove", "keepalive_stopped", success: true);

        await _wsl.TerminateDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_terminated");
        _diagnostics.LifecycleStep("remove", "distro_terminated", success: true);
        var unregister = await _wsl.UnregisterDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_unregistered");
        if (!unregister.Success)
        {
            _diagnostics.LifecycleStep("remove", "distro_unregistered", success: false, "distro_unregister_failed", $"Failed to unregister WSL distro '{_options.DistroName}'.");
            return await CompleteLifecycleAsync("remove", Fail("distro_unregister_failed", $"Failed to unregister WSL distro '{_options.DistroName}'.", steps), lifecycleStopwatch, cancellationToken);
        }
        _diagnostics.LifecycleStep("remove", "distro_unregistered", success: true);

        if (request.ClearLocalCredentials && _settings is not null)
        {
            _settings.Token = string.Empty;
            _settings.BootstrapToken = string.Empty;
            _settings.EnableNodeMode = false;
            _settings.UseSshTunnel = false;
            _settings.Save();
            steps.Add("local_credentials_cleared");
            _diagnostics.LifecycleStep("remove", "local_credentials_cleared", success: true);
        }

        if (request.PreserveRelayRegistration)
            steps.Add("relay_registration_preserved");

        return await CompleteLifecycleAsync("remove", new LocalGatewayLifecycleResult(true, Steps: steps), lifecycleStopwatch, cancellationToken);
    }

    private async Task<LocalGatewayLifecycleResult> RestartGatewayServiceAsync(List<string> steps, CancellationToken cancellationToken)
    {
        var serviceName = _options.GatewayServiceName;
        var enable = await RunInDistroAsRootAsync(["systemctl", "enable", "--now", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_enabled");
        if (!enable.Success)
        {
            _diagnostics.LifecycleStep("repair", $"{serviceName}_enabled", success: false, "service_enable_failed", $"Failed to enable {serviceName}.service.");
            return Fail("service_enable_failed", $"Failed to enable {serviceName}.service.", steps);
        }
        _diagnostics.LifecycleStep("repair", $"{serviceName}_enabled", success: true);

        var restart = await RunInDistroAsRootAsync(["systemctl", "restart", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_restarted");
        if (!restart.Success)
        {
            _diagnostics.LifecycleStep("repair", $"{serviceName}_restarted", success: false, "service_restart_failed", $"Failed to restart {serviceName}.service.");
            return Fail("service_restart_failed", $"Failed to restart {serviceName}.service.", steps);
        }
        _diagnostics.LifecycleStep("repair", $"{serviceName}_restarted", success: true);

        var active = await RunInDistroAsRootAsync(["systemctl", "is-active", "--quiet", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_active_checked");
        if (!active.Success)
        {
            _diagnostics.LifecycleStep("repair", $"{serviceName}_active_checked", success: false, "service_inactive", $"{serviceName}.service is not active after repair.");
            return Fail("service_inactive", $"{serviceName}.service is not active after repair.", steps);
        }
        _diagnostics.LifecycleStep("repair", $"{serviceName}_active_checked", success: true);

        return new LocalGatewayLifecycleResult(true, Steps: steps);
    }

    private Task<WslCommandResult> RunInDistroAsRootAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-d", _options.DistroName, "-u", "root", "--" };
        args.AddRange(command);
        return _wsl.RunAsync(args, cancellationToken);
    }

    private async Task<LocalGatewayLifecycleResult> CompleteLifecycleAsync(
        string operation,
        LocalGatewayLifecycleResult result,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        _diagnostics.LifecycleCompleted(operation, result, stopwatch.Elapsed);
        await _diagnostics.FlushAsync(TimeSpan.FromSeconds(2), cancellationToken);
        return result;
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
    private static LocalGatewayLifecycleResult Fail(string errorCode, string errorMessage, IReadOnlyList<string> steps) => new(false, errorCode, errorMessage, steps);
}

public static class LocalGatewaySetupEngineFactory
{
    public static LocalGatewaySetupEngine CreateLocalOnly(
        SettingsManager settings,
        IGatewayOperatorConnector operatorConnector,
        IWindowsNodeConnector windowsNodeConnector,
        IOpenClawLogger? logger = null,
#if !OPENCLAW_TRAY_TESTS
        NodeService? nodeService = null,
#endif
        string? distroName = null,
        bool allowExistingDistro = false,
        bool replaceExistingConfigurationConfirmed = false,
        string? identityDataPath = null,
        string? setupStatePath = null,
        OpenClaw.Connection.GatewayRegistry? gatewayRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(operatorConnector);
        ArgumentNullException.ThrowIfNull(windowsNodeConnector);

        // Defense-in-depth fail-closed: refuse to construct the engine if any of the
        // 6 sync existing-config predicates fire and the caller has not passed explicit
        // confirmation. Predicates checked: Token, BootstrapToken, GatewayUrl (non-default),
        // operator DeviceToken, node DeviceToken, and setup-state phase (non-initial).
        // The 7th predicate (WSL distro probe) is intentionally excluded here — the engine
        // factory is a synchronous constructor path, and the WSL distro check is async-only.
        // Forcing it async would cascade to all callers. The page-level gate
        // (LocalSetupProgressPage) performs the full 7-predicate check including the WSL probe.
        // Default is false (safe). Pass true only after the V2 setup warning is confirmed.
        if (!replaceExistingConfigurationConfirmed)
        {
            var resolvedIdentityDataPath = identityDataPath ?? Path.Combine(
                Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray");
            var guard = new OnboardingExistingConfigGuard(settings, resolvedIdentityDataPath, setupStatePath);
            if (guard.HasExistingConfiguration())
            {
                throw new InvalidOperationException(
                    "existing_config_replacement_not_confirmed: " +
                    "Existing OpenClaw configuration detected (token, bootstrap token, " +
                    "gateway URL, device identity, or active setup state). " +
                    "Pass replaceExistingConfigurationConfirmed=true to confirm replacement.");
            }
        }

        var runtime = LocalGatewaySetupRuntimeConfiguration.FromEnvironment();
        var options = new LocalGatewaySetupOptions
        {
            GatewayUrl = settings.GetEffectiveGatewayUrl(),
            DistroName = ResolveDistroName(runtime, distroName),
            AllowExistingDistro = allowExistingDistro || runtime.AllowExistingDistro || replaceExistingConfigurationConfirmed,
#if OPENCLAW_TRAY_TESTS
            EnableWindowsTrayNodeByDefault = settings.EnableNodeMode
#else
            EnableWindowsTrayNodeByDefault = settings.EnableNodeMode || nodeService != null
#endif
        };

        // When replacing existing configuration, clear persisted setup state
        // so the engine starts from scratch instead of replaying a completed run.
        var resolvedStatePath = setupStatePath ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray",
            "setup-state.json");
        if (replaceExistingConfigurationConfirmed && File.Exists(resolvedStatePath))
        {
            try { File.Delete(resolvedStatePath); }
            catch { /* best-effort — engine will overwrite on first save */ }
        }

        var diagnostics = new LocalGatewaySetupDiagnosticsService();
        var wsl = new WslExeCommandRunner(logger, TimeSpan.FromMinutes(30), diagnostics: diagnostics);
        var settingsAdapter = new SettingsManagerLocalGatewaySetupSettings(settings, gatewayRegistry);
        var bootstrapTokenProvider = new WslGatewayCliBootstrapTokenProvider(wsl, options.OpenClawInstallPrefix + "/bin/openclaw");
        var sharedGatewayTokenProvider = new WslGatewayCliSharedGatewayTokenProvider(wsl);
        var gatewayConfigurationPreparer = new OpenClawCliGatewayConfigurationPreparer(wsl);
        var pendingDeviceApprover = new WslGatewayCliPendingDeviceApprover(wsl, options.OpenClawInstallPrefix + "/bin/openclaw");

        // Resolve the pair retry delay from the environment to allow ops
        // override without rebuilding. Falls back to
        // SettingsWindowsTrayNodeProvisioner.DefaultPairRetryDelay (5s) which
        // is conservatively below the gateway's internal ~10s pending-
        // approval timer. Power users on slow ARM64 hardware can raise it;
        // tests want zero. Round-2 review: clamp to a sane upper bound
        // (60s) so a misconfigured env var (extra zero) doesn't hang the
        // wizard for hours with no UI feedback; warn on unparseable values
        // instead of silently ignoring them.
        const int PairRetryDelayUpperBoundMs = 60_000;
        TimeSpan? pairRetryDelay = null;
        var pairRetryDelayMsRaw = Environment.GetEnvironmentVariable("OPENCLAW_PAIR_RETRY_DELAY_MS");
        if (!string.IsNullOrWhiteSpace(pairRetryDelayMsRaw))
        {
            if (int.TryParse(pairRetryDelayMsRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pairRetryDelayMs)
                && pairRetryDelayMs >= 0)
            {
                if (pairRetryDelayMs > PairRetryDelayUpperBoundMs)
                {
                    logger?.Warn($"[Engine] OPENCLAW_PAIR_RETRY_DELAY_MS={pairRetryDelayMs} exceeds the {PairRetryDelayUpperBoundMs}ms ceiling; clamping.");
                    pairRetryDelayMs = PairRetryDelayUpperBoundMs;
                }
                pairRetryDelay = TimeSpan.FromMilliseconds(pairRetryDelayMs);
                logger?.Info($"[Engine] OPENCLAW_PAIR_RETRY_DELAY_MS override: pairRetryDelay={pairRetryDelay}");
            }
            else
            {
                logger?.Warn($"[Engine] OPENCLAW_PAIR_RETRY_DELAY_MS='{pairRetryDelayMsRaw}' is not a valid non-negative integer; ignoring (default 5s applies).");
            }
        }

        return new LocalGatewaySetupEngine(
            options,
            new LocalGatewaySetupStateStore(),
            new LocalGatewayPreflightProbe(wsl),
            wsl,
            new LocalGatewayHealthProbe(),
            new SettingsBootstrapTokenProvisioner(settingsAdapter, bootstrapTokenProvider),
            new SettingsOperatorPairingService(settingsAdapter, operatorConnector, pendingDeviceApprover),
            new SettingsWindowsTrayNodeProvisioner(settingsAdapter, windowsNodeConnector, pendingDeviceApprover, pairRetryDelay),
            logger,
            gatewayConfigurationPreparer: gatewayConfigurationPreparer,
            sharedGatewayTokenProvisioner: new SettingsSharedGatewayTokenProvisioner(settingsAdapter, sharedGatewayTokenProvider, gatewayConfigurationPreparer),
            wslPlatformInstaller: new ElevatedWslPlatformInstaller(new WslPlatformProbe(wsl), logger),
            diagnosticsSink: diagnostics);
    }

    private static string ResolveDistroName(LocalGatewaySetupRuntimeConfiguration runtime, string? explicitDistroName)
    {
#if DEBUG || OPENCLAW_TRAY_TESTS
        // Test/dev seam only: shipping builds are locked to the Craig-approved OpenClawGateway instance name.
        return string.IsNullOrWhiteSpace(explicitDistroName) ? runtime.DistroName ?? "OpenClawGateway" : explicitDistroName;
#else
        return "OpenClawGateway";
#endif
    }
}
