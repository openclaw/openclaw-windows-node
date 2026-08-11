using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum ManagedLocalPortConflictRepairOutcome
{
    NotNeeded,
    Repaired,
    BlockedUnknownOwner,
    Failed,
}

public sealed record ManagedLocalPortConflictRepairResult(
    ManagedLocalPortConflictRepairOutcome Outcome,
    string? Detail = null);

internal interface IManagedLocalGatewayPortPlatform
{
    WindowsTcpListenerSnapshotResult CaptureListeners();
    string? GetProcessCommandLine(int processId);
    WslRelayTrustResult InspectWslRelayBinary(string processPath);
    bool IsExpectedWslGatewayListening(string distroName, int port);
    string? ReadScheduledTaskXml(string taskName);
    string? ReadFile(string path);
    Task<bool> DisableScheduledTaskAsync(string taskName, CancellationToken cancellationToken);
    Task EndScheduledTaskAsync(string taskName, CancellationToken cancellationToken);
    Task<bool> StopProcessAsync(
        int processId,
        DateTime? expectedStartTimeUtc,
        CancellationToken cancellationToken);
}

internal readonly record struct WslRelayTrustResult(bool IsTrusted, string? Detail)
{
    public static WslRelayTrustResult Trusted() => new(true, null);

    public static WslRelayTrustResult Rejected(string detail) => new(false, detail);
}

internal sealed class WindowsManagedLocalGatewayPortPlatform : IManagedLocalGatewayPortPlatform
{
    public WindowsTcpListenerSnapshotResult CaptureListeners() =>
        WindowsTcpListenerSnapshot.Capture();

    public string? GetProcessCommandLine(int processId) =>
        WindowsTcpListenerSnapshot.GetProcessCommandLine(processId);

    public WslRelayTrustResult InspectWslRelayBinary(string processPath) =>
        EvaluateWslRelayBinary(processPath, WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile);

