using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Owned native handle for one EXACT target (process + thread). Its lifetime is the physical native worker
/// lifetime: it is disposed in the worker finally, never before physical drain.
/// </summary>
internal interface IExactTargetHandle : IDisposable
{
    /// <summary>True when the SAME held handles still match the exact target (pid + raw birth + thread binding).</summary>
    bool IsStillExact();
}

/// <summary>
/// Testable NATIVE OPERATION seam for <see cref="WindowsUiThreadStackSource"/>. The DEFAULT implementation is the
/// real Windows API (Wait Chain Traversal + process/thread handles); portable tests inject a gated implementation
/// so the ACTUAL source class logic (admission / timeout / release / cross-instance ownership) runs WITHOUT
/// calling Windows or touching any private app.
/// </summary>
internal interface IWindowsThreadStackNativeOperations
{
    bool TryOpenVerified(UiThreadTarget target, out IExactTargetHandle? handle, out string? failure);

    bool TryCaptureWaitChain(
        IExactTargetHandle handle, int maxNodes,
        out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure);

    bool TryCaptureStack(
        IExactTargetHandle handle, int maxFrames,
        out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure);
}

/// <summary>
/// WINDOWS native source for the bounded UI-thread stack + wait-chain collector (compiled by the WinUI project;
/// also linked into portable tests so the ACTUAL admission/timeout/release logic is exercised with a fake native
/// seam). Read-only queries only, EXTERNAL to the target UI thread.
/// <para>
/// SHARED EXACT-TARGET PHYSICAL OWNERSHIP: before launching a native worker, this class takes a process-local
/// ownership for the exact target identity (pid + raw FILETIME creation ticks + thread id). It is held until the
/// NATIVE WORKER (not merely this call) finishes - so a timeout returns a receipt while the ownership stays held,
/// and a FRESH source instance pointed at the same target is refused rather than launching another native worker.
/// The stripe table is FIXED-SIZE (bounded bookkeeping). This is a PROCESS-LOCAL bound: it does NOT exclude a
/// separate external host process.
/// </para>
/// <para>
/// SDK ABI (verified against pinned wct.h 10.0.28000.2705): WCT_MAX_NODE_COUNT 16; WctThreadType 8; the node
/// union is read as ThreadObject ONLY for WctThreadType; the status enum order is 1 NoAccess, 2 Running,
/// 3 Blocked, 4 PidOnly, 5 PidOnlyRpcss, 6 Owned, 7 NotOwned, 8 Abandoned, 9 Unknown, 10 Error; the node stride
/// is 280 bytes (ObjectType + ObjectStatus + the 272-byte union). Object names are never read or emitted.
/// </para>
/// <para>
/// EXPLICIT STACK CAPABILITY GAP: bounded stack symbolization needs a brief suspend + DbgHelp in an approved
/// diagnostic host; until then the stack operation returns Unavailable and never leaves the target suspended.
/// No raw/heap/full-memory dump; no target kill/restart/injection.
/// </para>
/// </summary>
internal sealed class WindowsUiThreadStackSource : IUiThreadStackSource
{
    private const int PhysicalOwnershipStripes = 64;

