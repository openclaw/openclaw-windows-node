using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.BrowserBootstrap;

/// <summary>Owns only a keyless probe tree, assigned before its input frame permits Node startup.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProbeProcessTree : IDisposable
{
    private readonly SafeFileHandle job;
    internal ProbeProcessTree(Process launcher)
    {
        job=CreateJobObjectW(IntPtr.Zero,null);
        if(job.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000}};
            if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job,launcher.SafeHandle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch{job.Dispose();throw;}
    }
    internal void Terminate()
    {
        if(!TerminateJobObject(job,1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal async Task JoinAsync()
    {
        // A launcher exit is not a descendant join. Keep management ownership until
        // the job reports no active process, even after cancellation requested a kill.
        while(true)
        {
            if(!QueryInformationJobObject(job,1,out var accounting,Marshal.SizeOf<Accounting>(),out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(accounting.ActiveProcesses==0)return;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    public void Dispose()=>job.Dispose();
    [StructLayout(LayoutKind.Sequential)]private struct Accounting
    {public long User,Kernel,PeriodUser,PeriodKernel;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits
    {public long User,JobUser;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters
    {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits
    {public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits limits,int length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,out Accounting accounting,int length,out int returned);
}
