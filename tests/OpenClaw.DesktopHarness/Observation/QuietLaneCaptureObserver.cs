#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Passive fresh-guest proof observer. Never closes, hides, activates or switches a window.
public sealed class QuietLaneCaptureObserver : IDisposable
{
    public readonly ConcurrentQueue<Dictionary<string, object>> Records =
        new ConcurrentQueue<Dictionary<string, object>>();
    public readonly AutoResetEvent Changed = new AutoResetEvent(false);
    public readonly ManualResetEvent Ready = new ManualResetEvent(false);
    private readonly EventWaitHandle stopCases;
    private readonly int intervalMs, maximumGapMs;
    private readonly Thread worker;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, bool> ownedDesktops =
        new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private readonly WinEventProc callback;
    private volatile bool stopping, failed, executionBlocked;
    private readonly AttributionContinuationPolicy attributionContinuation;
    private readonly ConcurrentDictionary<string, bool> previouslyOwnedWindows =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private readonly bool countEveryEligibleShow;
    private long lastProbeTicks;
    private uint workerId;
    private readonly QuietCaptureLedger capture = new QuietCaptureLedger();
    private readonly ObserverCallbackContext callbackContext = new ObserverCallbackContext();
    public void TrackProcess(uint pid, string startedUtc) { capture.TrackProcess(pid, startedUtc); }
    public bool Failed { get { return failed; } }
    public bool ExecutionBlocked { get { return executionBlocked; } }
    public int AttributionLimitations { get { return attributionContinuation.Limitations; } }
    public double LastProbeAgeMs
    {
        get { return (clock.ElapsedTicks - Interlocked.Read(ref lastProbeTicks)) * 1000.0 / Stopwatch.Frequency; }
    }
    public QuietLaneCaptureObserver(EventWaitHandle stopCases, int intervalMs, int maximumGapMs)
        : this(stopCases, intervalMs, maximumGapMs, false) { }
    public QuietLaneCaptureObserver(EventWaitHandle stopCases, int intervalMs, int maximumGapMs,
        bool explicitlyAuthorizeSingleUnattributableShow)
        : this(stopCases, intervalMs, maximumGapMs, explicitlyAuthorizeSingleUnattributableShow, false) { }
    public QuietLaneCaptureObserver(EventWaitHandle stopCases, int intervalMs, int maximumGapMs,
        bool explicitlyAuthorizeUnattributableShow, bool countEveryEligibleShow)
    {
        if (intervalMs < 50 || maximumGapMs <= intervalMs)
            throw new ArgumentException("Invalid observer cadence.");
        this.stopCases = stopCases;
        attributionContinuation = new AttributionContinuationPolicy(explicitlyAuthorizeUnattributableShow, countEveryEligibleShow);
        this.countEveryEligibleShow = countEveryEligibleShow;
        this.intervalMs = intervalMs;
        this.maximumGapMs = maximumGapMs;
        callback = OnEvent;
        worker = new Thread(Run) { IsBackground = true, Name = "Quiet lane passive observer" };
    }
    public void Start() { worker.Start(); }
    public void TrackDesktop(string name, bool rootAlive)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
            throw new ArgumentException("Only the recorded leaf desktop name is accepted.");
        ownedDesktops[name] = rootAlive;
    }
    public void Fail(string reason)
    {
        failed = true;
        executionBlocked = true;
        stopCases.Set();
        Emit("lane-failure", new Dictionary<string, object> { ["reason"] = reason });
    }
    private void Emit(string kind, Dictionary<string, object> fields)
    {
        fields["kind"] = kind;
        fields["observedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        fields["monotonicMs"] = clock.Elapsed.TotalMilliseconds;
        Records.Enqueue(fields);
        Changed.Set();
        if (Records.Count > 4096) { failed = true; executionBlocked = true; stopCases.Set(); }
    }
    private void ProbeError(string operation, int error)
    {
        failed = true;
        executionBlocked = true;
        stopCases.Set();
        Emit("probe-error", new Dictionary<string, object> { ["operation"] = operation, ["win32Error"] = error });
    }
    private string ObjectName(IntPtr handle)
    {
        StringBuilder name = new StringBuilder(512);
        uint needed;
        if (!GetUserObjectInformationW(handle, 2, name, 1024, out needed))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    private void ObserveWindow(IntPtr window, string origin, bool dialogEvent, uint eventThread, uint eventTime, long callbackSequence = 0, int? objectId = null, int? childId = null, WindowFirstSnapshot first = null, ThreadOwnershipAttempt originalAtEntry = null)
    {
        first = first ?? WindowFirstCapture.Capture(new WindowFirstNative(), window, () => Environment.TickCount64);
        bool validBefore = first.ValidBefore;
        bool visible = first.Visible;
        if (!visible && !dialogEvent && !origin.StartsWith("WinEvent-", StringComparison.Ordinal)) return;
        StringBuilder className = new StringBuilder(first.ClassName ?? "");
        long classQueryBeganTickCount64 = first.ClassBegin;
        int classLength = first.ClassLength;
        int classError = first.ClassError;
        long classQueryEndedTickCount64 = first.ClassEnd;
        uint processId = first.OwnerPid;
        long ownerQueryBeganTickCount64 = first.OwnerBegin;
        uint windowThread = first.OwnerThread;
        int windowOwnerError = first.OwnerError;
        long ownerQueryEndedTickCount64 = first.OwnerEnd;
        uint hwndProcessId = processId, threadProcessId = 0;
        long threadCreated = 0, threadExit = 0, threadKernel, threadUser;
        int openThreadError = 0, threadOwnerError = 0, threadTimesError = 0;
        bool threadTimesOk = false;
        bool eventThreadOpened = false;
        var retryTicks = new Dictionary<string, long>();
        ThreadOwnershipAttempt originalAttempt = null;
        if (processId == 0 && eventThread != 0)
        {
            originalAttempt = originalAtEntry ?? ThreadOwnershipProbe.Query(new ObserverThreadNative(), eventThread);
            processId = originalAttempt.OwnerPid;
        }
        string processName = null, ownerError = null;
        if (processId != 0)
        {
            try { using (Process p = Process.GetProcessById((int)processId)) processName = p.ProcessName; }
            catch (Exception ex) { ownerError = ex.GetType().Name; }
        }
        bool wer = string.Equals(processName, "WerFault", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "WerFaultSecure", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "wermgr", StringComparison.OrdinalIgnoreCase);
        bool modal = dialogEvent || className.ToString() == "#32770" || wer;
        // Extra queries follow the original classification queries, never replace their fallback.
        if (eventThread != 0)
        {
            retryTicks["openBegin"] = Environment.TickCount64;
            IntPtr diagnosticThread = OpenThread(0x0800, false, eventThread);
            openThreadError = Marshal.GetLastWin32Error();
            retryTicks["openEnd"] = Environment.TickCount64;
            if (diagnosticThread != IntPtr.Zero)
            {
                eventThreadOpened = true;
                try
                {
                    retryTicks["ownerBegin"] = Environment.TickCount64;
                    threadProcessId = GetProcessIdOfThread(diagnosticThread);
                    threadOwnerError = Marshal.GetLastWin32Error();
                    retryTicks["ownerEnd"] = Environment.TickCount64;
                    retryTicks["timesBegin"] = Environment.TickCount64;
                    threadTimesOk = GetThreadTimes(diagnosticThread, out threadCreated, out threadExit, out threadKernel, out threadUser);
                    threadTimesError = Marshal.GetLastWin32Error();
                    retryTicks["timesEnd"] = Environment.TickCount64;
                }
                finally
                {
                    retryTicks["closeBegin"] = Environment.TickCount64;
                    if (!CloseHandle(diagnosticThread))
                        ProbeError("close-event-thread-capture", Marshal.GetLastWin32Error());
                    retryTicks["closeEnd"] = Environment.TickCount64;
                }
            }
        }
        string processCreatedUtc = null, processIdentityError = null;
        if (processId != 0)
        {
            try
            {
                using (Process identity = Process.GetProcessById((int)processId))
                    processCreatedUtc = identity.StartTime.ToUniversalTime().ToString("o");
            }
            catch (Exception ex) { processIdentityError = ex.GetType().Name; }
        }
        string eventProcessCreatedUtc = null, eventProcessIdentityError = null;
        if (threadProcessId != 0)
        {
            try
            {
                using (Process identity = Process.GetProcessById((int)threadProcessId))
                    eventProcessCreatedUtc = identity.StartTime.ToUniversalTime().ToString("o");
            }
            catch (Exception ex) { eventProcessIdentityError = ex.GetType().Name; }
        }
        Emit("native-classification-capture", new Dictionary<string, object> {
            ["callbackFirstWindowCapture"] = first.Fields,
            ["objectId"] = objectId, ["childId"] = childId,
            ["originalThreadLookup"] = originalAttempt == null ? null : originalAttempt.Fields(),
            ["retryThreadLookupLabel"] = "diagnostic-retry-not-original",
            ["retryThreadCallTickCount64"] = retryTicks,
            ["callbackSequence"] = callbackSequence,
            ["classQueryBeganTickCount64"] = classQueryBeganTickCount64,
            ["classQueryEndedTickCount64"] = classQueryEndedTickCount64,
            ["ownerQueryBeganTickCount64"] = ownerQueryBeganTickCount64,
            ["ownerQueryEndedTickCount64"] = ownerQueryEndedTickCount64,
            ["nativeCalls"] = QuietCaptureLedger.NativeCalls(classLength, classError, windowThread, windowOwnerError),
            ["window"] = QuietCaptureLedger.WindowKey(window), ["origin"] = origin,
            ["validBeforeQueries"] = validBefore, ["validAfterQueries"] = IsWindow(window),
            ["visibleAtQuery"] = visible, ["classLength"] = classLength,
            ["className"] = className.ToString(), ["classLastErrorRaw"] = classError,
            ["classLastErrorApplies"] = classLength == 0,
            ["windowThreadId"] = windowThread, ["hwndOwnerPid"] = hwndProcessId,
            ["windowOwnerLastErrorRaw"] = windowOwnerError, ["windowOwnerLastErrorApplies"] = windowThread == 0,
            ["eventThreadId"] = eventThread, ["eventThreadOwnerPid"] = threadProcessId,
            ["eventThreadOpened"] = eventThreadOpened,
            ["eventThreadIdentityIsCurrentQueryNotEventTimeProof"] = true,
            ["openEventThreadLastErrorRaw"] = openThreadError,
            ["eventThreadOwnerLastErrorRaw"] = threadOwnerError,
            ["eventThreadTimesSucceeded"] = threadTimesOk, ["eventThreadTimesLastErrorRaw"] = threadTimesError,
            ["eventThreadCreationFileTime"] = threadTimesOk ? (object)threadCreated : null,
            ["eventThreadExitFileTime"] = threadTimesOk ? (object)threadExit : null,
            ["eventThreadExitFileTimeRequiresIndependentTerminationEvidence"] = true,
            ["ownerPidAfterOriginalFallback"] = processId, ["ownerProcessCreatedUtc"] = processCreatedUtc,
            ["ownerIdentityError"] = processIdentityError,
            ["knownOwnedIdentity"] = capture.Correlate(processId, processCreatedUtc),
            ["eventThreadOwnerCreatedUtc"] = eventProcessCreatedUtc,
            ["eventThreadOwnerIdentityError"] = eventProcessIdentityError,
            ["eventThreadKnownOwnedIdentity"] = capture.Correlate(threadProcessId, eventProcessCreatedUtc),
            ["nativeEventMilliseconds"] = eventTime
        });
        string windowKey = QuietCaptureLedger.WindowKey(window);
        if (capture.Correlate(processId, processCreatedUtc) == "exact-pid-and-creation-time-match" ||
            capture.Correlate(threadProcessId, eventProcessCreatedUtc) == "exact-pid-and-creation-time-match")
            previouslyOwnedWindows[windowKey] = true;
        if (modal)
        {
            failed = true;
            executionBlocked = true;
            stopCases.Set();
            Emit("modal", new Dictionary<string, object> {
                ["origin"] = origin, ["window"] = "0x" + window.ToInt64().ToString("X"),
                ["windowClass"] = className.ToString(), ["visibleAtObservation"] = visible,
                ["dialogStartEvent"] = dialogEvent, ["ownerPid"] = processId,
                ["ownerProcessName"] = processName, ["ownerQueryError"] = ownerError,
                ["nativeEventMilliseconds"] = eventTime,
                ["dialogOwnerIsKillTarget"] = false
            });
        }
        else if (classLength == 0 || processId == 0 || ownerError != null)
        {
            Emit("observer-token-evidence-at-incomplete-classification", ObserverTokenFields());
            // Preserve unknown classification even when explicit policy separates execution status.
            Emit("classification-incomplete", new Dictionary<string, object> {
                ["origin"] = origin, ["ownerPid"] = processId, ["ownerQueryError"] = ownerError,
                ["nativeEventMilliseconds"] = eventTime, ["classError"] = classError
            });
            if (attributionContinuation.TryContinue(origin, callbackSequence, objectId, childId,
                first.ValidBefore, visible, classLength, classError, processId,
                !first.ValidAfter && first.OwnerPid == 0 && first.OwnerThread == 0 &&
                first.OwnerPidAfter == 0 && first.OwnerThreadAfter == 0 && threadProcessId == 0,
                previouslyOwnedWindows.ContainsKey(windowKey)))
            {
                failed = true;
                Emit("attribution-limitation", new Dictionary<string, object> {
                    ["callbackSequence"] = callbackSequence,
                    ["window"] = QuietCaptureLedger.WindowKey(window),
                    ["attributionStatus"] = "unknown-failed",
                    ["executionContinuation"] = countEveryEligibleShow ?
                        "explicit-user-counted-invalid-show-policy" : "explicit-user-single-show-policy",
                    ["visibleAtObservation"] = visible,
                    ["foreignOwnershipProven"] = false
                });
            }
            else ProbeError("visible-window-classification", classLength == 0 ? classError : 0);
        }
    }
    private void OnEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        ObserverCallbackContext.Frame frame = null;
        try
        {
            var raw = capture.Callback(kind, window, objectId, childId, eventThread, eventTime, Environment.TickCount64);
            frame = callbackContext.Enter((long)raw["callbackSequence"]);
            raw["callbackDepth"] = frame.Depth;
            raw["parentCallbackSequence"] = frame.ParentSequence;
            raw["hook"] = QuietCaptureLedger.WindowKey(hook);
            WindowFirstSnapshot first = null;
            ThreadOwnershipAttempt originalAtEntry = null;
            if (window != IntPtr.Zero && (kind == 0x0010 || (objectId == 0 && childId == 0)))
            {
                first = WindowFirstCapture.Capture(new WindowFirstNative(), window, () => Environment.TickCount64);
                first.Fields["callbackSequence"] = raw["callbackSequence"];
                first.Fields["callbackDepth"] = frame.Depth;
                first.Fields["parentCallbackSequence"] = frame.ParentSequence;
                first.Fields["eventThreadId"] = eventThread;
                first.Fields["originalEventTime"] = eventTime;
                first.Fields["originalEventKind"] = kind;
                first.Fields["objectId"] = objectId;
                first.Fields["childId"] = childId;
                first.Fields["hook"] = QuietCaptureLedger.WindowKey(hook);
                raw["callbackFirstWindowCapture"] = first.Fields;
                Emit("callback-first-window-capture", new Dictionary<string, object>(first.Fields));
                if (first.OwnerPid == 0 && eventThread != 0)
                {
                    originalAtEntry = ThreadOwnershipProbe.Query(new ObserverThreadNative(), eventThread);
                    raw["callbackFirstFallbackThread"] = originalAtEntry.Fields();
                }
                raw["callbackFirstWindowOwnerThread"] = first.OwnerThread == 0 ? null :
                    ThreadOwnershipProbe.Query(new ObserverThreadNative(), first.OwnerThread).Fields();
                raw["callbackFirstWindowOwnerProcess"] = CallbackProcessIdentity.Capture(first.OwnerPid);
                if (first.OwnerPid == 0 && eventThread != 0)
                    raw["callbackDeliveryEventThreadSnapshot"] = CallbackThreadSnapshot.Capture(eventThread);
            }
            Emit("raw-WinEvent-callback", raw);
            long sequence = (long)raw["callbackSequence"];
            if (kind == 0x0010)
                ObserveWindow(window, "WinEvent-dialog-start", true, eventThread, eventTime, sequence, objectId, childId, first, originalAtEntry);
            else if (kind == 0x8002 && objectId == 0 && childId == 0)
                ObserveWindow(window, "WinEvent-object-show", false, eventThread, eventTime, sequence, objectId, childId, first, originalAtEntry);
        }
        catch (Exception) { ProbeError("WinEvent-callback", Marshal.GetLastWin32Error()); }
        finally
        {
            if (frame != null)
            {
                frame.Dispose();
                Emit("WinEvent-callback-completed", new Dictionary<string, object> {
                    ["callbackSequence"] = frame.Sequence, ["callbackDepth"] = frame.Depth,
                    ["parentCallbackSequence"] = frame.ParentSequence,
                    ["completedTickCount64"] = Environment.TickCount64
                });
            }
        }
    }
    private static Dictionary<string, object> ObserverTokenFields()
    {
        try { return ObserverTokenEvidence.Capture(); }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["captureErrorType"] = ex.GetType().Name };
        }
    }
    private void Probe(string phase)
    {
        double gap = LastProbeAgeMs;
        if (Interlocked.Read(ref lastProbeTicks) != 0 && gap > maximumGapMs)
            ProbeError("fallback-gap-exceeded", 0);
        long began = clock.ElapsedTicks;
        string threadDesktop = ObjectName(GetThreadDesktop(GetCurrentThreadId()));
        string station = ObjectName(GetProcessWindowStation());
        if (!string.Equals(threadDesktop, "Default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(station, "WinSta0", StringComparison.OrdinalIgnoreCase))
            ProbeError("observer-not-on-WinSta0-Default", 0);
        IntPtr input = OpenInputDesktop(0, false, 0x0001);
        if (input == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        string inputName;
        try { inputName = ObjectName(input); }
        finally { CloseDesktop(input); }
        if (!string.Equals(threadDesktop, inputName, StringComparison.OrdinalIgnoreCase))
            ProbeError("observer-not-on-visible-input-desktop", 0);
        int windows = 0;
        EnumWindowsProc enumerate = delegate(IntPtr window, IntPtr unused) {
            try { windows++; ObserveWindow(window, "fallback-" + phase, false, 0, 0); }
            catch (Exception) { ProbeError("fallback-window", Marshal.GetLastWin32Error()); }
            return true;
        };
        if (!EnumWindows(enumerate, IntPtr.Zero))
            ProbeError("EnumWindows", Marshal.GetLastWin32Error());
        foreach (KeyValuePair<string, bool> desktop in ownedDesktops)
        {
            IntPtr owned = OpenDesktopW(desktop.Key, 0, false, 0x0041);
            int error = Marshal.GetLastWin32Error();
            if (owned == IntPtr.Zero)
            {
                if (error != 2 || desktop.Value) ProbeError("OpenDesktop-owned", error);
                continue;
            }
            try
            {
                if (!EnumDesktopWindows(owned, enumerate, IntPtr.Zero))
                    ProbeError("EnumDesktopWindows-owned", Marshal.GetLastWin32Error());
            }
            finally { CloseDesktop(owned); }
        }
        double duration = (clock.ElapsedTicks - began) * 1000.0 / Stopwatch.Frequency;
        if (duration > maximumGapMs) ProbeError("fallback-probe-duration-exceeded", 0);
        Interlocked.Exchange(ref lastProbeTicks, began);
        Emit("fallback-probe", new Dictionary<string, object> {
            ["phase"] = phase, ["windowStation"] = station, ["observerDesktop"] = threadDesktop,
            ["inputDesktop"] = inputName, ["enumeratedWindows"] = windows,
            ["previousProbeGapMs"] = gap,
            ["probeDurationMs"] = duration,
            ["maximumAllowedGapMs"] = maximumGapMs
        });
    }

    public static class QuietLaneKernel
    {
        [DllImport("kernel32.dll")] public static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern int WerGetFlags(IntPtr process, out uint flags);
        [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
        public static uint ReadWerFlags()
        {
            uint flags;
            int result = WerGetFlags(GetCurrentProcess(), out flags);
            if (result != 0) throw new InvalidOperationException("WerGetFlags failed: 0x" + result.ToString("X8"));
            return flags;
        }
        public static void WriteWerFlags(uint flags)
        {
            int result = WerSetFlags(flags);
            if (result != 0) throw new InvalidOperationException("WerSetFlags failed: 0x" + result.ToString("X8"));
            if (ReadWerFlags() != flags) throw new InvalidOperationException("WER process flag readback mismatch.");
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited,
            out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr Heap;
            public uint Module, Threads, ParentId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
        }

        public sealed class QuietLaneSafety : IDisposable
        {
            public uint OriginalMode { get; private set; }
            public uint OriginalWerFlags { get; private set; }
            public uint ActiveMode { get; private set; }
            public uint ActiveWerFlags { get; private set; }
            public bool Restored { get; private set; }
            private QuietLaneSafety() { }
            public static QuietLaneSafety Enter()
            {
                QuietLaneSafety scope = new QuietLaneSafety();
                scope.OriginalMode = QuietLaneKernel.GetErrorMode();
                scope.OriginalWerFlags = QuietLaneKernel.ReadWerFlags();
                scope.ActiveMode = scope.OriginalMode | 3u;
                scope.ActiveWerFlags = scope.OriginalWerFlags | 32u;
                try
                {
                    QuietLaneKernel.SetErrorMode(scope.ActiveMode);
                    QuietLaneKernel.WriteWerFlags(scope.ActiveWerFlags);
                    scope.EnsureQuiet();
                    return scope;
                }
                catch (Exception failure)
                {
                    try { scope.Dispose(); }
                    catch (Exception restore) { throw new AggregateException("Quiet entry and restoration failed.", failure, restore); }
                    throw;
                }
            }
            public void EnsureQuiet()
            {
                if (QuietLaneKernel.GetErrorMode() != ActiveMode ||
                    QuietLaneKernel.ReadWerFlags() != ActiveWerFlags)
                    throw new InvalidOperationException("Process quiet mode or WER NO_UI readback changed.");
            }
            public void Dispose()
            {
                if (Restored) return;
                List<Exception> errors = new List<Exception>();
                try { QuietLaneKernel.WriteWerFlags(OriginalWerFlags); }
                catch (Exception ex) { errors.Add(ex); }
                QuietLaneKernel.SetErrorMode(OriginalMode);
                if (QuietLaneKernel.GetErrorMode() != OriginalMode)
                    errors.Add(new InvalidOperationException("Original process error mode was not restored."));
                Restored = errors.Count == 0;
                if (!Restored) throw new AggregateException("Process-scoped quiet restoration failed.", errors);
            }
        }
        public static long CreationTime(Process process)
        {
            long created, exited, kernel, user;
            if (!GetProcessTimes(process.SafeHandle.DangerousGetHandle(), out created, out exited, out kernel, out user))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return created;
        }
        private static uint ParentId(int processId)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                do { if (entry.ProcessId == (uint)processId) return entry.ParentId; }
                while (Process32NextW(snapshot, ref entry));
                throw new InvalidOperationException("Tracked root is absent from the process snapshot.");
            }
            finally { CloseHandle(snapshot); }
        }
        public static string CancelOwnedTree(int processId, long creationTime, string expectedImage,
            Process retainedController, long controllerCreationTime, int waitMilliseconds)
        {
            if (CreationTime(retainedController) != controllerCreationTime)
                throw new InvalidOperationException("Retained controller creation identity changed.");
            Process root;
            try { root = Process.GetProcessById(processId); }
            catch (ArgumentException) { return "original-already-exited"; }
            using (root)
            {
                // Pin the original process object across identity checks and Kill(true).
                bool pinned = false;
                try
                {
                    root.SafeHandle.DangerousAddRef(ref pinned);
                    if (root.HasExited) return "original-already-exited";
                    if (CreationTime(root) != creationTime)
                        return "original-exited-pid-reused-not-touched";
                    StringBuilder image = new StringBuilder(32768);
                    uint length = (uint)image.Capacity;
                    if (!QueryFullProcessImageNameW(root.SafeHandle.DangerousGetHandle(), 0, image, ref length))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!string.Equals(image.ToString(), expectedImage, StringComparison.OrdinalIgnoreCase) ||
                        ParentId(processId) != (uint)retainedController.Id)
                        throw new InvalidOperationException("Tracked root image or actual controller lineage does not match.");
                    root.Kill(true);
                    if (!root.WaitForExit(waitMilliseconds))
                        throw new TimeoutException("Exact-owned root did not exit after tree cancellation.");
                    return "verified-owned-tree-cancelled-root-reaped";
                }
                finally { if (pinned) root.SafeHandle.DangerousRelease(); }
            }
        }
        public static bool DesktopAbsent(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
                throw new ArgumentException("Only a recorded leaf desktop name is accepted.");
            IntPtr desktop = OpenDesktopW(name, 0, false, 1);
            int error = Marshal.GetLastWin32Error();
            if (desktop != IntPtr.Zero) { CloseDesktop(desktop); return false; }
            if (error != 2) throw new Win32Exception(error);
            return true;
        }
    }
    private void Pump()
    {
        Message message;
        while (PeekMessageW(out message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }
    private void Run()
    {
        IntPtr dialogs = IntPtr.Zero, shows = IntPtr.Zero, destroys = IntPtr.Zero;
        workerId = GetCurrentThreadId();
        try
        {
            // Hooks precede baseline enumeration, so baseline-to-start events are not omitted.
            dialogs = SetWinEventHook(0x0010, 0x0010, IntPtr.Zero, callback, 0, 0, 0);
            shows = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, callback, 0, 0, 0);
            destroys = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, callback, 0, 0, 0);
            if (dialogs == IntPtr.Zero || shows == IntPtr.Zero || destroys == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var identity = ObserverTokenFields();
            identity["clockAnchorTickCount64"] = Environment.TickCount64;
            identity["clockAnchorUtcFileTime"] = DateTime.UtcNow.ToFileTimeUtc();
            identity["hookProcessFilter"] = 0;
            identity["hookThreadFilter"] = 0;
            identity["hookFlags"] = 0;
            identity["hookEventRanges"] = new uint[] { 0x0010, 0x8002, 0x8001 };
            Emit("observer-token-evidence-startup", identity);
            Probe("baseline");
            Pump();
            Emit("observer-ready", new Dictionary<string, object> { ["failed"] = failed });
            Ready.Set();
            while (!stopping)
            {
                uint result = MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)intervalMs, 0x04FF);
                if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                Pump();
                if (LastProbeAgeMs >= intervalMs) Probe("running");
            }
            Pump();
            Probe("final");
        }
        catch (Exception ex) { ProbeError("observer-thread-" + ex.GetType().Name, Marshal.GetLastWin32Error()); }
        finally
        {
            if (destroys != IntPtr.Zero && !UnhookWinEvent(destroys))
                ProbeError("UnhookWinEvent-destroy-capture", Marshal.GetLastWin32Error());
            if (shows != IntPtr.Zero && !UnhookWinEvent(shows))
                ProbeError("UnhookWinEvent-show", Marshal.GetLastWin32Error());
            if (dialogs != IntPtr.Zero && !UnhookWinEvent(dialogs))
                ProbeError("UnhookWinEvent-dialog", Marshal.GetLastWin32Error());
            Ready.Set();
            Emit("observer-stopped", new Dictionary<string, object> { ["failed"] = failed });
        }
    }
    public bool Stop(int milliseconds)
    {
        stopping = true;
        if (workerId != 0) PostThreadMessageW(workerId, 0, UIntPtr.Zero, IntPtr.Zero);
        return worker.Join(milliseconds);
    }
    public void Dispose()
    {
        if (worker.IsAlive) throw new InvalidOperationException("Observer thread must stop before disposal.");
        Changed.Dispose();
        Ready.Dispose();
    }
    private delegate void WinEventProc(IntPtr hook, uint kind, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window; public uint Kind; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int kind, StringBuilder value, uint size, out uint needed);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool all, uint milliseconds, uint mask);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