    internal static WslRelayTrustResult EvaluateWslRelayBinary(
        string processPath,
        Func<string, AuthenticodeTrustResult> verifySignature)
    {
        try
        {
            var fullPath = Path.GetFullPath(processPath);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var canonicalProgramFiles = Path.Combine(programFiles, "WSL", "wslrelay.exe");
            var canonicalSystem = Path.Combine(systemRoot, "System32", "wslrelay.exe");
            var windowsAppsRoot = Path.Combine(programFiles, "WindowsApps") + Path.DirectorySeparatorChar;
            var isCanonical =
                string.Equals(fullPath, canonicalProgramFiles, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, canonicalSystem, StringComparison.OrdinalIgnoreCase) ||
                (fullPath.StartsWith(windowsAppsRoot, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(Path.GetFileName(fullPath), "wslrelay.exe", StringComparison.OrdinalIgnoreCase));
            if (!isCanonical)
            {
                return WslRelayTrustResult.Rejected(
                    "WSL relay executable path is not canonical.");
            }

            var signature = verifySignature(fullPath);
            return signature.IsTrusted
                ? WslRelayTrustResult.Trusted()
                : WslRelayTrustResult.Rejected(
                    signature.Detail ?? "WSL relay Authenticode verification failed.");
        }
        catch
        {
            return WslRelayTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }
    }

    public bool IsExpectedWslGatewayListening(string distroName, int port)
    {
        try
        {
            var probe = CreateExpectedWslGatewayProbe(distroName, port);
            using var process = Process.Start(probe.StartInfo);
            if (process is null)
                return false;
            process.StandardInput.Write(probe.StandardInput);
            process.StandardInput.Close();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static (ProcessStartInfo StartInfo, string StandardInput) CreateExpectedWslGatewayProbe(
        string distroName,
        int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveWslExePath(),
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(distroName);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-s");

        // WSL relay can project one distro socket onto both Windows loopback
        // families. Verify the service-owned port inside the expected distro
        // without assuming family parity across that relay boundary.
        var script =
            "pid=$(systemctl --user show openclaw-gateway -p MainPID --value 2>/dev/null); " +
            "test \"${pid:-0}\" -gt 0 && " +
            $"ss -ltnp 2>/dev/null | grep -E ':{port}([^0-9]|$)' | " +
            "grep -F \"pid=$pid,\" >/dev/null";
        return (startInfo, script);
    }

    public string? ReadScheduledTaskXml(string taskName) =>
        RunSchtasks(["/Query", "/TN", taskName, "/XML"], CancellationToken.None).GetAwaiter().GetResult().Output;

    public string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public async Task<bool> DisableScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        var result = await RunSchtasks(["/Change", "/TN", taskName, "/Disable"], cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    public async Task EndScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        _ = await RunSchtasks(["/End", "/TN", taskName], cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StopProcessAsync(
        int processId,
        DateTime? expectedStartTimeUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (expectedStartTimeUtc is { } expected &&
                process.StartTime.ToUniversalTime() != expected)
            {
                return false;
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already exited
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Success, string? Output)> RunSchtasks(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WindowsStartupTaskRegistration.ResolveSchtasksPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            process = Process.Start(psi);
            if (process is null)
                return (false, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return (process.ExitCode == 0, output);
        }

        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return (false, null);
        }
        catch
        {
            return (false, null);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveWslExePath()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDir))
            windowsDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(windowsDir, "System32", "wsl.exe");
    }
}

/// <summary>
/// Proves which process owns a managed WSL gateway's loopback endpoint. Unknown owners are never
/// terminated and never trusted with shared/bootstrap credential fallback.
/// </summary>
public sealed class ManagedLocalGatewayPortProvenanceService
{
    private readonly IManagedLocalGatewayPortPlatform _platform;
    private readonly IOpenClawLogger _logger;
    private readonly Func<string> _getUserProfilePath;
    private readonly ConcurrentDictionary<ProvenanceCacheKey, GatewayEndpointProvenance>
        _lastProvenance = new();
    private readonly record struct ProvenanceCacheKey(string GatewayId, string Url);

    internal ManagedLocalGatewayPortProvenanceService(
        IManagedLocalGatewayPortPlatform platform,
        IOpenClawLogger logger,
        Func<string>? getUserProfilePath = null)
    {
        _platform = platform;
        _logger = logger;
        _getUserProfilePath = getUserProfilePath ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public ManagedLocalGatewayPortProvenanceService(IOpenClawLogger logger)
        : this(new WindowsManagedLocalGatewayPortPlatform(), logger)
    {
    }

    public Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(record), cancellationToken);

    public GatewayEndpointProvenance Inspect(GatewayRecord record)
    {
        var result = InspectCore(record);
        _lastProvenance[new ProvenanceCacheKey(record.Id, record.Url)] = result;
        return result;
    }

    public bool IsStrongCredentialAllowed(GatewayRecord record, GatewayCredential credential)
    {
        var isStrong =
            credential.IsBootstrapToken ||
            string.Equals(credential.Source, CredentialResolver.SourceSharedGatewayToken, StringComparison.Ordinal) ||
            string.Equals(credential.Source, CredentialResolver.SourceBootstrapToken, StringComparison.Ordinal);
        var needsProof =
            isStrong &&
            record.SshTunnel is null &&
            (record.IsLocal || GatewayRecordEditing.ResolveManagedDistroName(record) is not null) &&
            GatewayRecordEditing.IsLoopbackEndpoint(record.Url);
        if (!needsProof)
            return true;

        if (!_lastProvenance.TryGetValue(
                new ProvenanceCacheKey(record.Id, record.Url),
                out var cached) ||
            cached.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway ||
            !Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) ||
            GatewayRecordEditing.ResolveManagedDistroName(record) is not { } managedDistroName)
        {
            return false;
        }

        var currentSnapshot = _platform.CaptureListeners();
        if (!HasRequiredAddressFamilies(currentSnapshot, uri))
            return false;
        var current = currentSnapshot.Listeners
            .Where(listener => listener.Port == uri.Port && AcceptsHost(listener.Address, uri.Host))
            .ToArray();

        if (HasNoWindowsProcessIdentity(cached))
        {
            if (current.Length != 0 ||
                !_platform.IsExpectedWslGatewayListening(managedDistroName, uri.Port))
            {
                return false;
            }

            return ListenerSnapshotStillCurrent(current, uri);
        }

        if (cached.ProcessId is not int expectedPid ||
            cached.ProcessStartTimeUtc is not DateTime expectedStart ||
            string.IsNullOrWhiteSpace(cached.ProcessPath))
        {
            return false;
        }

        if (current.Length == 0 ||
            !current.All(listener =>
                listener.ProcessId == expectedPid &&
                listener.ProcessStartTimeUtc == expectedStart &&
                string.Equals(
                    listener.ProcessPath,
                    cached.ProcessPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _platform.IsExpectedWslGatewayListening(managedDistroName, uri.Port);
    }

    private GatewayEndpointProvenance InspectCore(GatewayRecord record)
    {
        if (record.SshTunnel is not null ||
            GatewayRecordEditing.ResolveManagedDistroName(record) is not { } managedDistroName ||
            !Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) ||
            !uri.IsLoopback ||
            uri.Port is < 1 or > 65535)
        {
            return new GatewayEndpointProvenance(GatewayEndpointProvenanceKind.NotApplicable, 0);
        }

        var snapshot = _platform.CaptureListeners();
        if (!HasRequiredAddressFamilies(snapshot, uri))
        {
            return new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener,
                uri.Port,
                Detail: "Windows TCP listener ownership could not be captured completely.");
        }

        var listeners = snapshot.Listeners
            .Where(listener => listener.Port == uri.Port && AcceptsHost(listener.Address, uri.Host))
            .ToArray();
        if (listeners.Length == 0)
        {
            if (!_platform.IsExpectedWslGatewayListening(managedDistroName, uri.Port))
                return new GatewayEndpointProvenance(GatewayEndpointProvenanceKind.NoListener, uri.Port);

            if (!ListenerSnapshotStillCurrent(listeners, uri))
            {
                return new GatewayEndpointProvenance(
                    GatewayEndpointProvenanceKind.UnknownListener,
                    uri.Port,
                    Detail: "Windows listener ownership changed during relayless provenance verification.",
                    FailureReason: GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged);
            }

            return new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                uri.Port,
                Detail: $"Expected WSL gateway owns port {uri.Port} without a Windows relay listener.");
        }

        var relayTrustByPath = listeners
            .Where(listener =>
                string.Equals(listener.ProcessName, "wslrelay", StringComparison.OrdinalIgnoreCase) &&
                listener.ProcessPath is not null)
            .Select(listener => listener.ProcessPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => path,
                path => _platform.InspectWslRelayBinary(path),
                StringComparer.OrdinalIgnoreCase);
        var expectedDistroListening =
            relayTrustByPath.Values.Any(result => result.IsTrusted) &&
            _platform.IsExpectedWslGatewayListening(managedDistroName, uri.Port);
        var classified = listeners
            .Select(listener => ClassifyListener(
                managedDistroName,
                uri.Port,
                listener,
                relayTrustByPath,
                expectedDistroListening))
            .ToArray();

        if (!ListenerSnapshotStillCurrent(listeners, uri))
        {
            return new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener,
                uri.Port,
                Detail: "Loopback listener ownership changed during provenance verification.",
                FailureReason: GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged);
        }

        if (classified.All(item => item.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway))
        {
            var first = classified[0];
            return first with { Detail = $"Expected WSL relay owns loopback port {uri.Port}." };
        }

        if (classified.All(item => item.Kind == GatewayEndpointProvenanceKind.ConflictingOpenClawGateway) &&
            classified.Select(item => item.ProcessId).Distinct().Count() == 1)
        {
            return classified[0];
        }

        var ownerSummary = string.Join(
            "; ",
            classified
                .Where(item => item.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                .Select(item =>
                    $"{item.ProcessName ?? "unknown"} (PID {item.ProcessId?.ToString() ?? "?"}): " +
                    (item.Detail ?? "listener verification failed"))
                .Distinct(StringComparer.Ordinal));
        return new GatewayEndpointProvenance(
            GatewayEndpointProvenanceKind.UnknownListener,
            uri.Port,
            Detail: $"Loopback port {uri.Port} listener verification failed: {ownerSummary}.");
    }

    public async Task<ManagedLocalPortConflictRepairResult> RepairConflictAsync(
        GatewayRecord record,
        CancellationToken cancellationToken,
        Func<bool>? canContinue = null)
    {
        canContinue ??= static () => true;
        var proof = Inspect(record);
        if (proof.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway ||
            proof.Kind == GatewayEndpointProvenanceKind.NoListener ||
            proof.Kind == GatewayEndpointProvenanceKind.NotApplicable)
        {
            return new ManagedLocalPortConflictRepairResult(ManagedLocalPortConflictRepairOutcome.NotNeeded);
        }

        if (proof.Kind != GatewayEndpointProvenanceKind.ConflictingOpenClawGateway ||
            proof.ProcessId is not int processId ||
            string.IsNullOrWhiteSpace(proof.ScheduledTaskName))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
                proof.Detail);
        }
        if (!canContinue())
            return AbortedByIntent();

        // Re-prove immediately before every destructive action.
        var current = Inspect(record);
        if (!SameProcessIdentity(current, proof) ||
            !string.Equals(current.ScheduledTaskName, proof.ScheduledTaskName, StringComparison.Ordinal))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
                "Listener ownership changed before repair; no process was stopped.");
        }
        if (!canContinue())
            return AbortedByIntent();

