using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationFinalizationState
{
    AwaitingInnoRemoval,
    InspectionFailed,
    StartupPreferenceFailed,
    StartupPreferenceRefused,
    RecordCleanupFailed,
    Finalized
}

public sealed record StoreMigrationFinalizationDecision(StoreMigrationFinalizationState State)
{
    /// <summary>
    /// A refused startup preference still finalizes the migration: Windows gave a durable answer,
    /// so the receipt is cleared and launch proceeds rather than retrying on every start.
    /// </summary>
    /// <remarks>
    /// A failed startup preference also allows launch. Finalization only reaches that step once
    /// the Inno source is verified removed, so the receipt no longer protects anything and a
    /// cosmetic startup preference must never block the app forever. The receipt is retained, so
    /// the preference is retried on the next launch.
    /// </remarks>
    public bool AllowsNormalStartup =>
        State is StoreMigrationFinalizationState.Finalized
              or StoreMigrationFinalizationState.StartupPreferenceRefused
              or StoreMigrationFinalizationState.StartupPreferenceFailed;
}

public enum InnoSourceRemovalStatus
{
    Removed,
    SourcePresent,
    InspectionFailed
}

/// <summary>
/// Verifies that source payload and runtime evidence disappeared after its registry entry did.
/// </summary>
public interface IInnoSourceRemovalVerifier
{
    InnoSourceRemovalStatus VerifyRemoved();
}

/// <summary>
/// Applies the completed migration's startup preference through the host platform.
/// </summary>
public interface IStoreMigrationAutoStartApplier
{
    Task ApplyAsync(bool enabled);
}

/// <summary>
/// Signals that the host platform durably refused the startup preference, for example when the
/// user or policy disabled startup for the app.
/// </summary>
/// <remarks>
/// A refusal is an answer, not a failure. Finalization must continue and clear the receipt so the
/// user is not blocked from launching on every start. Transient failures must not use this type.
/// </remarks>
public sealed class StoreMigrationAutoStartRefusedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Captures the bounded migration inventory at finalization time.
/// </summary>
public interface IMigrationInventoryCapture
{
    MigrationInventory Capture();
}

/// <summary>
/// Removes completed migration records only after the Store startup preference is applied.
/// </summary>
public interface IStoreMigrationRecordCleaner
{
    void ClearCompleted(MigrationRecord receipt);
}

internal enum InnoSourceProcessState
{
    Exited,
    ImageResolved,
    OwnedByOtherUser,
    Unresolved
}

internal sealed record InnoSourceProcessCandidate(InnoSourceProcessState State, string? ImagePath = null);

internal interface IInnoSourceProcessInspector
{
    IEnumerable<InnoSourceProcessCandidate> FindSameNameProcesses();
}

