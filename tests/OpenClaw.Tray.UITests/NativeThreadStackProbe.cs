// Captures a content-free native stack (module+offset frames) of another thread in this process.
//
// UIThreadFixture uses it when WinUI startup misses its ready deadline: CI collects no memory
// dumps, so without this the log only names the managed phase, never the native call that blocks.
// Frames hold module file names and offsets only, never memory contents or full paths, and can be
// symbolized offline against the runtime versions recorded in native-failure.json. The first frame
// is the instruction pointer; later frames are return addresses (subtract 1 when symbolizing).
//
//   NativeThreadStackProbe.Start();                       // before the watched thread can stall
//   var stack = NativeThreadStackProbe.Capture(threadId); // bounded; never throws
//
// Start creates a dedicated worker that warms itself, so a capture creates no thread and loads no
// DLL: a stalled thread may hold the loader lock. The worker copies the target's registers from a
// PssCaptureSnapshot (which resumes the target before returning, so no thread stays suspended
// while managed code runs) and walks with StackWalk64. The target keeps running, so the walk reads
// the stack through ReadProcessMemory, which fails instead of faulting on a stale address.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.UITests;

internal static class NativeThreadStackProbe
{
    private const int MaxFrames = 48;
    private const int ContextSize = 1232;
    private const int RbpOffset = 0xA0;
    private const int RspOffset = 0x98;
    private const int RipOffset = 0xF8;
    private const uint ContextFull = 0x10000B;
    private const uint CaptureThreads = 0x80;
    private const uint CaptureThreadContext = 0x100;
    private const int WalkThreads = 3;
    private const int ErrorNoMoreItems = 259;
    private const uint MachineAmd64 = 0x8664;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    // PSS_THREAD_ENTRY (x64) field offsets.
    private const int EntrySize = 120;
    private const int EntryThreadIdOffset = 20;
    private const int EntryContextSizeOffset = 110;
    private const int EntryContextOffset = 112;
    // STACKFRAME64 (x64): five 16-byte ADDRESS64 fields, then fields DbgHelp fills; 264 bytes in total.
    private const int StackFrameSize = 512;
    private const int AddrPcOffset = 0;
    private const int AddrFrameOffset = 32;
    private const int AddrStackOffset = 48;
    private const int AddressModeOffset = 12;
    private const int AddressModeFlat = 3;
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly AutoResetEvent Requested = new(false);

    private static readonly FunctionTableAccess FunctionTable = (process, address) => RtlLookupFunctionEntry(address, out _, IntPtr.Zero);
    private static readonly GetModuleBase ModuleBase = (process, address) =>
        RtlLookupFunctionEntry(address, out var imageBase, IntPtr.Zero) != IntPtr.Zero ? imageBase : 0;
    private static Thread? s_worker;
    private static Request? s_pending;

    // Each request owns its result, so a timed-out walk that finishes later cannot answer a newer one.
    private sealed class Request(uint threadId)
    {
        public uint ThreadId { get; } = threadId;
        public string Result = "unavailable: stack walk did not finish";
        public readonly ManualResetEventSlim Done = new();
    }

