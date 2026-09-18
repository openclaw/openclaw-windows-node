using Microsoft.Win32.SafeHandles;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Creates the package launcher suspended, assigns its kill-on-close job, then resumes it.
/// Explicit JOB_LIST creation conflicts with an already-active package's Windows job.
/// No launcher code executes before assignment; assignment/resume failures terminate it.
/// Output is not captured or exported. The package remains non-isolated.
/// </summary>
internal sealed class WindowsNativeGatewayProcessHost : INativeGatewayProcessHost
{
    public Task<INativeGatewayProcess> StartAsync(NativeGatewayStartSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native gateway supervision requires Windows.");

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw Failure("Could not create the native gateway lifecycle job.");
        }
        IntPtr environment = IntPtr.Zero;
        Process? launcher = null;
        try
        {
            var limits = new ExtendedLimits
            {
                BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 }, // KILL_ON_JOB_CLOSE
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw Failure("Could not configure the native gateway lifecycle job.");

            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(spec.Environment));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
            };
            var command = new StringBuilder($"\"{spec.ExecutablePath}\" gateway run --port {spec.Port} --bind loopback");
            cancellationToken.ThrowIfCancellationRequested();
            if (!CreateProcessW(spec.ExecutablePath, command, IntPtr.Zero, IntPtr.Zero, false,
                0x00000004 | 0x00000400 | 0x08000000, // SUSPENDED, UNICODE_ENVIRONMENT, NO_WINDOW
                environment, spec.WorkingDirectory, ref startup, out var created))
            {
                throw Failure("Could not start the installed native gateway package alias.");
            }
            using var process = new SafeProcessHandle(created.Process, ownsHandle: true);
            using var thread = new SafeNativeHandle(created.Thread);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                    throw Failure("Could not assign the suspended Gateway launcher to its lifecycle job.");
                // Acquire and retain the managed handle while the original creation handle
                // is held and the process is suspended. A later PID lookup is not an anchor.
                launcher = Process.GetProcessById(checked((int)created.ProcessId));
                _ = launcher.SafeHandle;
                if (ResumeThread(thread) == uint.MaxValue)
                    throw Failure("Could not resume the owned Gateway launcher.");
            }
            catch (Exception launchFailure)
            {
                if (!TerminateProcess(process, 1))
                    throw new AggregateException(launchFailure,
                        Failure("Could not terminate the suspended Gateway launcher after a launch failure."));
                throw;
            }
            var owned = new JobProcess(job, launcher, spec.PackageFamilyName);
            job = null!;
            launcher = null;
            if (cancellationToken.IsCancellationRequested)
            {
                owned.DisposeAsync().AsTask().GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult<INativeGatewayProcess>(owned);
        }
        finally
        {
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
            launcher?.Dispose();
            job?.Dispose();
        }
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string)entry.Value!;
        foreach (var entry in overrides)
            environment[entry.Key] = entry.Value;
        return string.Join('\0', environment.Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
    }

    private sealed class JobProcess(SafeNativeHandle job, Process launcher, string? family) : INativeGatewayProcess
    {
        private int _disposed;

        public bool HasExited => _disposed != 0 || ActiveProcesses() == 0;

        private uint ActiveProcesses()
        {
            if (!QueryInformationJobObject(job, 1, out BasicAccounting accounting,
                (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                throw Failure("Could not inspect the native gateway lifecycle job.");
            return accounting.ActiveProcesses;
        }

        public bool Owns(WindowsTcpListenerInfo listener)
        {
            if (_disposed != 0 || listener.ProcessId <= 0 || listener.ProcessStartTimeUtc is null)
                return false;
            try
            {
                using var process = Process.GetProcessById(listener.ProcessId);
                // Retain this process handle throughout the lifetime/membership comparison.
                // Looking up a PID by itself cannot prove that the TCP snapshot still applies.
                var handle = process.SafeHandle;
                return !process.HasExited &&
                    process.StartTime.ToUniversalTime() == listener.ProcessStartTimeUtc.Value &&
                    IsProcessInJob(handle, job, out var member) &&
                    (member || (family is not null && !launcher.HasExited &&
                        IsProcessInJob(launcher.SafeHandle, job, out var launcherMember) && launcherMember &&
                        WindowsPackagedProcessAncestry.OwnsDescendant(launcher, process, family))) &&
                    !process.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                if (!TerminateJobObject(job, 1))
                    throw Failure("Could not stop the native gateway lifecycle job.");
                var elapsed = Stopwatch.StartNew();
                while (ActiveProcesses() != 0)
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                        throw new TimeoutException("The native gateway lifecycle job did not stop in time.");
                    await Task.Delay(20).ConfigureAwait(false);
                }
            }
            finally
            {
                job.Dispose();
                launcher.Dispose();
            }
        }
    }

    private static Win32Exception Failure(string message) => new(Marshal.GetLastWin32Error(), message);

    private sealed class SafeNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeNativeHandle() : base(true) { }
        public SafeNativeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeNativeHandle CreateJobObjectW(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeNativeHandle job, int informationClass,
        ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeNativeHandle job, int informationClass,
        out BasicAccounting information, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeNativeHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeNativeHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeNativeHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeNativeHandle thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
