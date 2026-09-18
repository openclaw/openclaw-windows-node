using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Attributes a workload to an already-owned package launcher without requiring its
/// descendants to inherit the caller's Windows job. This is same-user supervision,
/// not an isolation boundary against code running with that user's process-access rights.
/// </summary>
internal static class WindowsPackagedProcessAncestry
{
    internal static bool OwnsDescendant(Process launcher, Process listener, string expectedFamily)
    {
        try
        {
            return !launcher.HasExited &&
                string.Equals(GetFamily(launcher.SafeHandle), expectedFamily, StringComparison.Ordinal) &&
                IsLiveDescendant(launcher, listener);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // A failed inspection is a negative ownership result, never an adoption.
            return false;
        }
    }

    internal static bool IsLiveDescendant(Process launcher, Process listener)
    {
        var ancestors = new List<Process>();
        try
        {
            var launcherHandle = launcher.SafeHandle;
            if (launcher.HasExited || launcher.Id == listener.Id)
                return false;
            var owner = GetUser(launcherHandle);
            var child = listener;
            var seen = new HashSet<int> { child.Id };
            for (var depth = 0; depth < 16; depth++)
            {
                var childHandle = child.SafeHandle;
                if (child.HasExited || GetUser(childHandle) != owner)
                    return false;
                var parentId = GetParentId(childHandle);
                if (parentId == launcher.Id)
                {
                    return launcher.StartTime.ToUniversalTime() <= child.StartTime.ToUniversalTime() &&
                        !launcher.HasExited && !listener.HasExited && ancestors.All(p => !p.HasExited);
                }
                if (parentId <= 0 || !seen.Add(parentId))
                    return false;

                // Keep every handle until the complete chain has been checked. A reused
                // parent PID creates a newer process and fails the lifetime ordering check.
                var parent = Process.GetProcessById(parentId);
                ancestors.Add(parent);
                _ = parent.SafeHandle;
                if (parent.HasExited || parent.StartTime.ToUniversalTime() > child.StartTime.ToUniversalTime())
                    return false;
                child = parent;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            return false;
        }
        finally
        {
            foreach (var ancestor in ancestors)
                ancestor.Dispose();
        }
    }

    private static string GetUser(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Package workload verification requires Windows.");
        if (!OpenProcessToken(process, 8 /* TOKEN_QUERY */, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InvalidOperationException("The process has no user SID.");
    }

    private static string? GetFamily(SafeProcessHandle process)
    {
        uint length = 0;
        var result = GetPackageFamilyName(process, ref length, null);
        if (result == 15700) // APPMODEL_ERROR_NO_PACKAGE
            return null;
        if (result != 122 || length == 0) // ERROR_INSUFFICIENT_BUFFER
            throw new Win32Exception(result);
        var buffer = new char[length];
        result = GetPackageFamilyName(process, ref length, buffer);
        if (result != 0)
            throw new Win32Exception(result);
        return new string(buffer, 0, checked((int)length - 1));
    }

    private static int GetParentId(SafeProcessHandle process)
    {
        var status = NtQueryInformationProcess(process, 0, out var basic,
            (uint)Marshal.SizeOf<ProcessBasicInformation>(), out var returned);
        if (status != 0 || returned != Marshal.SizeOf<ProcessBasicInformation>())
            throw new InvalidOperationException("The process parent could not be verified.");
        return checked((int)basic.ParentProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint ProcessId;
        public nuint ParentProcessId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, [Out] char[]? name);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out ProcessBasicInformation information, uint size, out uint returned);
}
