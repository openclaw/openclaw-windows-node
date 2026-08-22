using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Reads the shared-GPU-memory capacity and current DXGI budget for NVIDIA
/// adapters. NVML reports dedicated CUDA memory only, while Task Manager's GPU
/// memory total also includes this DXGI-reported shared allocation.
/// </summary>
internal static class DxgiGpuMemoryProbe
{
    private const uint NvidiaVendorId = 0x10DE;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private static readonly Guid IidDxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid IidDxgiAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

    public static IReadOnlyDictionary<string, DxgiGpuMemoryInfo> CaptureNvidiaMemoryByName()
    {
        if (!OperatingSystem.IsWindows())
            return new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return Capture();
        }
        catch (DllNotFoundException)
        {
            return new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);
        }
        catch (EntryPointNotFoundException)
        {
            return new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyDictionary<string, DxgiGpuMemoryInfo> Capture()
    {
        Guid factoryId = IidDxgiFactory1;
        int createResult = CreateDXGIFactory1(ref factoryId, out IntPtr factory);
        if (createResult < 0 || factory == IntPtr.Zero)
            return new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);

        var results = new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerateAdapters = GetDelegate<EnumAdapters1>(factory, 12);
            for (uint index = 0; ; index++)
            {
                int enumerateResult = enumerateAdapters(factory, index, out IntPtr adapter);
                if (enumerateResult == DxgiErrorNotFound)
                    break;
                if (enumerateResult < 0 || adapter == IntPtr.Zero)
                    continue;

                try
                {
                    AddNvidiaAdapterMemory(adapter, results, ambiguousNames);
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return results;
    }

    private static void AddNvidiaAdapterMemory(
        IntPtr adapter,
        IDictionary<string, DxgiGpuMemoryInfo> results,
        ISet<string> ambiguousNames)
    {
        var getDescription = GetDelegate<GetDesc1>(adapter, 10);
        if (getDescription(adapter, out DxgiAdapterDescription description) < 0 ||
            description.VendorId != NvidiaVendorId ||
            string.IsNullOrWhiteSpace(description.Description))
        {
            return;
        }

        long? sharedMemoryBytes = ToInt64(description.SharedSystemMemory);
        long? freeSharedMemoryBytes = QueryFreeSharedMemory(adapter);
        AddMemoryByName(
            results,
            ambiguousNames,
            description.Description,
            new DxgiGpuMemoryInfo(sharedMemoryBytes, freeSharedMemoryBytes));
    }

    internal static void AddMemoryByName(
        IDictionary<string, DxgiGpuMemoryInfo> results,
        ISet<string> ambiguousNames,
        string adapterName,
        DxgiGpuMemoryInfo memory)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(ambiguousNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        ArgumentNullException.ThrowIfNull(memory);

        string normalizedName = NormalizeName(adapterName);
        if (ambiguousNames.Contains(normalizedName))
            return;
        if (results.ContainsKey(normalizedName))
        {
            results.Remove(normalizedName);
            ambiguousNames.Add(normalizedName);
            return;
        }

        results.Add(normalizedName, memory);
    }

    private static long? QueryFreeSharedMemory(IntPtr adapter)
    {
        var queryInterface = GetDelegate<QueryInterface>(adapter, 0);
        Guid adapter3Id = IidDxgiAdapter3;
        if (queryInterface(adapter, ref adapter3Id, out IntPtr adapter3) < 0 || adapter3 == IntPtr.Zero)
            return null;

        try
        {
            var queryMemory = GetDelegate<QueryVideoMemoryInfo>(adapter3, 14);
            if (queryMemory(adapter3, 0, DxgiMemorySegmentGroup.NonLocal, out DxgiVideoMemoryInfo memory) < 0 ||
                memory.Budget == 0 ||
                memory.Budget < memory.CurrentUsage)
            {
                return null;
            }

            return ToInt64(memory.Budget - memory.CurrentUsage);
        }
        finally
        {
            Marshal.Release(adapter3);
        }
    }

    private static T GetDelegate<T>(IntPtr instance, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr function = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static long? ToInt64(ulong value) =>
        value <= long.MaxValue ? (long)value : null;

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(IntPtr instance, ref Guid interfaceId, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr instance, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr instance, out DxgiAdapterDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfo(
        IntPtr instance,
        uint nodeIndex,
        DxgiMemorySegmentGroup segmentGroup,
        out DxgiVideoMemoryInfo memoryInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    private enum DxgiMemorySegmentGroup
    {
        Local = 0,
        NonLocal = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}

internal sealed record DxgiGpuMemoryInfo(
    long? SharedMemoryBytes,
    long? FreeSharedMemoryBytes);