/// <summary>
/// Checks only the canonical source executable, uninstaller, process image, and instance mutex.
/// It never acquires or holds the source mutex.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoSourceRemovalVerifier : IInnoSourceRemovalVerifier
{
    private readonly string _sourceDirectory;
    private readonly string _mutexName;
    private readonly IInnoSourceProcessInspector _processes;

    public InnoSourceRemovalVerifier(MigrationBinding binding, string mutexName)
        : this(binding, mutexName, new WindowsInnoSourceProcessInspector(binding.UserSid))
    {
    }

    internal InnoSourceRemovalVerifier(
        MigrationBinding binding,
        string mutexName,
        IInnoSourceProcessInspector processes)
    {
        _sourceDirectory = CanonicalizeDirectory(binding.InstallDirectory);
        _mutexName = string.IsNullOrWhiteSpace(mutexName)
            ? throw new ArgumentException("Source mutex name is required.", nameof(mutexName))
            : mutexName;
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    public InnoSourceRemovalStatus VerifyRemoved()
    {
        try
        {
            var executable = Path.Combine(_sourceDirectory, "OpenClaw.Tray.WinUI.exe");
            var uninstaller = Path.Combine(_sourceDirectory, "unins000.exe");
            MigrationRecordCodec.RejectReparsePoints(executable);
            MigrationRecordCodec.RejectReparsePoints(uninstaller);
            if (File.Exists(executable) || File.Exists(uninstaller))
                return InnoSourceRemovalStatus.SourcePresent;

            var activity = new InnoSourceActivityVerifier(executable, _processes).VerifyStopped();
            if (activity == InnoSourceActivityStatus.Running)
                return InnoSourceRemovalStatus.SourcePresent;
            if (activity != InnoSourceActivityStatus.Stopped)
                return InnoSourceRemovalStatus.InspectionFailed;

            using var mutex = new Mutex(false, _mutexName, out var createdNew);
            return createdNew ? InnoSourceRemovalStatus.Removed : InnoSourceRemovalStatus.SourcePresent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         SecurityException or Win32Exception or
                                         InvalidOperationException or ArgumentException)
        {
            return InnoSourceRemovalStatus.InspectionFailed;
        }
    }

    private static string CanonicalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Source installation directory must be absolute.", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsInnoSourceProcessInspector(string expectedUserSid) : IInnoSourceProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNotFound = 1168;

    public IEnumerable<InnoSourceProcessCandidate> FindSameNameProcesses()
    {
        var candidates = new List<InnoSourceProcessCandidate>();
        foreach (var process in Process.GetProcessesByName("OpenClaw.Tray.WinUI"))
        {
            using (process)
            {
                int processId;
                try
                {
                    processId = process.Id;
                }
                catch (InvalidOperationException)
                {
                    candidates.Add(new(InnoSourceProcessState.Exited));
                    continue;
                }

                candidates.Add(Inspect(processId));
            }
        }
        return candidates;
    }

    private InnoSourceProcessCandidate Inspect(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error is ErrorInvalidParameter or ErrorNotFound
                ? new(InnoSourceProcessState.Exited)
                : new(InnoSourceProcessState.Unresolved);
        }

        try
        {
            if (TryGetImagePath(process, out var imagePath))
                return new(InnoSourceProcessState.ImageResolved, imagePath);

            return TryGetOwnerSid(process, out var ownerSid) &&
                   !string.Equals(ownerSid, expectedUserSid, StringComparison.OrdinalIgnoreCase)
                ? new(InnoSourceProcessState.OwnedByOtherUser)
                : new(InnoSourceProcessState.Unresolved);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool TryGetImagePath(IntPtr process, out string? imagePath)
    {
        for (var capacity = 512; capacity <= 32768; capacity *= 2)
        {
            var buffer = new System.Text.StringBuilder(capacity);
            var length = capacity;
            if (QueryFullProcessImageName(process, 0, buffer, ref length))
            {
                imagePath = buffer.ToString(0, length);
                return true;
            }
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                break;
        }

        imagePath = null;
        return false;
    }

    private static bool TryGetOwnerSid(IntPtr process, out string? ownerSid)
    {
        ownerSid = null;
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return false;

        try
        {
            _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var size);
            if (size == 0)
                return false;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, size, out _))
                    return false;
                var sid = Marshal.ReadIntPtr(buffer);
                ownerSid = new System.Security.Principal.SecurityIdentifier(sid).Value;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, System.Text.StringBuilder imagePath, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Captures the canonical inventory without giving finalization another owner for its policy.
/// </summary>
public sealed class MigrationInventoryCapture(MigrationBinding binding) : IMigrationInventoryCapture
{
    public MigrationInventory Capture() =>
        MigrationInventory.Capture(binding.RoamingDirectory, binding.LocalDirectory);
}

/// <summary>
/// Reparse-safe final cleanup that keeps the completion receipt until all earlier cleanup succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationFinalizationRecordCleaner(MigrationBinding binding) : IStoreMigrationRecordCleaner
{
    public void ClearCompleted(MigrationRecord receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var intentPath = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        var completionPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);

        MigrationRecordCodec.RejectReparsePoints(directory);
        MigrationRecordCodec.RejectReparsePoints(intentPath);
        MigrationRecordCodec.RejectReparsePoints(completionPath);
        var durable = MigrationRecordCodec.ReadCompletion(
            completionPath, binding, DateTime.UtcNow);
        if (!SameReceipt(durable, receipt))
            throw new InvalidDataException("Completion receipt changed before cleanup.");

        // The receipt is the recovery anchor, so it is always deleted last.
        File.Delete(intentPath);
        File.Delete(completionPath);
    }

    private static bool SameReceipt(MigrationRecord left, MigrationRecord right) =>
        left.Kind == right.Kind &&
        left.MigrationId == right.MigrationId &&
        left.SourceVersion == right.SourceVersion &&
        left.TargetVersion == right.TargetVersion &&
        left.Fingerprint == right.Fingerprint &&
        left.AutoStart == right.AutoStart &&
        left.CreatedUtc == right.CreatedUtc &&
        left.ExpiresUtc == right.ExpiresUtc &&
        left.InventoryJson == right.InventoryJson &&
        left.Binding.InstallDirectory == right.Binding.InstallDirectory &&
        left.Binding.RoamingDirectory == right.Binding.RoamingDirectory &&
        left.Binding.LocalDirectory == right.Binding.LocalDirectory &&
        left.Binding.Architecture == right.Binding.Architecture &&
        left.Binding.UserSid == right.Binding.UserSid;
}

/// <summary>
/// Finalizes a previously validated handoff after exact source removal. It intentionally does
/// not acquire the Inno-visible mutex, start services, or invoke uninstallation.
/// </summary>
public sealed class StoreMigrationFinalizationCoordinator(
    MigrationBinding binding,
    IInnoInstallationDetector detector,
    IInnoSourceRemovalVerifier sourceRemoval,
    IMigrationStartupRecordReader records,
    IMigrationInventoryCapture inventory,
    IStoreMigrationAutoStartApplier autoStart,
    IStoreMigrationRecordCleaner cleaner,
    IOpenClawLogger logger)
{
    public async Task<StoreMigrationFinalizationDecision> FinalizeAsync()
    {
        var detected = detector.Detect();
        if (detected.Status == InnoInstallationStatus.Detected)
            return new(StoreMigrationFinalizationState.AwaitingInnoRemoval);
        if (detected.Status is InnoInstallationStatus.Unsupported or InnoInstallationStatus.InspectionFailed)
            return new(StoreMigrationFinalizationState.InspectionFailed);
        if (detected.Status != InnoInstallationStatus.NotInstalled)
            throw new InvalidOperationException("Unknown Inno installation detection result.");

        var sourceRemovalStatus = sourceRemoval.VerifyRemoved();
        if (sourceRemovalStatus == InnoSourceRemovalStatus.SourcePresent)
            return new(StoreMigrationFinalizationState.AwaitingInnoRemoval);
        if (sourceRemovalStatus == InnoSourceRemovalStatus.InspectionFailed)
            return new(StoreMigrationFinalizationState.InspectionFailed);
        if (sourceRemovalStatus != InnoSourceRemovalStatus.Removed)
            throw new InvalidOperationException("Unknown Inno source-removal result.");

        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var lockPath = Path.Combine(directory, "prepare.lock");
        try
        {
            MigrationRecordCodec.RejectReparsePoints(directory);
            MigrationRecordCodec.RejectReparsePoints(lockPath);
            using var finalizationLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var durable = records.Read();
            if (durable.Status != MigrationStartupRecordStatus.Completed || durable.Record is null)
                return new(StoreMigrationFinalizationState.InspectionFailed);

            MigrationInventory current;
            try
            {
                current = inventory.Capture();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException)
            {
                logger.Error($"Store migration finalization inventory failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.InspectionFailed);
            }

            if (!string.Equals(current.Fingerprint, durable.Record.Fingerprint, StringComparison.Ordinal))
            {
                logger.Warn("Store migration finalization inventory no longer matches the completion receipt.");
                return new(StoreMigrationFinalizationState.InspectionFailed);
            }

            var startupRefused = false;
            try
            {
                await autoStart.ApplyAsync(durable.Record.AutoStart).ConfigureAwait(false);
            }
            catch (StoreMigrationAutoStartRefusedException exception)
            {
                logger.Warn($"Store migration startup preference refused by Windows: {exception.Message}");
                startupRefused = true;
            }
            catch (Exception exception) when (exception is COMException or IOException or
                                             UnauthorizedAccessException or InvalidOperationException)
            {
                logger.Error($"Store migration startup preference finalization failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.StartupPreferenceFailed);
            }

            try
            {
                cleaner.ClearCompleted(durable.Record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException)
            {
                logger.Error($"Store migration record cleanup failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.RecordCleanupFailed);
            }

            logger.Info($"Store migration finalized: {durable.Record.MigrationId}.");
            return new(startupRefused
                ? StoreMigrationFinalizationState.StartupPreferenceRefused
                : StoreMigrationFinalizationState.Finalized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidDataException)
        {
            logger.Error($"Store migration finalization could not acquire the preparation lock: {exception.Message}");
            return new(StoreMigrationFinalizationState.RecordCleanupFailed);
        }
    }
}
