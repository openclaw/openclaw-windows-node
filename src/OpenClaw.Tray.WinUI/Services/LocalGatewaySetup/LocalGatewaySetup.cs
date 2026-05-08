using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    string? InstanceInstallLocation,
    bool AllowExistingDistro)
{
    public const string DistroNameVariable = "OPENCLAW_WSL_DISTRO_NAME";
    public const string InstanceInstallLocationVariable = "OPENCLAW_WSL_INSTALL_LOCATION";
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
            NullIfWhiteSpace(environment.GetVariable(InstanceInstallLocationVariable)),
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

        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<LocalGatewaySetupState>(stream, s_jsonOptions, cancellationToken);
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
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _streamDrainTimeout;

    public WslExeCommandRunner(IOpenClawLogger? logger = null, TimeSpan? defaultTimeout = null, TimeSpan? streamDrainTimeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
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

        _logger.Info($"[WSL] {fileName} {string.Join(" ", arguments.Select(RedactArgument))}");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new WslCommandResult(-1, string.Empty, $"Failed to start wsl.exe: {ex.Message}");
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

        // Drain stdout/stderr with a bounded post-exit timeout. wsl.exe routinely spawns
        // descendants (wslhost.exe, distro init processes) that inherit our redirected
        // pipe handles. Even after wsl.exe itself has exited, ReadToEndAsync can hang
        // indefinitely waiting for EOF — observed as the "checking system" wizard hang
        // during PR #274 smoke testing where the gateway distro held the pipes open for
        // hours. WaitForExitAsync only governs process exit, not stream drain, so we
        // need an explicit drain bound here.
        var stdout = await DrainAsync(stdoutTask, _streamDrainTimeout, _logger, isStderr: false);
        var stderr = await DrainAsync(stderrTask, _streamDrainTimeout, _logger, isStderr: true);

        if (timedOut)
            return new WslCommandResult(-1, stdout, "wsl.exe timed out");

        return new WslCommandResult(process.ExitCode, stdout, stderr);
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

    private static string RedactArgument(string argument) =>
        SecretRedactor.Redact(argument.Contains("token", StringComparison.OrdinalIgnoreCase)
            || argument.Contains("private", StringComparison.OrdinalIgnoreCase)
            || argument.Contains("setupCode", StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : argument);
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

    public LocalGatewayPreflightProbe(IWslCommandRunner wsl, IPortProbe? portProbe = null)
    {
        _wsl = wsl;
        _portProbe = portProbe ?? new TcpPortProbe();
    }

    public async Task<LocalGatewayPreflightResult> RunAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<LocalGatewaySetupIssue>();

        if (!OperatingSystem.IsWindows())
            issues.Add(new LocalGatewaySetupIssue("unsupported_os", "OpenClaw local WSL gateway setup requires Windows.", LocalGatewaySetupSeverity.Blocking));

        if (Environment.Is64BitOperatingSystem is false)
            issues.Add(new LocalGatewaySetupIssue("unsupported_architecture", "OpenClaw local WSL gateway setup requires a 64-bit Windows installation.", LocalGatewaySetupSeverity.Blocking));

        var wslStatus = await _wsl.RunAsync(["--status"], cancellationToken);
        if (!wslStatus.Success)
        {
            issues.Add(new LocalGatewaySetupIssue("wsl_unavailable", WslLogsHelp("WSL is not available or is blocked by policy."), LocalGatewaySetupSeverity.Blocking));
        }
        else
        {
            var status = WslExeCommandRunner.ParseStatus(wslStatus.StandardOutput);
            if (status.DefaultVersion == 1)
                issues.Add(new LocalGatewaySetupIssue("wsl_default_version_1", "The host default WSL version is WSL1. OpenClaw creates its dedicated gateway instance as WSL2.", LocalGatewaySetupSeverity.Warning));
        }

        var distros = await _wsl.ListDistrosAsync(cancellationToken);
        if (!options.AllowExistingDistro && distros.Any(d => string.Equals(d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new LocalGatewaySetupIssue("distro_exists", $"A WSL distro named {options.DistroName} already exists.", LocalGatewaySetupSeverity.Blocking));

        if (distros.Any(d => d.Version == 1))
            issues.Add(new LocalGatewaySetupIssue("wsl1_present", "WSL1 distros are present. OpenClaw uses WSL2 and does not modify existing distros.", LocalGatewaySetupSeverity.Warning));

        if (!_portProbe.IsPortAvailable(options.GatewayPort))
        {
            if (options.AllowExistingDistro && await IsExistingGatewayPortAsync(options, cancellationToken))
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
            return options.AllowExistingDistro
                ? new WslInstanceInstallResult(true, installLocation, ["wsl_instance_already_exists"])
                : new WslInstanceInstallResult(false, installLocation, ErrorCode: "distro_exists", ErrorMessage: $"A WSL distro named {options.DistroName} already exists.");
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
        return new WslInstanceInstallResult(
            false,
            installLocation,
            diagnostics,
            "wsl_instance_install_failed",
            WslLogsHelp("Creating the OpenClaw Gateway WSL instance failed."));
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

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";

    private static string SanitizeForDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = SecretRedactor.Redact(value).Replace("\0", string.Empty).Trim();
        const int maxLength = 2000;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...<truncated>";
    }
}

public sealed record WslInstanceConfigurationResult(
    bool Success,
    IReadOnlyList<string>? Warnings = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

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
            "",
            "[interop]",
            "enabled=false",
            "appendWindowsPath=false",
            "",
            "[user]",
            "default=openclaw",
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
            return new WslInstanceConfigurationResult(
                false,
                ErrorCode: "wsl_firstboot_config_failed",
                ErrorMessage: WslLogsHelp("Failed to configure the OpenClaw WSL instance."));
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
        var script = string.Join("\n", new[]
        {
            "set -euo pipefail",
            "id -u openclaw >/dev/null",
            "test -d /home/openclaw/.openclaw",
            "test -d " + ShellQuote(options.OpenClawInstallPrefix),
            "grep -q '^systemd=true$' /etc/wsl.conf",
            "grep -q '^enabled=false$' /etc/wsl.conf",
            "grep -q '^appendWindowsPath=false$' /etc/wsl.conf",
            "grep -q '^default=openclaw$' /etc/wsl.conf"
        });

        var probe = await _wsl.RunAsync(["-d", options.DistroName, "-u", "root", "--", "bash", "-lc", script], cancellationToken);
        return probe.Success;
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
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

        return new OpenClawLinuxInstallResult(true, events);
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
        var start = await RunOpenClawAsync(options, ["gateway", "start"], cancellationToken);
        if (!start.Success)
            return new GatewayServiceOperationResult(false, "gateway_service_start_failed", WslLogsHelp("Failed to start the upstream OpenClaw gateway service."));

        WslCommandResult? lastStatus = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStatus = await RunStatusWithTokenAsync(options, cancellationToken);
            if (lastStatus.Success)
                return new GatewayServiceOperationResult(true);

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return new GatewayServiceOperationResult(
            false,
            "gateway_service_status_failed",
            WslLogsHelp("The OpenClaw gateway service started, but did not report ready status."),
            lastStatus is null ? null : DiagnosticFormatter.Build("gateway_service_status", lastStatus));
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

        var sanitized = SecretRedactor.Redact(value).Replace("\0", string.Empty).Trim();
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

    public SettingsManagerLocalGatewaySetupSettings(SettingsManager settings)
    {
        _settings = settings;
    }

    public string GatewayUrl { get => _settings.GatewayUrl; set => _settings.GatewayUrl = value; }
    public string Token { get => _settings.Token; set => _settings.Token = value; }
    public string BootstrapToken { get => _settings.BootstrapToken; set => _settings.BootstrapToken = value; }
    public bool UseSshTunnel { get => _settings.UseSshTunnel; set => _settings.UseSshTunnel = value; }
    public bool EnableNodeMode { get => _settings.EnableNodeMode; set => _settings.EnableNodeMode = value; }
    public void Save() => _settings.Save();
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

#if !OPENCLAW_TRAY_TESTS
public sealed class NodeServiceWindowsNodeConnector : IWindowsNodeConnector
{
    private readonly NodeService _nodeService;

    public NodeServiceWindowsNodeConnector(NodeService nodeService)
    {
        _nodeService = nodeService;
    }

    public async Task ConnectAsync(string gatewayUrl, string token, string? bootstrapToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _nodeService.ConnectAsync(gatewayUrl, token, bootstrapToken);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_nodeService.IsConnected && _nodeService.IsPaired)
                return;

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway.");
    }
}
#endif

public static class WslDistroKeepAlive
{
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, Process> s_processes = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_processExitRegistered;

    public static void EnsureStarted(string distroName, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(distroName))
            return;

        lock (s_lock)
        {
            if (s_processes.TryGetValue(distroName, out var existing) && !existing.HasExited)
                return;

            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-d", distroName, "-u", "openclaw", "--", "sleep", "2147483647" }
                });

                if (process == null)
                {
                    logger?.Warn("Failed to start WSL keepalive process.");
                    return;
                }

                s_processes[distroName] = process;
                logger?.Info($"Started WSL keepalive process for {distroName} (PID {process.Id}).");

                if (!s_processExitRegistered)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();
                    s_processExitRegistered = true;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to start WSL keepalive process: {ex.Message}");
            }
        }
    }

    private static void StopAll()
    {
        lock (s_lock)
        {
            foreach (var process in s_processes.Values)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process exit cleanup is best-effort only.
                }
            }

            s_processes.Clear();
        }
    }
}

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