        if (!await _platform.DisableScheduledTaskAsync(proof.ScheduledTaskName!, cancellationToken).ConfigureAwait(false))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.Failed,
                $"Could not disable scheduled task '{proof.ScheduledTaskName}'.");
        }

        if (!canContinue())
            return AbortedByIntent();
        await _platform.EndScheduledTaskAsync(proof.ScheduledTaskName!, cancellationToken).ConfigureAwait(false);

        current = Inspect(record);
        if (current.Kind is GatewayEndpointProvenanceKind.NoListener or
            GatewayEndpointProvenanceKind.ExpectedManagedGateway)
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.Repaired,
                "The obsolete native task released the managed WSL gateway port.");
        }

        if (!SameProcessIdentity(current, proof))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
                "Listener ownership changed after the task was disabled; no process was stopped.");
        }
        if (!canContinue())
            return AbortedByIntent();

        if (!await _platform.StopProcessAsync(
                processId,
                proof.ProcessStartTimeUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.Failed,
                $"Could not stop the proven obsolete OpenClaw gateway process (PID {processId}).");
        }

        current = Inspect(record);
        if (current.Kind is not (GatewayEndpointProvenanceKind.NoListener or
            GatewayEndpointProvenanceKind.ExpectedManagedGateway))
        {
            return new ManagedLocalPortConflictRepairResult(
                ManagedLocalPortConflictRepairOutcome.Failed,
                "The proven process stopped, but the managed gateway port is still owned by an unverified listener.");
        }

        _logger.Info($"[GatewayPort] Disabled '{proof.ScheduledTaskName}' and stopped proven obsolete native gateway PID {processId}.");
        return new ManagedLocalPortConflictRepairResult(
            ManagedLocalPortConflictRepairOutcome.Repaired,
            "Removed a proven obsolete native OpenClaw gateway from the managed WSL gateway port.");
    }

    private static ManagedLocalPortConflictRepairResult AbortedByIntent() =>
        new(
            ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
            "Gateway changed or was explicitly disconnected before port-conflict repair completed.");

    private GatewayEndpointProvenance ClassifyListener(
        string managedDistroName,
        int port,
        WindowsTcpListenerInfo listener,
        IReadOnlyDictionary<string, WslRelayTrustResult> relayTrustByPath,
        bool expectedDistroListening)
    {
        var isWslRelay =
            string.Equals(listener.ProcessName, "wslrelay", StringComparison.OrdinalIgnoreCase);
        var relayPath = listener.ProcessPath;
        var relayTrust = default(WslRelayTrustResult);
        var hasRelayTrust =
            relayPath is not null &&
            relayTrustByPath.TryGetValue(relayPath, out relayTrust);
        var trustedRelay = hasRelayTrust && relayTrust.IsTrusted;
        if (isWslRelay && trustedRelay && expectedDistroListening)
        {
            return new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                port,
                listener.ProcessId,
                listener.ProcessName,
                listener.ProcessStartTimeUtc,
                listener.ProcessPath);
        }

        var taskName = $"OpenClaw Gateway ({managedDistroName})";
        if (IsProvenObsoleteNativeGateway(managedDistroName, port, listener, taskName))
        {
            return new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.ConflictingOpenClawGateway,
                port,
                listener.ProcessId,
                listener.ProcessName,
                listener.ProcessStartTimeUtc,
                listener.ProcessPath,
                taskName,
                $"Proven obsolete native OpenClaw gateway '{taskName}' owns loopback port {port}.");
        }

        var detail = isWslRelay
            ? relayPath is null
                ? "WSL relay executable path could not be read."
                : !trustedRelay
                    ? relayTrust.Detail ??
                        "WSL relay Authenticode verification failed."
                    : $"Expected distro '{managedDistroName}' does not report its systemd gateway MainPID owning port {port}."
            : "Process is not a verified managed WSL relay or proven obsolete OpenClaw gateway.";
        return new GatewayEndpointProvenance(
            GatewayEndpointProvenanceKind.UnknownListener,
            port,
            listener.ProcessId,
            listener.ProcessName,
            listener.ProcessStartTimeUtc,
            listener.ProcessPath,
            Detail: detail);
    }

    private bool IsProvenObsoleteNativeGateway(
        string managedDistroName,
        int port,
        WindowsTcpListenerInfo listener,
        string taskName)
    {
        if (!string.Equals(Path.GetFileName(listener.ProcessPath), "node.exe", StringComparison.OrdinalIgnoreCase) ||
            listener.ProcessStartTimeUtc is null)
            return false;

        var commandLine = _platform.GetProcessCommandLine(listener.ProcessId);
        if (string.IsNullOrWhiteSpace(commandLine) ||
            !commandLine.Contains(@"\OpenClawTray\native-cli\", StringComparison.OrdinalIgnoreCase) ||
            !commandLine.Contains(@"\openclaw\dist\index.js", StringComparison.OrdinalIgnoreCase) ||
            !commandLine.Contains(" gateway ", StringComparison.OrdinalIgnoreCase) ||
            !commandLine.Contains($"--port {port}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var profileDir = Path.Combine(
            _getUserProfilePath(),
            $".openclaw-{managedDistroName}");
        var vbsPath = Path.Combine(profileDir, "gateway.vbs");
        var cmdPath = Path.Combine(profileDir, "gateway.cmd");
        var taskXml = _platform.ReadScheduledTaskXml(taskName);
        var vbs = _platform.ReadFile(vbsPath);
        var cmd = _platform.ReadFile(cmdPath);
        if (string.IsNullOrWhiteSpace(taskXml) ||
            string.IsNullOrWhiteSpace(vbs) ||
            string.IsNullOrWhiteSpace(cmd))
        {
            return false;
        }

        string? taskCommand;
        try
        {
            taskCommand = XDocument.Parse(taskXml)
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "Command", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }
        catch
        {
            return false;
        }

        try
        {
            return string.Equals(
                    Path.GetFullPath(taskCommand ?? string.Empty),
                    Path.GetFullPath(vbsPath),
                    StringComparison.OrdinalIgnoreCase) &&
                vbs.Contains(cmdPath, StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains($"OPENCLAW_STATE_DIR={profileDir}", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains($"OPENCLAW_WINDOWS_TASK_NAME={taskName}", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains($"OPENCLAW_GATEWAY_PORT={port}", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains(@"\OpenClawTray\native-cli\", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains(" gateway --port ", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameProcessIdentity(
        GatewayEndpointProvenance left,
        GatewayEndpointProvenance right) =>
        left.Kind == GatewayEndpointProvenanceKind.ConflictingOpenClawGateway &&
        left.ProcessId == right.ProcessId &&
        left.ProcessStartTimeUtc == right.ProcessStartTimeUtc;

    private static bool HasNoWindowsProcessIdentity(GatewayEndpointProvenance provenance) =>
        provenance.ProcessId is null &&
        provenance.ProcessName is null &&
        provenance.ProcessStartTimeUtc is null &&
        provenance.ProcessPath is null &&
        provenance.ScheduledTaskName is null;

    private static bool AcceptsHost(IPAddress listenerAddress, string host)
    {
        if (listenerAddress.Equals(IPAddress.Any) || listenerAddress.Equals(IPAddress.IPv6Any))
            return true;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.IsLoopback(listenerAddress);
        return IPAddress.TryParse(host, out var target) && listenerAddress.Equals(target);
    }

    private bool ListenerSnapshotStillCurrent(
        IReadOnlyList<WindowsTcpListenerInfo> original,
        Uri uri)
    {
        var snapshot = _platform.CaptureListeners();
        if (!HasRequiredAddressFamilies(snapshot, uri))
            return false;
        var current = snapshot.Listeners
            .Where(listener => listener.Port == uri.Port && AcceptsHost(listener.Address, uri.Host))
            .ToArray();
        if (current.Length != original.Count)
            return false;

        return original.All(expected => current.Any(actual =>
            actual.Address.Equals(expected.Address) &&
            actual.Port == expected.Port &&
            actual.ProcessId == expected.ProcessId &&
            actual.ProcessStartTimeUtc == expected.ProcessStartTimeUtc &&
            string.Equals(actual.ProcessPath, expected.ProcessPath, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasRequiredAddressFamilies(
        WindowsTcpListenerSnapshotResult snapshot,
        Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return snapshot.Ipv4Complete && snapshot.Ipv6Complete;
        if (!IPAddress.TryParse(uri.Host, out var address))
            return false;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? snapshot.Ipv4Complete
            : snapshot.Ipv6Complete;
    }
}