// Pure receipt construction and identity matching. No native methods or observation policy.
public sealed class QuietCaptureLedger
{
    private readonly ConcurrentDictionary<uint, long> owners = new ConcurrentDictionary<uint, long>();
    private readonly Dictionary<string, uint> lastShow = new Dictionary<string, uint>();
    private readonly Dictionary<string, uint> lastDestroy = new Dictionary<string, uint>();
    private long sequence;
    public static string WindowKey(IntPtr window) { return "0x" + window.ToInt64().ToString("X"); }
    public void TrackProcess(uint pid, string startedUtc)
    {
        owners[pid] = DateTimeOffset.Parse(startedUtc, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime.Ticks;
    }
    public string Correlate(uint pid, string createdUtc)
    {
        if (pid == 0 || string.IsNullOrEmpty(createdUtc)) return "unknown";
        long known;
        if (!owners.TryGetValue(pid, out known)) return "not-recorded-as-owned";
        return known == DateTimeOffset.Parse(createdUtc, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime.Ticks
            ? "exact-pid-and-creation-time-match" : "pid-reused-or-identity-mismatch";
    }
    public Dictionary<string, object> Callback(uint kind, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime, long receivedTickCount64 = 0)
    {
        lock (lastShow)
        {
            string key = WindowKey(window);
            uint destroyed;
            object priorDestroy = lastDestroy.TryGetValue(key, out destroyed) ? (object)destroyed : null;
            uint prior;
            bool destroyPair = kind == 0x8001 && objectId == 0 && childId == 0 && lastShow.TryGetValue(key, out prior);
            // Unchecked subtraction preserves DWORD event-clock wraparound.
            object elapsed = destroyPair ? (object)unchecked(eventTime - lastShow[key]) : null;
            if (kind == 0x8002 && objectId == 0 && childId == 0) lastShow[key] = eventTime;
            if (kind == 0x8001 && objectId == 0 && childId == 0)
            {
                lastShow.Remove(key);
                lastDestroy[key] = eventTime;
            }
            return new Dictionary<string, object> {
                ["callbackSequence"] = ++sequence, ["window"] = key, ["event"] = kind,
                ["objectId"] = objectId, ["childId"] = childId, ["idEventThread"] = eventThread,
                ["dwmsEventTime"] = eventTime, ["pairedShowToDestroyMilliseconds"] = elapsed,
                ["priorDestroyMillisecondsSameHandleOnly"] = priorDestroy,
                ["receivedTickCount64"] = receivedTickCount64,
                ["deliveryLagMillisecondsModuloDword"] = unchecked((uint)receivedTickCount64 - eventTime),
                ["pairingIsSameHandleOnlyNotIdentityProof"] = true
            };
        }
    }
    public static Dictionary<string, object> NativeCalls(int classReturn, int classError, uint threadReturn, int ownerError)
    {
        return new Dictionary<string, object> {
            ["GetClassNameW"] = new Dictionary<string, object> {
                ["return"] = classReturn, ["lastErrorRaw"] = classError, ["lastErrorApplies"] = classReturn == 0 },
            ["GetWindowThreadProcessId"] = new Dictionary<string, object> {
                ["return"] = threadReturn, ["lastErrorRaw"] = ownerError, ["lastErrorApplies"] = threadReturn == 0 }
        };
    }
}