public sealed class OpenClawGatewayOperatorConnector : IGatewayOperatorConnector
{
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _timeout;

    public OpenClawGatewayOperatorConnector(IOpenClawLogger? logger = null, TimeSpan? timeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _timeout = timeout ?? TimeSpan.FromSeconds(35);
    }

    public async Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default)
    {
        using var client = new OpenClawGatewayClient(gatewayUrl, token, _logger, tokenIsBootstrapToken, bootstrapPairAsNode: false);
        var completion = new TaskCompletionSource<GatewayOperatorConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.StatusChanged += (_, status) =>
        {
            if (status == ConnectionStatus.Connected)
                completion.TrySetResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
            else if (status == ConnectionStatus.Error)
            {
                if (client.IsPairingRequired)
                    completion.TrySetResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "Gateway requires pairing approval.", client.PairingRequiredRequestId));
                else if (client.IsAuthFailed)
                    completion.TrySetResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.AuthFailed, "Gateway rejected operator authentication."));
            }
        };

        try
        {
            await client.ConnectAsync();
            var completed = await Task.WhenAny(completion.Task, Task.Delay(_timeout, cancellationToken));
            return completed == completion.Task
                ? await completion.Task
                : new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Timeout, "Timed out waiting for operator handshake.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Failed, ex.Message);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    public async Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default)
    {
        var dataPath = Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray");
        var identity = new DeviceIdentity(dataPath, _logger);
        identity.Initialize();

        if (string.IsNullOrWhiteSpace(identity.DeviceToken))
            return new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.AuthFailed, "Gateway did not return a stored device token after bootstrap pairing.");

        return await ConnectAsync(gatewayUrl, identity.DeviceToken, tokenIsBootstrapToken: false, cancellationToken);
    }
}

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
        return new Stage1Outcome(second, FirstResult: first);
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
    private readonly ILocalGatewaySetupSettings _settings;
    private readonly IWindowsNodeConnector? _connector;
    private readonly IPendingDeviceApprover? _pendingApprover;

    public SettingsWindowsTrayNodeProvisioner(SettingsManager settings, IWindowsNodeConnector? connector = null, IPendingDeviceApprover? pendingApprover = null)
        : this(new SettingsManagerLocalGatewaySetupSettings(settings), connector, pendingApprover)
    {
    }

    public SettingsWindowsTrayNodeProvisioner(ILocalGatewaySetupSettings settings, IWindowsNodeConnector? connector = null, IPendingDeviceApprover? pendingApprover = null)
    {
        _settings = settings;
        _connector = connector;
        _pendingApprover = pendingApprover;
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
                    var approval = await _pendingApprover.ApproveLatestAsync(state, cancellationToken);
                    if (!approval.Success)
                    {
                        return new ProvisioningResult(
                            false,
                            approval.ErrorCode ?? "windows_node_pending_approval_failed",
                            approval.ErrorMessage ?? "Local gateway pending role-upgrade approval failed.");
                    }

                    try
                    {
                        await _connector.ConnectAsync(state.GatewayUrl, _settings.Token, _settings.BootstrapToken, cancellationToken);
                    }
                    catch (Exception retryEx) when (retryEx is not OperationCanceledException)
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
    private readonly IOpenClawLogger _logger;

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
        ISharedGatewayTokenProvisioner? sharedGatewayTokenProvisioner = null)
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
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<LocalGatewaySetupState> RunLocalOnlyAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken) ?? LocalGatewaySetupState.Create(_options);
        state.DistroName = _options.DistroName;
        state.GatewayUrl = LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(_options);
        var distroExists = await HasDistroAsync(cancellationToken);
        var resumingAfterInstanceStarted = IsCreateOrLater(state.Phase) && distroExists;
        var preflightOptions = _options with { AllowExistingDistro = _options.AllowExistingDistro || resumingAfterInstanceStarted };

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

        await RunPhaseAsync(state, LocalGatewaySetupPhase.EnsureWslEnabled, "Checking WSL support", () => Task.FromResult(state.Status == LocalGatewaySetupStatus.Running), cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.CreateWslInstance, "Creating OpenClaw Gateway WSL instance", async () =>
        {
            var installOptions = _options with { AllowExistingDistro = _options.AllowExistingDistro || resumingAfterInstanceStarted };
            var result = await _wslInstanceInstaller.EnsureInstalledAsync(installOptions, cancellationToken);
            if (!result.Success)
            {
                var detail = string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(detail))
                    _logger.Warn($"WSL instance install diagnostics: {SecretRedactor.Redact(detail)}");
                state.Block(result.ErrorCode ?? "wsl_instance_install_failed", result.ErrorMessage ?? "Failed to create the OpenClaw Gateway WSL instance.", retryable: true, detail: detail);
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
                state.Block(result.ErrorCode ?? "wsl_instance_config_failed", result.ErrorMessage ?? "Failed to configure the OpenClaw Gateway WSL instance.", retryable: true);
                return false;
            }

            foreach (var warning in result.Warnings ?? Array.Empty<string>())
                _logger.Warn($"WSL instance configuration warning: {SecretRedactor.Redact(warning)}");

            return true;
        }, cancellationToken);

        await RunPhaseAsync(state, LocalGatewaySetupPhase.InstallOpenClawCli, "Installing OpenClaw inside WSL", async () =>
        {
            var result = await _openClawLinuxInstaller.InstallAsync(_options, cancellationToken);
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

        return state;
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

        state.StartPhase(phase, message);
        await SaveAndPublishAsync(state, cancellationToken);
        bool completed;
        try
        {
            completed = await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Local gateway setup phase {phase} failed.", ex);
            state.Block($"{phase.ToString().ToLowerInvariant()}_failed", ex.Message, retryable: true, detail: SecretRedactor.Redact(ex.ToString()));
            await SaveAndPublishAsync(state, cancellationToken);
            return;
        }

        if (completed && state.Status == LocalGatewaySetupStatus.Running)
        {
            state.CompletePhase(phase, message);
            await SaveAndPublishAsync(state, cancellationToken);
        }
        else if (!completed)
        {
            await SaveAndPublishAsync(state, cancellationToken);
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

    public LocalGatewayLifecycleManager(LocalGatewaySetupOptions options, IWslCommandRunner wsl, ILocalGatewayHealthProbe healthProbe, ILocalGatewaySetupSettings? settings = null)
    {
        _options = options;
        _wsl = wsl;
        _healthProbe = healthProbe;
        _settings = settings;
    }

    public async Task<LocalGatewayLifecycleResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var distros = await _wsl.ListDistrosAsync(cancellationToken);
        if (!distros.Any(d => d.Name.Equals(_options.DistroName, StringComparison.OrdinalIgnoreCase) && d.Version == 2))
            return Fail("distro_missing", $"The OpenClaw WSL distro '{_options.DistroName}' was not found.", steps);

        await _wsl.TerminateDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_terminated");

        var daemonReload = await RunInDistroAsRootAsync(["systemctl", "daemon-reload"], cancellationToken);
        steps.Add("daemon_reloaded");
        if (!daemonReload.Success)
            return Fail("daemon_reload_failed", "Failed to reload OpenClaw Gateway systemd units.", steps);

        var gateway = await RestartGatewayServiceAsync(steps, cancellationToken);
        if (!gateway.Success)
            return gateway;

        var health = await _healthProbe.WaitForHealthyAsync(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(_options), cancellationToken);
        steps.Add("gateway_health_checked");
        if (!health.Success)
            return Fail("gateway_unhealthy", health.Error ?? WslLogsHelp("Gateway did not become healthy after repair."), steps);

        return new LocalGatewayLifecycleResult(true, Steps: steps);
    }

    public async Task<LocalGatewayLifecycleResult> RemoveAsync(LocalGatewayRemoveRequest request, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        if (!request.ConfirmRemove)
            return Fail("confirmation_required", "Removing the local OpenClaw Gateway requires explicit confirmation.", steps);

        await _wsl.TerminateDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_terminated");
        var unregister = await _wsl.UnregisterDistroAsync(_options.DistroName, cancellationToken);
        steps.Add("distro_unregistered");
        if (!unregister.Success)
            return Fail("distro_unregister_failed", $"Failed to unregister WSL distro '{_options.DistroName}'.", steps);

        if (request.ClearLocalCredentials && _settings is not null)
        {
            _settings.Token = string.Empty;
            _settings.BootstrapToken = string.Empty;
            _settings.EnableNodeMode = false;
            _settings.UseSshTunnel = false;
            _settings.Save();
            steps.Add("local_credentials_cleared");
        }

        if (request.PreserveRelayRegistration)
            steps.Add("relay_registration_preserved");

        return new LocalGatewayLifecycleResult(true, Steps: steps);
    }

    private async Task<LocalGatewayLifecycleResult> RestartGatewayServiceAsync(List<string> steps, CancellationToken cancellationToken)
    {
        var serviceName = _options.GatewayServiceName;
        var enable = await RunInDistroAsRootAsync(["systemctl", "enable", "--now", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_enabled");
        if (!enable.Success)
            return Fail("service_enable_failed", $"Failed to enable {serviceName}.service.", steps);

        var restart = await RunInDistroAsRootAsync(["systemctl", "restart", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_restarted");
        if (!restart.Success)
            return Fail("service_restart_failed", $"Failed to restart {serviceName}.service.", steps);

        var active = await RunInDistroAsRootAsync(["systemctl", "is-active", "--quiet", $"{serviceName}.service"], cancellationToken);
        steps.Add($"{serviceName}_active_checked");
        if (!active.Success)
            return Fail("service_inactive", $"{serviceName}.service is not active after repair.", steps);

        return new LocalGatewayLifecycleResult(true, Steps: steps);
    }

    private Task<WslCommandResult> RunInDistroAsRootAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-d", _options.DistroName, "-u", "root", "--" };
        args.AddRange(command);
        return _wsl.RunAsync(args, cancellationToken);
    }

    private static string WslLogsHelp(string message) => message + " Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
    private static LocalGatewayLifecycleResult Fail(string errorCode, string errorMessage, IReadOnlyList<string> steps) => new(false, errorCode, errorMessage, steps);
}

public static class LocalGatewaySetupEngineFactory
{
    public static LocalGatewaySetupEngine CreateLocalOnly(
        SettingsManager settings,
        IOpenClawLogger? logger = null,
#if !OPENCLAW_TRAY_TESTS
        NodeService? nodeService = null,
#endif
        string? distroName = null,
        string? instanceInstallLocation = null,
        bool allowExistingDistro = false,
        bool replaceExistingConfigurationConfirmed = false,
        string? identityDataPath = null,
        string? setupStatePath = null)
    {
        // Defense-in-depth fail-closed: refuse to construct the engine if any of the
        // 6 sync existing-config predicates fire and the caller has not passed explicit
        // confirmation. Predicates checked: Token, BootstrapToken, GatewayUrl (non-default),
        // operator DeviceToken, node DeviceToken, and setup-state phase (non-initial).
        // The 7th predicate (WSL distro probe) is intentionally excluded here — the engine
        // factory is a synchronous constructor path, and the WSL distro check is async-only.
        // Forcing it async would cascade to all callers. The page-level gate
        // (LocalSetupProgressPage) performs the full 7-predicate check including the WSL probe.
        // Default is false (safe). Pass true only from the confirmed SetupWarningPage flow.
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
            InstanceInstallLocation = string.IsNullOrWhiteSpace(instanceInstallLocation) ? runtime.InstanceInstallLocation : instanceInstallLocation,
            AllowExistingDistro = allowExistingDistro || runtime.AllowExistingDistro,
#if OPENCLAW_TRAY_TESTS
            EnableWindowsTrayNodeByDefault = settings.EnableNodeMode
#else
            EnableWindowsTrayNodeByDefault = settings.EnableNodeMode || nodeService != null
#endif
        };

        var wsl = new WslExeCommandRunner(logger, TimeSpan.FromMinutes(30));
        var settingsAdapter = new SettingsManagerLocalGatewaySetupSettings(settings);
        var operatorConnector = new OpenClawGatewayOperatorConnector(logger);
        var bootstrapTokenProvider = new WslGatewayCliBootstrapTokenProvider(wsl, options.OpenClawInstallPrefix + "/bin/openclaw");
        var sharedGatewayTokenProvider = new WslGatewayCliSharedGatewayTokenProvider(wsl);
        var gatewayConfigurationPreparer = new OpenClawCliGatewayConfigurationPreparer(wsl);
        var pendingDeviceApprover = new WslGatewayCliPendingDeviceApprover(wsl, options.OpenClawInstallPrefix + "/bin/openclaw");
#if OPENCLAW_TRAY_TESTS
        IWindowsNodeConnector? windowsNodeConnector = null;
#else
        IWindowsNodeConnector? windowsNodeConnector = nodeService == null ? null : new NodeServiceWindowsNodeConnector(nodeService);
#endif

        return new LocalGatewaySetupEngine(
            options,
            new LocalGatewaySetupStateStore(),
            new LocalGatewayPreflightProbe(wsl),
            wsl,
            new LocalGatewayHealthProbe(),
            new SettingsBootstrapTokenProvisioner(settingsAdapter, bootstrapTokenProvider),
            new SettingsOperatorPairingService(settingsAdapter, operatorConnector, pendingDeviceApprover),
            new SettingsWindowsTrayNodeProvisioner(settingsAdapter, windowsNodeConnector, pendingDeviceApprover),
            logger,
            gatewayConfigurationPreparer: gatewayConfigurationPreparer,
            sharedGatewayTokenProvisioner: new SettingsSharedGatewayTokenProvisioner(settingsAdapter, sharedGatewayTokenProvider, gatewayConfigurationPreparer));
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
