using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClawTray.Services.LocalGatewaySetup;

/// <summary>
/// Schema v1 for easy-button setup diagnostics.
///
/// Required top-level JSONL fields:
/// schema_version, timestamp_utc, run_id, install_id, level, event.
///
/// Optional top-level JSONL fields:
/// phase, visible_stage, status, message, failure_code, retryable,
/// duration_ms, details.
/// </summary>
public interface ILocalGatewaySetupDiagnosticsSink
{
    string? RunTracePath { get; }
    string? LatestTracePath { get; }
    string? LatestSummaryPath { get; }

    void RunStarted(LocalGatewaySetupState state, LocalGatewaySetupOptions options);
    void RunCompleted(LocalGatewaySetupState state, TimeSpan duration);
    void PhaseStarted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message);
    void PhaseCompleted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message, TimeSpan duration);
    string CommandStarted(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
    void CommandCompleted(string commandId, string fileName, IReadOnlyList<string> arguments, TimeSpan duration, WslCommandResult result, bool timedOut);
    void InstallerEvent(LocalGatewaySetupPhase phase, OpenClawLinuxInstallerEvent installerEvent);
    void LifecycleStarted(string operation);
    void LifecycleStep(string operation, string step, bool success, string? errorCode = null, string? errorMessage = null);
    void LifecycleCompleted(string operation, LocalGatewayLifecycleResult result, TimeSpan duration);
    Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class NullLocalGatewaySetupDiagnosticsSink : ILocalGatewaySetupDiagnosticsSink
{
    public static readonly NullLocalGatewaySetupDiagnosticsSink Instance = new();

    public string? RunTracePath => null;
    public string? LatestTracePath => null;
    public string? LatestSummaryPath => null;

    public void RunStarted(LocalGatewaySetupState state, LocalGatewaySetupOptions options) { }
    public void RunCompleted(LocalGatewaySetupState state, TimeSpan duration) { }
    public void PhaseStarted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message) { }
    public void PhaseCompleted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message, TimeSpan duration) { }
    public string CommandStarted(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) => string.Empty;
    public void CommandCompleted(string commandId, string fileName, IReadOnlyList<string> arguments, TimeSpan duration, WslCommandResult result, bool timedOut) { }
    public void InstallerEvent(LocalGatewaySetupPhase phase, OpenClawLinuxInstallerEvent installerEvent) { }
    public void LifecycleStarted(string operation) { }
    public void LifecycleStep(string operation, string step, bool success, string? errorCode = null, string? errorMessage = null) { }
    public void LifecycleCompleted(string operation, LocalGatewayLifecycleResult result, TimeSpan duration) { }
    public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class LocalGatewaySetupDiagnosticsService : ILocalGatewaySetupDiagnosticsSink
{
    public const int SchemaVersion = 1;
    private const int MaxCommandOutputChars = 4096;
    private const int MaxStoredRecords = 512;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly object _lock = new();
    private readonly string _setupLogDirectory;
    private readonly List<SetupDiagnosticRecord> _records = new();
    private string? _runId;
    private string? _installId;
    private bool _initialized;

    public LocalGatewaySetupDiagnosticsService(string? localDataPath = null)
    {
        _setupLogDirectory = Path.Combine(localDataPath ?? ResolveLocalDataPath(), "Logs", "Setup");
        LatestTracePath = Path.Combine(_setupLogDirectory, "easy-setup-latest.jsonl");
        LatestSummaryPath = Path.Combine(_setupLogDirectory, "easy-setup-latest.txt");
    }

    public string? RunTracePath { get; private set; }
    public string? LatestTracePath { get; }
    public string? LatestSummaryPath { get; }

    public static string ResolveLocalDataPath()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } dataOverride)
            return dataOverride;

        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR") is { Length: > 0 } localDataOverride)
            return Path.Combine(localDataOverride, "OpenClawTray");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
    }

    public static string LatestSummaryPathForCurrentUser() =>
        Path.Combine(ResolveLocalDataPath(), "Logs", "Setup", "easy-setup-latest.txt");

    public static string SetupStatePathForCurrentUser() =>
        Path.Combine(ResolveLocalDataPath(), "setup-state.json");

    public void RunStarted(LocalGatewaySetupState state, LocalGatewaySetupOptions options)
    {
        EnsureRunInitialized(state);
        Write(new SetupDiagnosticRecord(
            Event: "run_started",
            Level: "info",
            RunId: state.RunId,
            InstallId: state.InstallId,
            Status: state.Status.ToString(),
            Message: "Local easy-button setup started.",
            Details: new Dictionary<string, object?>
            {
                ["distro_name"] = options.DistroName,
                ["gateway_url"] = SetupDiagnosticsRedactor.SanitizeText(LocalGatewayEndpointResolver.BuildLoopbackGatewayUrl(options)),
                ["gateway_port"] = options.GatewayPort,
                ["openclaw_install_version"] = options.OpenClawInstallVersion,
                ["allow_existing_distro"] = options.AllowExistingDistro,
                ["enable_windows_tray_node"] = options.EnableWindowsTrayNodeByDefault,
                ["os_version"] = Environment.OSVersion.VersionString,
                ["process_architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ["os_architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                ["dotnet_runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["is_64_bit_os"] = Environment.Is64BitOperatingSystem,
                ["is_elevated"] = IsElevated(),
                ["setup_state_path"] = SetupStatePathForCurrentUser(),
                ["tray_log_path"] = Logger.LogFilePath
            }));
    }

    public void RunCompleted(LocalGatewaySetupState state, TimeSpan duration)
    {
        EnsureRunInitialized(state);
        var failed = state.Status is LocalGatewaySetupStatus.FailedRetryable
            or LocalGatewaySetupStatus.FailedTerminal
            or LocalGatewaySetupStatus.Blocked;
        Write(new SetupDiagnosticRecord(
            Event: failed ? "run_failed" : "run_completed",
            Level: failed ? "error" : "info",
            RunId: state.RunId,
            InstallId: state.InstallId,
            Phase: state.Phase.ToString(),
            VisibleStage: GetVisibleStage(state.Phase),
            Status: state.Status.ToString(),
            Message: state.UserMessage,
            FailureCode: state.FailureCode,
            Retryable: state.Status == LocalGatewaySetupStatus.FailedRetryable,
            DurationMs: duration.TotalMilliseconds,
            Details: BuildStateDetails(state)));
        WriteSummary(state, duration);
    }

    public void PhaseStarted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message)
    {
        EnsureRunInitialized(state);
        Write(new SetupDiagnosticRecord(
            Event: "phase_started",
            Level: "info",
            RunId: state.RunId,
            InstallId: state.InstallId,
            Phase: phase.ToString(),
            VisibleStage: GetVisibleStage(phase),
            Status: state.Status.ToString(),
            Message: message));
    }

    public void PhaseCompleted(LocalGatewaySetupState state, LocalGatewaySetupPhase phase, string message, TimeSpan duration)
    {
        EnsureRunInitialized(state);
        var failed = state.Status is LocalGatewaySetupStatus.FailedRetryable
            or LocalGatewaySetupStatus.FailedTerminal
            or LocalGatewaySetupStatus.Blocked
            or LocalGatewaySetupStatus.Cancelled;
        Write(new SetupDiagnosticRecord(
            Event: failed ? "phase_failed" : "phase_succeeded",
            Level: failed ? "error" : "info",
            RunId: state.RunId,
            InstallId: state.InstallId,
            Phase: phase.ToString(),
            VisibleStage: GetVisibleStage(phase),
            Status: state.Status.ToString(),
            Message: failed ? state.UserMessage : message,
            FailureCode: state.FailureCode,
            Retryable: state.Status == LocalGatewaySetupStatus.FailedRetryable,
            DurationMs: duration.TotalMilliseconds,
            Details: failed ? BuildStateDetails(state) : null));

        if (failed)
            WriteSummary(state, duration);
    }

    public string CommandStarted(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var commandId = Guid.NewGuid().ToString("N")[..12];
        Write(new SetupDiagnosticRecord(
            Event: "command_started",
            Level: "debug",
            RunId: _runId,
            InstallId: _installId,
            Message: fileName,
            Details: new Dictionary<string, object?>
            {
                ["command_id"] = commandId,
                ["file_name"] = fileName,
                ["arguments"] = SetupDiagnosticsRedactor.RedactArguments(arguments),
                ["timeout_ms"] = timeout.TotalMilliseconds
            }));
        return commandId;
    }

    public void CommandCompleted(string commandId, string fileName, IReadOnlyList<string> arguments, TimeSpan duration, WslCommandResult result, bool timedOut)
    {
        var stdout = SetupDiagnosticsRedactor.SanitizeCommandOutput(result.StandardOutput, MaxCommandOutputChars, out var stdoutTruncated);
        var stderr = SetupDiagnosticsRedactor.SanitizeCommandOutput(result.StandardError, MaxCommandOutputChars, out var stderrTruncated);
        Write(new SetupDiagnosticRecord(
            Event: result.Success && !timedOut ? "command_succeeded" : "command_failed",
            Level: result.Success && !timedOut ? "debug" : "warn",
            RunId: _runId,
            InstallId: _installId,
            Message: fileName,
            DurationMs: duration.TotalMilliseconds,
            Details: new Dictionary<string, object?>
            {
                ["command_id"] = commandId,
                ["file_name"] = fileName,
                ["arguments"] = SetupDiagnosticsRedactor.RedactArguments(arguments),
                ["exit_code"] = result.ExitCode,
                ["timed_out"] = timedOut,
                ["stdout"] = stdout,
                ["stdout_truncated"] = stdoutTruncated,
                ["stderr"] = stderr,
                ["stderr_truncated"] = stderrTruncated
            }));
    }

    public void InstallerEvent(LocalGatewaySetupPhase phase, OpenClawLinuxInstallerEvent installerEvent)
    {
        Write(new SetupDiagnosticRecord(
            Event: "installer_event",
            Level: "info",
            RunId: _runId,
            InstallId: _installId,
            Phase: phase.ToString(),
            VisibleStage: GetVisibleStage(phase),
            Message: SetupDiagnosticsRedactor.SanitizeText(installerEvent.Message ?? installerEvent.RawLine),
            Details: new Dictionary<string, object?>
            {
                ["installer_event"] = SetupDiagnosticsRedactor.SanitizeText(installerEvent.Event),
                ["installer_phase"] = SetupDiagnosticsRedactor.SanitizeText(installerEvent.Phase),
                ["raw_line"] = SetupDiagnosticsRedactor.SanitizeText(installerEvent.RawLine)
            }));
    }

    public void LifecycleStarted(string operation)
    {
        EnsureLifecycleInitialized(operation);
        Write(new SetupDiagnosticRecord(
            Event: "lifecycle_started",
            Level: "info",
            RunId: _runId,
            InstallId: _installId,
            Message: operation));
    }

    public void LifecycleStep(string operation, string step, bool success, string? errorCode = null, string? errorMessage = null)
    {
        Write(new SetupDiagnosticRecord(
            Event: success ? "lifecycle_step_succeeded" : "lifecycle_step_failed",
            Level: success ? "info" : "error",
            RunId: _runId,
            InstallId: _installId,
            Message: step,
            FailureCode: errorCode,
            Details: new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["step"] = step,
                ["error_message"] = SetupDiagnosticsRedactor.SanitizeText(errorMessage)
            }));
    }

    public void LifecycleCompleted(string operation, LocalGatewayLifecycleResult result, TimeSpan duration)
    {
        Write(new SetupDiagnosticRecord(
            Event: result.Success ? "lifecycle_completed" : "lifecycle_failed",
            Level: result.Success ? "info" : "error",
            RunId: _runId,
            InstallId: _installId,
            Message: operation,
            FailureCode: result.ErrorCode,
            Retryable: !result.Success,
            DurationMs: duration.TotalMilliseconds,
            Details: new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["error_message"] = SetupDiagnosticsRedactor.SanitizeText(result.ErrorMessage),
                ["steps"] = result.Steps ?? Array.Empty<string>()
            }));
        WriteLifecycleSummary(operation, result, duration);
    }

    public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void EnsureRunInitialized(LocalGatewaySetupState state)
    {
        lock (_lock)
        {
            if (_initialized
                && string.Equals(_runId, state.RunId, StringComparison.Ordinal)
                && string.Equals(_installId, state.InstallId, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(_setupLogDirectory);
            _runId = state.RunId;
            _installId = state.InstallId;
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var shortRunId = string.IsNullOrWhiteSpace(state.RunId)
                ? Guid.NewGuid().ToString("N")[..12]
                : state.RunId[..Math.Min(12, state.RunId.Length)];
            RunTracePath = Path.Combine(_setupLogDirectory, $"setup-{timestamp}-{shortRunId}.jsonl");
            SafeDelete(LatestTracePath);
            SafeDelete(LatestSummaryPath);
            _records.Clear();
            _initialized = true;
        }
    }

    private void EnsureLifecycleInitialized(string operation)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_setupLogDirectory);
            _runId = Guid.NewGuid().ToString("N");
            _installId = null;
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var operationSlug = string.IsNullOrWhiteSpace(operation)
                ? "lifecycle"
                : string.Concat(operation.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(operationSlug))
                operationSlug = "lifecycle";
            RunTracePath = Path.Combine(_setupLogDirectory, $"setup-{timestamp}-{operationSlug}-{_runId[..12]}.jsonl");
            SafeDelete(LatestTracePath);
            SafeDelete(LatestSummaryPath);
            _records.Clear();
            _initialized = true;
        }
    }

    private void Write(SetupDiagnosticRecord record)
    {
        lock (_lock)
        {
            if (RunTracePath is null || LatestTracePath is null)
                return;

            var sanitized = record.Sanitized();
            _records.Add(sanitized);
            if (_records.Count > MaxStoredRecords)
                _records.RemoveAt(0);

            var line = SetupDiagnosticsRedactor.SanitizeText(JsonSerializer.Serialize(sanitized, s_jsonOptions)) ?? "{}";
            File.AppendAllText(RunTracePath, line + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(LatestTracePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private void WriteSummary(LocalGatewaySetupState state, TimeSpan duration)
    {
        lock (_lock)
        {
            if (LatestSummaryPath is null)
                return;

            Directory.CreateDirectory(_setupLogDirectory);
            File.WriteAllText(LatestSummaryPath, BuildSummary(state, duration), Encoding.UTF8);
        }
    }

    private void WriteLifecycleSummary(string operation, LocalGatewayLifecycleResult result, TimeSpan duration)
    {
        lock (_lock)
        {
            if (LatestSummaryPath is null)
                return;

            Directory.CreateDirectory(_setupLogDirectory);
            File.WriteAllText(LatestSummaryPath, BuildLifecycleSummary(operation, result, duration), Encoding.UTF8);
        }
    }

    private string BuildSummary(LocalGatewaySetupState state, TimeSpan duration)
    {
        var failed = state.Status is LocalGatewaySetupStatus.FailedRetryable
            or LocalGatewaySetupStatus.FailedTerminal
            or LocalGatewaySetupStatus.Blocked;
        var sb = new StringBuilder();
        sb.AppendLine("OpenClaw easy setup diagnostics");
        sb.AppendLine($"Outcome: {(failed ? "FAILED" : state.Status.ToString().ToUpperInvariant())}");
        if (failed)
        {
            sb.AppendLine($"Failed phase: {LastRunningPhase(state)}");
            if (!string.IsNullOrWhiteSpace(state.FailureCode))
                sb.AppendLine($"Failure code: {SetupDiagnosticsRedactor.SanitizeText(state.FailureCode)}");
            if (!string.IsNullOrWhiteSpace(state.UserMessage))
                sb.AppendLine($"Message: {SetupDiagnosticsRedactor.SanitizeText(state.UserMessage)}");
            sb.AppendLine($"Retryable: {state.Status == LocalGatewaySetupStatus.FailedRetryable}");
        }
        sb.AppendLine($"Run ID: {state.RunId}");
        sb.AppendLine($"Install ID: {state.InstallId}");
        sb.AppendLine($"Updated UTC: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Duration: {duration.TotalSeconds:F1}s");
        sb.AppendLine($"Summary: {LatestSummaryPath}");
        sb.AppendLine($"JSONL trace: {LatestTracePath}");
        sb.AppendLine($"Per-run JSONL trace: {RunTracePath}");
        sb.AppendLine($"Tray log: {Logger.LogFilePath}");
        sb.AppendLine($"Setup state: {SetupStatePathForCurrentUser()}");
        sb.AppendLine();
        sb.AppendLine("Phase timeline:");
        foreach (var record in _records.Where(r => r.Event is "phase_succeeded" or "phase_failed"))
        {
            var mark = record.Event == "phase_succeeded" ? "OK" : "FAILED";
            var phase = record.Phase ?? "(unknown)";
            var visible = string.IsNullOrWhiteSpace(record.VisibleStage) ? "" : $" [{record.VisibleStage}]";
            var ms = record.DurationMs is null ? "" : $" {record.DurationMs.Value:F0}ms";
            sb.AppendLine($"- {mark} {phase}{visible}{ms} - {SetupDiagnosticsRedactor.SanitizeText(record.Message)}");
        }
        if (failed)
        {
            sb.AppendLine();
            sb.AppendLine("Next actions:");
            foreach (var action in BuildNextActions(state))
                sb.AppendLine($"- {action}");
        }
        return sb.ToString();
    }

    private string BuildLifecycleSummary(string operation, LocalGatewayLifecycleResult result, TimeSpan duration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OpenClaw easy setup diagnostics");
        sb.AppendLine($"Outcome: {(result.Success ? "COMPLETE" : "FAILED")}");
        sb.AppendLine($"Gateway lifecycle operation: {SetupDiagnosticsRedactor.SanitizeText(operation)}");
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorCode))
                sb.AppendLine($"Failure code: {SetupDiagnosticsRedactor.SanitizeText(result.ErrorCode)}");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                sb.AppendLine($"Message: {SetupDiagnosticsRedactor.SanitizeText(result.ErrorMessage)}");
        }
        sb.AppendLine($"Run ID: {_runId}");
        sb.AppendLine($"Updated UTC: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Duration: {duration.TotalSeconds:F1}s");
        sb.AppendLine($"Summary: {LatestSummaryPath}");
        sb.AppendLine($"JSONL trace: {LatestTracePath}");
        sb.AppendLine($"Per-run JSONL trace: {RunTracePath}");
        sb.AppendLine($"Tray log: {Logger.LogFilePath}");
        sb.AppendLine();
        sb.AppendLine("Lifecycle timeline:");
        foreach (var record in _records.Where(r => r.Event is "lifecycle_step_succeeded" or "lifecycle_step_failed"))
        {
            var mark = record.Event == "lifecycle_step_succeeded" ? "OK" : "FAILED";
            sb.AppendLine($"- {mark} {SetupDiagnosticsRedactor.SanitizeText(record.Message)}");
        }
        if (!result.Success)
        {
            sb.AppendLine();
            sb.AppendLine("Next actions:");
            sb.AppendLine($"- Open setup JSONL trace: {LatestTracePath}");
            sb.AppendLine($"- Open tray log: {Logger.LogFilePath}");
            if (string.Join(" ", result.ErrorCode, result.ErrorMessage).Contains("wsl", StringComparison.OrdinalIgnoreCase)
                || string.Join(" ", result.ErrorCode, result.ErrorMessage).Contains("gateway", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("- If WSL diagnostics are needed, follow aka.ms/wsllogs.");
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, object?> BuildStateDetails(LocalGatewaySetupState state)
    {
        return new Dictionary<string, object?>
        {
            ["issues"] = state.Issues.Select(issue => new Dictionary<string, object?>
            {
                ["code"] = SetupDiagnosticsRedactor.SanitizeText(issue.Code),
                ["message"] = SetupDiagnosticsRedactor.SanitizeText(issue.Message),
                ["severity"] = issue.Severity.ToString(),
                ["detail"] = SetupDiagnosticsRedactor.SanitizeText(issue.Detail)
            }).ToArray(),
            ["next_actions"] = BuildNextActions(state)
        };
    }

    private static string[] BuildNextActions(LocalGatewaySetupState state)
    {
        var actions = new List<string>
        {
            $"Open setup summary: {LatestSummaryPathForCurrentUser()}",
            $"Open setup JSONL trace: {Path.Combine(ResolveLocalDataPath(), "Logs", "Setup", "easy-setup-latest.jsonl")}",
            $"Open tray log: {Logger.LogFilePath}"
        };

        var text = string.Join(" ", new[] { state.FailureCode, state.UserMessage }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Contains("wsl", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gateway", StringComparison.OrdinalIgnoreCase)
            || state.Issues.Any(issue => issue.Message.Contains("aka.ms/wsllogs", StringComparison.OrdinalIgnoreCase)))
        {
            actions.Add("If WSL diagnostics are needed, follow aka.ms/wsllogs.");
        }

        return actions.ToArray();
    }

    private static string? GetVisibleStage(LocalGatewaySetupPhase phase)
    {
        var index = LocalSetupProgressStageMap.IndexOfStageForPhase(phase);
        return index >= 0 ? LocalSetupProgressStageMap.VisibleStages[index].LabelKey : null;
    }

    private static LocalGatewaySetupPhase LastRunningPhase(LocalGatewaySetupState state)
    {
        for (var i = state.History.Count - 1; i >= 0; i--)
        {
            var phase = state.History[i].Phase;
            if (phase is not LocalGatewaySetupPhase.Failed
                and not LocalGatewaySetupPhase.Cancelled
                and not LocalGatewaySetupPhase.NotStarted)
            {
                return phase;
            }
        }

        return state.Phase;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record SetupDiagnosticRecord(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("run_id")] string? RunId,
    [property: JsonPropertyName("install_id")] string? InstallId,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset? TimestampUtc = null,
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("visible_stage")] string? VisibleStage = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("failure_code")] string? FailureCode = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null,
    [property: JsonPropertyName("duration_ms")] double? DurationMs = null,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, object?>? Details = null)
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion => LocalGatewaySetupDiagnosticsService.SchemaVersion;

    public SetupDiagnosticRecord Sanitized() => this with
    {
        TimestampUtc = TimestampUtc ?? DateTimeOffset.UtcNow,
        RunId = SetupDiagnosticsRedactor.SanitizeText(RunId),
        InstallId = SetupDiagnosticsRedactor.SanitizeText(InstallId),
        Phase = SetupDiagnosticsRedactor.SanitizeText(Phase),
        VisibleStage = SetupDiagnosticsRedactor.SanitizeText(VisibleStage),
        Status = SetupDiagnosticsRedactor.SanitizeText(Status),
        Message = SetupDiagnosticsRedactor.SanitizeText(Message),
        FailureCode = SetupDiagnosticsRedactor.SanitizeText(FailureCode),
        Details = SetupDiagnosticsRedactor.SanitizeDictionary(Details)
    };
}

internal static partial class SetupDiagnosticsRedactor
{
    private static readonly string[] SecretValueFlags =
    [
        "--token",
        "--bootstrap-token",
        "--operator-token",
        "--device-token",
        "--setup-code",
        "--password",
        "--pass",
        "--key",
        "--private-key",
        "--auth"
    ];

    [GeneratedRegex(@"(?i)(https?|wss?)://([^/\s:@]+):([^@\s/]+)@")]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*(?:PRIVATE|PUBLIC) KEY-----.*?-----END [A-Z ]*(?:PRIVATE|PUBLIC) KEY-----", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)(OPENCLAW_[A-Z0-9_]*(?:TOKEN|SECRET|KEY)|(?:setup[_-]?code|bootstrap[_-]?token|device[_-]?token|gateway[_-]?token|auth[_-]?token|private[_-]?key|password|secret))([^\r\n\S]*[:=][^\r\n\S]*)([^\s,;""'}]+)")]
    private static partial Regex KeyValueSecretRegex();

    public static string? SanitizeText(string? value)
    {
        if (value is null)
            return null;

        var sanitized = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        sanitized = PrivateKeyRegex().Replace(sanitized, "<redacted-private-key>");
        sanitized = JwtRegex().Replace(sanitized, "<redacted-jwt>");
        sanitized = UrlCredentialRegex().Replace(sanitized, "$1://<redacted>@");
        sanitized = KeyValueSecretRegex().Replace(sanitized, "$1$2<redacted>");
        sanitized = TokenSanitizer.Sanitize(SecretRedactor.Redact(sanitized));
        return sanitized;
    }

    public static IReadOnlyList<string> RedactArguments(IReadOnlyList<string> arguments)
    {
        var redacted = new List<string>(arguments.Count);
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                redacted.Add("<redacted>");
                redactNext = false;
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var flagName = equalsIndex > 0 ? argument[..equalsIndex] : argument;
            if (IsSecretFlag(flagName))
            {
                if (equalsIndex > 0)
                    redacted.Add(argument[..(equalsIndex + 1)] + "<redacted>");
                else
                {
                    redacted.Add(argument);
                    redactNext = true;
                }
                continue;
            }

            redacted.Add(SanitizeText(argument) ?? string.Empty);
        }

        return redacted;
    }

    public static string? SanitizeCommandOutput(string? value, int maxChars, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var retained = value;
        if (retained.Length > maxChars)
        {
            retained = retained[^maxChars..];
            truncated = true;
        }

        return SanitizeText(retained.Trim());
    }

    public static IReadOnlyDictionary<string, object?>? SanitizeDictionary(IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null)
            return null;

        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in details)
            sanitized[pair.Key] = SanitizeValue(pair.Value);
        return sanitized;
    }

    private static object? SanitizeValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => SanitizeText(s),
            IReadOnlyDictionary<string, object?> d => SanitizeDictionary(d),
            IEnumerable<string> strings => strings.Select(SanitizeText).ToArray(),
            IEnumerable<object?> values => values.Select(SanitizeValue).ToArray(),
            _ => value
        };
    }

    private static bool IsSecretFlag(string flagName)
    {
        foreach (var flag in SecretValueFlags)
        {
            if (flagName.Equals(flag, StringComparison.OrdinalIgnoreCase)
                || flagName.Contains("token", StringComparison.OrdinalIgnoreCase)
                || flagName.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || flagName.Contains("password", StringComparison.OrdinalIgnoreCase)
                || flagName.Contains("private", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