    /// <summary>Starts the capture worker without waiting. Call before the watched thread can stall.</summary>
    internal static void Start()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        lock (Gate)
        {
            if (s_worker is not null)
                return;
            // The worker warms every path (P/Invokes, dbghelp, JIT) on the caller's stack before serving,
            // so a later capture only reuses loaded code. The caller does not wait for it; a capture
            // requested meanwhile is served after the warm-up.
            var caller = GetCurrentThreadId();
            s_worker = new Thread(() =>
            {
                SafeWalk(caller);
                Serve();
            })
            {
                IsBackground = true,
                Name = "NativeThreadStackProbe",
            };
            s_worker.Start();
        }
    }

    /// <summary>Returns the thread's frames, innermost first, or an "unavailable: ..." reason.</summary>
    internal static string Capture(uint nativeThreadId)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "unavailable: x64 only";
        if (nativeThreadId == 0)
            return "unavailable: thread not started";
        lock (Gate)
        {
            if (s_worker is null)
                return "unavailable: probe not started";
            var request = new Request(nativeThreadId);
            Volatile.Write(ref s_pending, request);
            Requested.Set();
            return request.Done.Wait(WalkTimeout) ? Volatile.Read(ref request.Result) : "unavailable: stack walk did not finish";
        }
    }

    private static void Serve()
    {
        while (true)
        {
            Requested.WaitOne();
            if (Interlocked.Exchange(ref s_pending, null) is not { } request)
                continue;
            Volatile.Write(ref request.Result, SafeWalk(request.ThreadId));
            request.Done.Set();
        }
    }

    private static string SafeWalk(uint nativeThreadId)
    {
        try { return Walk(nativeThreadId); }
        catch (Exception ex) { return $"unavailable: {ex.GetType().Name}"; }
    }

    private static string Walk(uint nativeThreadId)
    {
        var context = Marshal.AllocHGlobal(ContextSize + 16);
        var frame = Marshal.AllocHGlobal(StackFrameSize);
        var thread = IntPtr.Zero;
        try
        {
            var aligned = (IntPtr)(((long)context + 15) & ~15L); // CONTEXT requires 16-byte alignment.
            var failure = CaptureContext(nativeThreadId, aligned);
            if (failure is not null)
                return failure;
            thread = OpenThread(ThreadQueryLimitedInformation, false, nativeThreadId);
            if (thread == IntPtr.Zero)
                return $"unavailable: OpenThread error {Marshal.GetLastWin32Error()}";
            Marshal.Copy(new byte[StackFrameSize], 0, frame, StackFrameSize);
            SetAddress(frame, AddrPcOffset, Marshal.ReadInt64(aligned, RipOffset));
            SetAddress(frame, AddrFrameOffset, Marshal.ReadInt64(aligned, RbpOffset));
            SetAddress(frame, AddrStackOffset, Marshal.ReadInt64(aligned, RspOffset));
            var modules = SnapshotModules();
            var frames = new List<string>();
            while (frames.Count < MaxFrames &&
                StackWalk64(MachineAmd64, GetCurrentProcess(), thread, frame, aligned, IntPtr.Zero, FunctionTable, ModuleBase, IntPtr.Zero))
            {
                var pc = (ulong)Marshal.ReadInt64(frame, AddrPcOffset);
                if (pc == 0)
                    break;
                frames.Add(Describe(pc, modules));
            }
            return frames.Count == 0 ? "unavailable: no frames" : string.Join(" <- ", frames);
        }
        finally
        {
            if (thread != IntPtr.Zero)
                CloseHandle(thread);
            Marshal.FreeHGlobal(frame);
            Marshal.FreeHGlobal(context);
        }
    }

    private static void SetAddress(IntPtr frame, int offset, long value)
    {
        Marshal.WriteInt64(frame, offset, value);
        Marshal.WriteInt32(frame, offset + AddressModeOffset, AddressModeFlat);
    }

    /// <summary>Copies the thread's snapshot CONTEXT into <paramref name="context"/>; returns a failure reason or null.</summary>
    private static string? CaptureContext(uint nativeThreadId, IntPtr context)
    {
        var error = PssCaptureSnapshot(GetCurrentProcess(), CaptureThreads | CaptureThreadContext, ContextFull, out var snapshot);
        if (error != 0)
            return $"unavailable: PssCaptureSnapshot error {error}";
        var entry = Marshal.AllocHGlobal(EntrySize);
        var marker = IntPtr.Zero;
        try
        {
            error = PssWalkMarkerCreate(IntPtr.Zero, out marker);
            while (error == 0 && (error = PssWalkSnapshot(snapshot, WalkThreads, marker, entry, EntrySize)) == 0)
            {
                if ((uint)Marshal.ReadInt32(entry, EntryThreadIdOffset) != nativeThreadId)
                    continue;
                var record = Marshal.ReadIntPtr(entry, EntryContextOffset);
                if (record == IntPtr.Zero || (ushort)Marshal.ReadInt16(entry, EntryContextSizeOffset) < ContextSize)
                    return "unavailable: snapshot has no thread context";
                var bytes = new byte[ContextSize];
                Marshal.Copy(record, bytes, 0, ContextSize);
                Marshal.Copy(bytes, 0, context, ContextSize);
                return null;
            }
            return error == ErrorNoMoreItems ? "unavailable: thread not in snapshot" : $"unavailable: PssWalkSnapshot error {error}";
        }
        finally
        {
            if (marker != IntPtr.Zero)
                PssWalkMarkerFree(marker);
            Marshal.FreeHGlobal(entry);
            PssFreeSnapshot(GetCurrentProcess(), snapshot);
        }
    }

    private static (string Name, ulong Base, ulong End)[] SnapshotModules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Select(m => (Path.GetFileName(m.FileName), (ulong)m.BaseAddress, (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize))
            .ToArray();
    }

    private static string Describe(ulong address, (string Name, ulong Base, ulong End)[] modules)
    {
        foreach (var module in modules)
        {
            if (address >= module.Base && address < module.End)
                return $"{module.Name}+0x{address - module.Base:X}";
        }
        return "<jit-or-unmapped>";
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr FunctionTableAccess(IntPtr process, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong GetModuleBase(IntPtr process, ulong address);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern int PssCaptureSnapshot(IntPtr process, uint captureFlags, uint threadContextFlags, out IntPtr snapshot);

    [DllImport("kernel32.dll")]
    private static extern int PssFreeSnapshot(IntPtr process, IntPtr snapshot);

    [DllImport("kernel32.dll")]
    private static extern int PssWalkMarkerCreate(IntPtr allocator, out IntPtr marker);

    [DllImport("kernel32.dll")]
    private static extern int PssWalkMarkerFree(IntPtr marker);

    [DllImport("kernel32.dll")]
    private static extern int PssWalkSnapshot(IntPtr snapshot, int informationClass, IntPtr marker, IntPtr buffer, int bufferLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StackWalk64(uint machineType, IntPtr process, IntPtr thread, IntPtr stackFrame, IntPtr contextRecord,
        IntPtr readMemoryRoutine, FunctionTableAccess functionTableAccessRoutine, GetModuleBase getModuleBaseRoutine,
        IntPtr translateAddress);
}
