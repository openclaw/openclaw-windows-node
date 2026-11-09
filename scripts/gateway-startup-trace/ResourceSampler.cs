using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Trace;

public sealed class ResourceSnapshot
{
    public long UtcTicks { get; set; }
    public long MonotonicTicks { get; set; }
    public long MonotonicFrequency { get; set; }
    public ulong Awake100ns { get; set; }
    public ulong Idle100ns { get; set; }
    public ulong Kernel100ns { get; set; }
    public ulong User100ns { get; set; }
    public ulong AvailableBytes { get; set; }
    public ulong TotalBytes { get; set; }
    public List<ProcessSnapshot> Processes { get; } = new();
    public int UnavailableProcesses { get; set; }
}

public sealed class ProcessSnapshot
{
    public int Id { get; set; }
    public long StartedUtcTicks { get; set; }
    public long ObservedUtcTicks { get; set; }
    public double CpuUserMs { get; set; }
    public double CpuKernelMs { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public ulong? ReadBytes { get; set; }
    public ulong? WriteBytes { get; set; }
    public ulong? ReadOperations { get; set; }
    public ulong? WriteOperations { get; set; }
}

public static class ResourceSampler
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    public static string[] PayloadRoots { get; set; } = Array.Empty<string>();
    public static Dictionary<int, long> SelectedProcesses { get; } = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    public static ResourceSnapshot Capture()
    {
        var result = new ResourceSnapshot
        {
            UtcTicks = DateTime.UtcNow.ToFileTimeUtc(),
            MonotonicTicks = Stopwatch.GetTimestamp(),
            MonotonicFrequency = Stopwatch.Frequency
        };
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GetSystemTimes(out var idle, out var kernel, out var user) ||
            !GlobalMemoryStatusEx(ref memory) || !QueryUnbiasedInterruptTime(out var awake))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        result.Idle100ns = (ulong)idle;
        result.Kernel100ns = (ulong)kernel;
        result.User100ns = (ulong)user;
        result.Awake100ns = awake;
        result.AvailableBytes = memory.AvailablePhysical;
        result.TotalBytes = memory.TotalPhysical;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (PayloadRoots.Length == 0 && !SelectedProcesses.ContainsKey(process.Id)) continue;
                    using var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                    if (handle.IsInvalid || !GetProcessTimes(handle, out var started, out _, out var cpuKernel, out var cpuUser))
                    {
                        result.UnavailableProcesses++;
                        continue;
                    }
                    var selected = SelectedProcesses.TryGetValue(process.Id, out var expected) && expected == started;
                    if (!selected && PayloadRoots.Length > 0)
                    {
                        var name = new StringBuilder(32768);
                        var size = name.Capacity;
                        if (QueryFullProcessImageName(handle, 0, name, ref size))
                        {
                            var image = name.ToString();
                            foreach (var root in PayloadRoots)
                                if (image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { selected = true; break; }
                        }
                        else result.UnavailableProcesses++;
                    }
                    if (!selected) continue;
                    var item = new ProcessSnapshot
                    {
                        Id = process.Id,
                        StartedUtcTicks = started,
                        CpuUserMs = cpuUser / 10000.0,
                        CpuKernelMs = cpuKernel / 10000.0,
                        WorkingSetBytes = process.WorkingSet64,
                        PeakWorkingSetBytes = process.PeakWorkingSet64,
                        PrivateBytes = process.PrivateMemorySize64
                    };
                    if (GetProcessIoCounters(handle, out var io))
                    {
                        item.ReadBytes = io.ReadBytes;
                        item.WriteBytes = io.WriteBytes;
                        item.ReadOperations = io.ReadOperations;
                        item.WriteOperations = io.WriteOperations;
                    }
                    item.ObservedUtcTicks = DateTime.UtcNow.ToFileTimeUtc();
                    result.Processes.Add(item);
                }
                catch (Exception ex) when (ex is InvalidOperationException ||
                    ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    // Processes can exit or deny access between enumeration and sampling.
                    result.UnavailableProcesses++;
                }
            }
        }
        return result;
    }
}