    // PROCESS-LOCAL shared exact-target physical ownership, held from before the native worker launches until its
    // finally. Static so separate source INSTANCES targeting the same pid+birth share it. Bounded memory.
    private static readonly SemaphoreSlim[] PhysicalOwnership =
        Enumerable.Range(0, PhysicalOwnershipStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    // Deterministic idle signal for the physical native workers (no sleeps). The COUNT and the SIGNAL are their
    // own seam under ONE gate: a decrement-to-zero Set can never be reordered after a fresh increment-to-one
    // Reset, so a stale signal cannot report false idle while a worker is running.
    private static readonly ManualResetEventSlim PhysicalIdle = new(true);
    private static readonly object PhysicalGate = new();
    private static int _physicalWorkers;

    internal static int PhysicalWorkers { get { lock (PhysicalGate) return _physicalWorkers; } }

    internal static bool WaitForPhysicalIdle(TimeSpan grace)
    {
        try { return PhysicalIdle.Wait(grace); }
        catch (ObjectDisposedException) { return true; }
    }

    private static void EnterPhysical()
    {
        lock (PhysicalGate)
        {
            if (++_physicalWorkers == 1) PhysicalIdle.Reset();
        }
    }

    private static void ExitPhysical()
    {
        lock (PhysicalGate)
        {
            if (_physicalWorkers > 0 && --_physicalWorkers == 0) PhysicalIdle.Set();
        }
    }

    private readonly IWindowsThreadStackNativeOperations _operations;

    public WindowsUiThreadStackSource() : this(WindowsThreadStackNativeOperations.Instance) { }

    internal WindowsUiThreadStackSource(IWindowsThreadStackNativeOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    internal static int StripeFor(UiThreadTarget target)
    {
        var identity = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{target.ProcessId}:{target.ProcessBirthTimeUtcTicks}:{target.ThreadId}");
        var hash = 0;
        foreach (var c in identity) hash = unchecked(hash * 31 + c);
        return (hash & int.MaxValue) % PhysicalOwnershipStripes;
    }

    public bool TryRevalidate(UiThreadTarget target, out string? failure)
    {
        if (!_operations.TryOpenVerified(target, out var handle, out failure))
            return false;
        handle!.Dispose();   // a validated identity must not leak its process/thread handles
        return true;
    }

    public bool TryCaptureWaitChain(
        UiThreadTarget target, int maxNodes, TimeSpan deadline,
        out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
    {
        nodes = Array.Empty<WaitChainNodeView>();
        truncated = false;
        failure = null;

        if (!_operations.TryOpenVerified(target, out var handle, out failure))
            return false;

        var stripe = StripeFor(target);
        // SHARED ownership BEFORE launching the native worker: a competing instance is refused, not queued.
        if (!PhysicalOwnership[stripe].Wait(TimeSpan.Zero))
        {
            handle!.Dispose();
            failure = "native physical ownership busy for this exact target";
            return false;
        }

        return RunOwnedNative(handle!, stripe, deadline,
            (IExactTargetHandle h, out IReadOnlyList<WaitChainNodeView> views, out bool trunc, out string? fail) => _operations.TryCaptureWaitChain(h, Math.Clamp(maxNodes, 1, 16), out views, out trunc, out fail),
            out nodes, out truncated, out failure);
    }

    public bool TryCaptureStack(
        UiThreadTarget target, int maxFrames, TimeSpan deadline,
        out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
    {
        frames = Array.Empty<StackFrameView>();
        truncated = false;
        failure = null;

        if (!_operations.TryOpenVerified(target, out var handle, out failure))
            return false;

        var stripe = StripeFor(target);
        if (!PhysicalOwnership[stripe].Wait(TimeSpan.Zero))
        {
            handle!.Dispose();
            failure = "native physical ownership busy for this exact target";
            return false;
        }

        return RunOwnedNative(handle!, stripe, deadline,
            (IExactTargetHandle h, out IReadOnlyList<StackFrameView> views, out bool trunc, out string? fail) => _operations.TryCaptureStack(h, Math.Clamp(maxFrames, 1, 64), out views, out trunc, out fail),
            out frames, out truncated, out failure);
    }

    private static bool RunOwnedNative<T>(
        IExactTargetHandle handle, int stripe, TimeSpan deadline,
        NativeCapture<T> capture, out IReadOnlyList<T> views, out bool truncated, out string? failure)
    {
        views = Array.Empty<T>();
        truncated = false;
        failure = null;
        IReadOnlyList<T> captured = Array.Empty<T>();
        var capturedTruncated = false;
        var identityStable = false;
        string? captureFailure = null;
        var ok = false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The NATIVE WORKER owns the handle AND the ownership lease for its whole physical lifetime; it disposes
        // both in its finally, so a caller timeout never releases a handle the worker still uses.
        EnterPhysical();
        CaptureDrain.Register();   // native workers are part of the host drain, not only the outer worker
        _ = Task.Run(() =>
        {
            try
            {
                ok = capture(handle, out captured, out capturedTruncated, out captureFailure);
                identityStable = ok && handle.IsStillExact();
            }
            catch (Exception exception)
            {
                captureFailure = $"native capture threw {exception.GetType().Name}";
            }
            finally
            {
                // Cleanup is ORDERED and individually guarded: a throwing Dispose must never skip releasing the
                // ownership lease, the physical count, the drain registration, or the completion signal.
                try { handle.Dispose(); } catch (Exception e) { captureFailure ??= $"handle dispose threw {e.GetType().Name}"; }
                try { PhysicalOwnership[stripe].Release(); } catch (Exception e) { captureFailure ??= $"lease release threw {e.GetType().Name}"; }
                try { ExitPhysical(); } catch (Exception e) { captureFailure ??= $"physical exit threw {e.GetType().Name}"; }
                try { CaptureDrain.Complete(); } catch { /* observed elsewhere */ }
                completion.TrySetResult(true);
            }
        });

        if (!completion.Task.Wait(deadline))
        {
            failure = "native capture exceeded the deadline (owned worker still draining)";
            return false;   // ownership stays held by the draining worker; no late publish
        }
        if (!ok)
        {
            failure = captureFailure ?? "native capture failed";
            return false;
        }
        if (!identityStable)
        {
            failure = "target identity changed during native capture (pid/thread reused)";
            return false;
        }
        views = captured;
        truncated = capturedTruncated;
        return true;
    }

    private delegate bool NativeCapture<T>(
        IExactTargetHandle handle, out IReadOnlyList<T> views, out bool truncated, out string? failure);
}

/// <summary>
/// DEFAULT real Windows implementation of the native operation seam. All Wait Chain Traversal / handle work lives
/// here; portable tests never call it. Struct/enum grounding matches the pinned wct.h ABI.
/// </summary>
internal sealed class WindowsThreadStackNativeOperations : IWindowsThreadStackNativeOperations
{
    public static readonly WindowsThreadStackNativeOperations Instance = new();

    private const int WctMaxNodeCount = 16;   // SDK WCT_MAX_NODE_COUNT
    private const int WctThreadType = 8;      // SDK WCT_OBJECT_TYPE.WctThreadType
    private const int WctStatusBlocked = 3;   // SDK WCT_OBJECT_STATUS.WctStatusBlocked
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    private const uint WCT_OUT_OF_PROC_FLAG = 0x00000001;

    private WindowsThreadStackNativeOperations() { }

    public bool TryOpenVerified(UiThreadTarget target, out IExactTargetHandle? handle, out string? failure)
    {
        handle = null;
        failure = null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)target.ProcessId);
        if (process == IntPtr.Zero)
        {
            failure = $"target pid {target.ProcessId} could not be opened (gone or access denied)";
            return false;
        }
        var thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, (uint)target.ThreadId);
        if (thread == IntPtr.Zero)
        {
            CloseHandle(process);
            failure = $"ui thread {target.ThreadId} could not be opened";
            return false;
        }
        var candidate = new ExactTargetHandle(target, process, thread);
        if (!candidate.IsStillExact())
        {
            candidate.Dispose();
            failure = $"pid {target.ProcessId} birth time differs or thread {target.ThreadId} is not its own (pid reused)";
            return false;
        }
        handle = candidate;
        return true;
    }

    public bool TryCaptureWaitChain(
        IExactTargetHandle handle, int maxNodes,
        out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
    {
        nodes = Array.Empty<WaitChainNodeView>();
        truncated = false;
        failure = null;

        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero)
        {
            failure = "Wait Chain Traversal session could not be opened";
            return false;
        }
        try
        {
            var capacity = Math.Clamp(maxNodes, 1, WctMaxNodeCount);
            var buffer = new WctNode[capacity];
            var count = (uint)capacity;
            if (!GetThreadWaitChain(session, IntPtr.Zero, WCT_OUT_OF_PROC_FLAG, (uint)ThreadIdOf(handle), ref count, buffer, out var isCycle))
            {
                failure = $"GetThreadWaitChain failed (win32={Marshal.GetLastWin32Error()})";
                return false;
            }

            var collected = new List<WaitChainNodeView>((int)count);
            for (var i = 0; i < (int)count && i < capacity; i++)
            {
                var node = buffer[i];
                var isThread = node.ObjectType == WctThreadType;   // only thread nodes expose ThreadObject ids
                collected.Add(new WaitChainNodeView(
                    isThread ? (int)node.ThreadId : -1,
                    isThread ? (int)node.ProcessId : -1,
                    DescribeStatus(node.ObjectStatus),
                    null,
                    isCycle && node.ObjectStatus == WctStatusBlocked ? "cycle" : null));
            }
            nodes = collected;
            truncated = count >= capacity;
            return true;
        }
        finally
        {
            CloseThreadWaitChainSession(session);
        }
    }

    // Bounded native stack capture: x64 only; suspend the EXACT target briefly, copy a bounded stack window,
    // RESUME BEFORE any (potentially slow) symbolization, then walk the copied frame chain and symbolize LOCALLY
    // with DbgHelp (globally serialized, no symbol server, no prompts). Only module!function+offset is emitted -
    // never registers, raw stack memory, heap, or a full-memory dump.
    // Real suspension stays GATED until owned-suspension safety is qualified (see CaptureResumeObligations).
    private static readonly bool RealSuspensionEnabled = false;   // runtime gate: real suspension not exposed
    private const int MaxStackBytes = 64 * 1024;   // bounded copied window
    // Least-rights handles for the GATED path: suspend/resume + context + bounded VM read on the EXACT target only.
    private const uint THREAD_SUSPEND_RESUME = 0x0002;
    private const uint THREAD_GET_CONTEXT = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private static readonly object DbgHelpGate = new();
    private static readonly Dictionary<IntPtr, bool> DbgHelpInitialized = new();

    public bool TryCaptureStack(
        IExactTargetHandle handle, int maxFrames,
        out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
    {
        frames = Array.Empty<StackFrameView>();
        truncated = false;
        failure = null;

        // GATED: owned-suspension safety (exact cleanup ownership across process lifetime) is NOT yet qualified, so
        // REAL suspension is not exposed. No native suspension/context/memory read is performed.
        if (!RealSuspensionEnabled)
        {
            failure = "real suspension gated unavailable until owned-suspension safety is qualified; no native suspension performed";
            return false;
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            failure = $"stack capture unsupported on {RuntimeInformation.ProcessArchitecture}; x64 only";
            return false;
        }
        if (handle is not ExactTargetHandle exact)
        {
            failure = "stack capture requires the real exact-target handle";
            return false;
        }

        var frameLimit = Math.Clamp(maxFrames, 1, 64);
        ulong rip, rbp, rsp;
        var stackCopy = new byte[MaxStackBytes];
        var copied = 0;

        // OWNED suspension: reserve BEFORE suspending, promote ONLY on success, release ONLY after a confirmed
        // resume. A DWORD_MAX suspend failure disposes the reservation (no owned suspension exists).
        var reservation = CaptureResumeObligations.TryReserve();
        if (reservation is null)
        {
            failure = "an owned suspension is already unresolved; refusing to start another";
            return false;
        }
        var suspended = SuspendThread(exact.ThreadHandle);
        if (suspended == unchecked((uint)-1))
        {
            CaptureResumeObligations.ReleaseReservationOnly(reservation);
            failure = "SuspendThread failed (DWORD_MAX); target not suspended";
            return false;
        }
        CaptureResumeObligations.PromoteToOwned(reservation);
        var resumed = false;
        try
        {
            try
            {
                var context = default(ContextX64);
                context.ContextFlags = ContextFull;
                if (!GetThreadContext(exact.ThreadHandle, ref context))
                {
                    failure = "GetThreadContext failed";
                    return false;
                }
                rip = context.Rip;
                rbp = context.Rbp;
                rsp = context.Rsp;
                if (!ReadProcessMemory(exact.ProcessHandle, (IntPtr)rsp, stackCopy, stackCopy.Length, out var read))
                    copied = 0;
                else
                    copied = Math.Min((int)read.ToInt64(), stackCopy.Length);
            }
            finally
            {
                resumed = ResumeThread(exact.ThreadHandle) != unchecked((uint)-1);
            }
        }
        finally
        {
            if (resumed) CaptureResumeObligations.ReleaseOwnedAfterConfirmedResume(reservation);
            // NOT resumed => the obligation STAYS visible so the host lifetime cannot exit silently.
        }
        if (!resumed)
        {
            failure = "ResumeThread failed (DWORD_MAX); target may still be suspended (resume obligation outstanding)";
            return false;
        }

        // Symbolization happens AFTER the resume, over the COPIED window only.
        var collected = new List<StackFrameView>(frameLimit);
        var windowBase = rsp;
        var windowEnd = windowBase + (ulong)copied;
        lock (DbgHelpGate)
        {
            EnsureDbgHelp(exact.ProcessHandle);
            collected.Add(Symbolize(exact.ProcessHandle, rip));
            var frameRbp = rbp;
            for (var i = 1; i < frameLimit; i++)
            {
                if (frameRbp < windowBase || frameRbp + 16 > windowEnd) break;
                var nextRbp = BitConverter.ToUInt64(stackCopy, (int)(frameRbp - windowBase));
                var returnAddress = BitConverter.ToUInt64(stackCopy, (int)(frameRbp - windowBase + 8));
                if (returnAddress == 0 || nextRbp <= frameRbp) break;
                collected.Add(Symbolize(exact.ProcessHandle, returnAddress));
                frameRbp = nextRbp;
            }
        }
        frames = collected;
        truncated = collected.Count >= frameLimit;
        return true;
    }

    private static void EnsureDbgHelp(IntPtr process)
    {
        if (DbgHelpInitialized.ContainsKey(process)) return;
        SymSetOptions(SymoptUndname | SymoptDeferredLoads | SymoptNoPrompts);
        // SearchPath null => LOCAL default search only; no symbol server, no network, no prompt.
        DbgHelpInitialized[process] = SymInitialize(process, null, false);
    }

    private static StackFrameView Symbolize(IntPtr process, ulong address)
    {
        var moduleName = "unknown";
        var functionName = "0x" + address.ToString("x");
        var module = default(ImagehlpModuleW64);
        module.SizeOfStruct = 3264;   // SDK sizeof(IMAGEHLP_MODULEW64); set BEFORE the call
        // Inline ByValTStr buffers must be non-null before the P/Invoke.
        module.ModuleName = string.Empty;
        module.ImageName = string.Empty;
        module.LoadedImageName = string.Empty;
        module.LoadedPdbName = string.Empty;
        if (SymGetModuleInfo64W(process, address, ref module) && module.LoadedImageName is { Length: > 0 })
        {
            var leaf = new string(module.LoadedImageName).TrimEnd('\0').Replace('/', '\\');
            var cut = leaf.LastIndexOf('\\');
            moduleName = cut >= 0 ? leaf[(cut + 1)..] : leaf;
        }

        var buffer = new byte[512];
        var offset = (uint)Marshal.OffsetOf<SymbolInfo>(nameof(SymbolInfo.Name));
        var info = default(SymbolInfo);
        // SDK sizeof(SYMBOL_INFOW) = 88 (includes WCHAR Name[1] + alignment padding); the extra name capacity
        info.SizeOfStruct = 88;   // SDK sizeof(SYMBOL_INFOW); MaxNameLen governs the inline buffer below
        info.MaxNameLen = 255;
        if (SymFromAddrW(process, address, out var displacement, ref info))
        {
            functionName = info.Name + "+0x" + displacement.ToString("x");
        }
        _ = buffer;
        _ = offset;
        return new StackFrameView(moduleName, functionName);
    }

    private const uint ContextFull = 0x0010000B;   // CONTEXT_AMD64 | CONTROL | INTEGER | FLOATING_POINT
    private const uint SymoptUndname = 0x00000002;
    private const uint SymoptDeferredLoads = 0x00000004;
    private const uint SymoptNoPrompts = 0x00080000;

    [StructLayout(LayoutKind.Explicit, Size = 1232)]
    private struct ContextX64
    {
        [FieldOffset(0x30)] public uint ContextFlags;
        [FieldOffset(0x98)] public ulong Rsp;
        [FieldOffset(0xA0)] public ulong Rbp;
        [FieldOffset(0xF8)] public ulong Rip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SymbolInfo
    {
        public uint SizeOfStruct;
        public uint TypeIndex;
        public ulong Reserved0;
        public ulong Reserved1;
        public uint Index;
        public uint Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value;
        public ulong Address;
        public uint Register;
        public uint Scope;
        public uint Tag;
        public uint NameLen;
        public uint MaxNameLen;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;
    }

    // SDK IMAGEHLP_MODULEW64 (grounded in the retained SDK header): sizeof 3264; BaseOfImage@8 (4 bytes padding
    // after SizeOfStruct), ImageSize@16, TimeDateStamp@20, CheckSum@24, NumSyms@28, SymType@32, ModuleName@36
    // (32 WCHARs), ImageName@100 (256), LoadedImageName@612 (256), LoadedPdbName@1124 (256). Sequential Unicode
    // with inline ByValTStr buffers (NOT managed references) and an explicit trailing Size so sizeof == 3264.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 3264)]
    private struct ImagehlpModuleW64
    {
        public uint SizeOfStruct;        // @0
        public ulong BaseOfImage;        // @8 (4 bytes padding after SizeOfStruct)
        public uint ImageSize;           // @16
        public uint TimeDateStamp;       // @20
        public uint CheckSum;            // @24
        public uint NumSyms;             // @28
        public uint SymType;             // @32
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ModuleName;         // @36
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ImageName;         // @100
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LoadedImageName;   // @612
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LoadedPdbName;     // @1124
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr thread, ref ContextX64 context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr baseAddress, [Out] byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SymInitialize(IntPtr process, string? searchPath, bool invadeProcess);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SymGetModuleInfo64W(IntPtr process, ulong address, ref ImagehlpModuleW64 module);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SymFromAddrW(IntPtr process, ulong address, out ulong displacement, ref SymbolInfo symbol);

    private static uint ThreadIdOf(IExactTargetHandle handle) =>
        handle is ExactTargetHandle exact ? (uint)exact.Target.ThreadId : 0u;

    private static string DescribeStatus(int status) => status switch   // SDK WCT_OBJECT_STATUS order
    {
        1 => "NoAccess", 2 => "Running", 3 => "Blocked", 4 => "PidOnly", 5 => "PidOnlyRpcss",
        6 => "Owned", 7 => "NotOwned", 8 => "Abandoned", 9 => "Unknown", 10 => "Error",
        _ => "Status" + status,
    };

    private sealed class ExactTargetHandle : IExactTargetHandle
    {
        private readonly IntPtr _process;
        private readonly IntPtr _thread;
        private bool _disposed;

        public ExactTargetHandle(UiThreadTarget target, IntPtr process, IntPtr thread)
        {
            Target = target;
            _process = process;
            _thread = thread;
        }

        public UiThreadTarget Target { get; }

        internal IntPtr ThreadHandle => _thread;
        internal IntPtr ProcessHandle => _process;

        public bool IsStillExact()
        {
            if (!GetProcessTimes(_process, out var creation, out _, out _, out _)) return false;
            var birth = unchecked((long)((ulong)creation.High << 32 | creation.Low));   // raw FILETIME ticks
            if (birth != Target.ProcessBirthTimeUtcTicks) return false;
            return GetProcessIdOfThread(_thread) == (uint)Target.ProcessId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_process != IntPtr.Zero) CloseHandle(_process);
            if (_thread != IntPtr.Zero) CloseHandle(_thread);
        }
    }

    // WAITCHAIN_NODE_INFO: ObjectType + ObjectStatus then a 272-byte union => stride 280 (SDK ABI).
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    private struct WctNode
    {
        [FieldOffset(0)] public int ObjectType;
        [FieldOffset(4)] public int ObjectStatus;
        [FieldOffset(8)] public uint ProcessId;
        [FieldOffset(12)] public uint ThreadId;
        [FieldOffset(16)] public uint WaitTime;
        [FieldOffset(20)] public uint ContextSwitches;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CloseThreadWaitChainSession(IntPtr session);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetThreadWaitChain(
        IntPtr session, IntPtr context, uint flags, uint threadId,
        ref uint nodeCount, [Out] WctNode[] nodes, out bool isCycle);
}