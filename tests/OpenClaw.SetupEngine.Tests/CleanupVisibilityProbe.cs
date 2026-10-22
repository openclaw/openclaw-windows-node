using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Native UIA is required for the modern shell taskbar. Framework UIA2 only
// exposed two container elements on the proof host, hiding all app buttons.
// Unused declarations preserve native vtable slots without extra dependencies.
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface CleanupUia
{
    void CompareElements();
    void CompareRuntimeIds();
    CleanupUiaElement GetRootElement();
    CleanupUiaElement ElementFromHandle(IntPtr hwnd);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void GetControlViewWalker();
    void GetContentViewWalker();
    void GetRawViewWalker();
    void GetRawViewCondition();
    void GetControlViewCondition();
    void GetContentViewCondition();
    CleanupUiaCacheRequest CreateCacheRequest();
    [return: MarshalAs(UnmanagedType.IUnknown)] object CreateTrueCondition();
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface CleanupUiaElement
{
    void SetFocus();
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
    void FindFirst();
    CleanupUiaElements FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition);
    void FindFirstBuildCache();
    CleanupUiaElements FindAllBuildCache(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition,
        CleanupUiaCacheRequest cache);
    void BuildUpdatedCache();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int property);
    void GetCurrentPropertyValueEx();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCachedPropertyValue(int property);
}

[ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface CleanupUiaCacheRequest
{
    void AddProperty(int property);
    void AddPattern();
    void Clone();
    void GetTreeScope();
    void SetTreeScope();
    void GetTreeFilter();
    void SetTreeFilter();
    void GetAutomationElementMode();
    void SetAutomationElementMode(int mode);
}

[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface CleanupUiaElements
{
    int GetLength();
    CleanupUiaElement GetElement(int index);
}

public sealed class CleanupVisibilityProbe : IDisposable
{
    private const int WindowSampleIntervalMs = 15;
    private const int ProcessTreeIntervalMs = 100;
    private const int TaskbarCoalesceIntervalMs = 250;
    public sealed class Observation
    {
        public string Source;
        public int Pid;
        public long Handle;
        public string ClassName;
        public string Match;
        public bool OnScreen;
        public bool ShowEvent;
        public double ElapsedMs;
    }

    public sealed class Result
    {
        public int? ExitCode;
        public int WindowSamples;
        public int TaskbarSamples;
        public int TaskbarsFound;
        public int MaximumTaskbarElements;
        public double MaximumWindowSampleGapMs;
        public double MaximumTaskbarSampleGapMs;
        public double ElapsedMs;
        public double ObserverCpuMs;
        public string StartedAtUtc;
        public string CompletedAtUtc;
        public int[] OwnedProcessIds;
        public string[] Errors;
        public Observation[] Observations;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, width, height, xc, yc, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPtr, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint msg;
        public IntPtr wp, lp;
        public uint time;
        public int x, y;
        public uint extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint size, usage, pid;
        public UIntPtr heap;
        public uint module, threads, parent;
        public int priority;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;
    }
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr state);
    private delegate void EventCallback(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit,
        int flags, IntPtr env, string directory, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(
        IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool GetMessage(out Message msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr wp, IntPtr lp);

    private readonly ConcurrentDictionary<int, bool> owned = new ConcurrentDictionary<int, bool>();
    private readonly ConcurrentDictionary<long, bool> ownedWindows = new ConcurrentDictionary<long, bool>();
    private readonly ConcurrentDictionary<int, bool> consoleOwners = new ConcurrentDictionary<int, bool>();
    private readonly ConcurrentDictionary<long, bool> consoleWindows = new ConcurrentDictionary<long, bool>();
    private readonly ConcurrentDictionary<string, bool> titles = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Observation> events = new ConcurrentQueue<Observation>();
    private readonly ConcurrentQueue<string> errors = new ConcurrentQueue<string>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double maximumWindowGap;
    private readonly CleanupVisibilityRunState runState = new CleanupVisibilityRunState();
    private readonly List<Process> externalProcesses = new List<Process>();
    private readonly TimeSpan initialCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly ManualResetEvent eventReady = new ManualResetEvent(false);
    private readonly ManualResetEvent taskbarReady = new ManualResetEvent(false);
    private readonly AutoResetEvent taskbarChanged = new AutoResetEvent(false);
    private readonly Thread eventThread, taskbarThread;
    private readonly string token;
    private EventCallback eventCallback;
    private uint eventThreadId;
    private int taskbarSamples, taskbarsFound;
    private int maximumTaskbarElements;
    private int windowSamples;
    private double maximumTaskbarGap;

    public CleanupVisibilityProbe(string token)
    {
        this.token = token;
        EnumWindows((hwnd, state) => {
            var name = ClassName(hwnd);
            if (name == "ConsoleWindowClass" || name == "CASCADIA_HOSTING_WINDOW_CLASS")
                consoleWindows.TryAdd(hwnd.ToInt64(), true);
            return true;
        }, IntPtr.Zero);
        eventThread = new Thread(WatchEvents) { IsBackground = true };
        taskbarThread = new Thread(WatchTaskbar) { IsBackground = true };
        taskbarThread.SetApartmentState(ApartmentState.MTA);
        eventThread.Start();
        taskbarThread.Start();
        if (!eventReady.WaitOne(10000) || !taskbarReady.WaitOne(10000))
            throw new TimeoutException("Visibility observers did not initialize.");
        ThrowObserverErrors();
    }

    private void ThrowObserverErrors()
    {
        if (!errors.IsEmpty) throw new InvalidOperationException(string.Join("\n", errors.ToArray()));
    }

    public Result Snapshot()
    {
        return new Result {
            ExitCode = runState.RootExitCode,
            WindowSamples = windowSamples, TaskbarSamples = taskbarSamples, TaskbarsFound = taskbarsFound,
            MaximumTaskbarElements = maximumTaskbarElements,
            MaximumWindowSampleGapMs = maximumWindowGap,
            ElapsedMs = clock.Elapsed.TotalMilliseconds,
            ObserverCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - initialCpu).TotalMilliseconds,
            MaximumTaskbarSampleGapMs = maximumTaskbarGap, Errors = errors.ToArray(),
            StartedAtUtc = startedAtUtc.ToString("o"), CompletedAtUtc = DateTime.UtcNow.ToString("o"),
            OwnedProcessIds = GetOwnedProcessIds(),
            Observations = Array.FindAll(events.ToArray(), e => e.Source == "TaskbarUIA" || IsOwnedWindow(e))
        };
    }

    private static string ClassName(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private bool IsOwnedWindow(Observation observation)
    {
        return (owned.ContainsKey(observation.Pid) &&
            runState.CanAttributeObservation(observation.Pid, observation.ElapsedMs)) ||
            ownedWindows.ContainsKey(observation.Handle);
    }

    private readonly DateTime startedAtUtc = DateTime.UtcNow;

    private bool StartedDuringProbe(int pid)
    {
        // StartTime in Windows PowerShell's Framework requests broader access.
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return false; // The snapshot entry exited.
            throw new System.ComponentModel.Win32Exception(error,
                "Cannot inspect candidate process " + pid + " with limited query access (Win32 " + error + ").");
        }
        try
        {
            long creation, exit, kernel, user;
            if (!GetProcessTimes(process, out creation, out exit, out kernel, out user))
            {
                int error = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(error,
                    "Cannot read creation time for candidate process " + pid + " (Win32 " + error + ").");
            }
            return DateTime.FromFileTimeUtc(creation) >= startedAtUtc;
        }
        finally { CloseHandle(process); }
    }

    private int[] GetOwnedProcessIds()
    {
        var ids = new HashSet<int>(owned.Keys);
        ids.UnionWith(consoleOwners.Keys);
        return new List<int>(ids).ToArray();
    }

    private void ObserveWindow(string source, IntPtr hwnd, bool showEvent = false)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        bool isOwned = owned.ContainsKey((int)pid) || runState.IsExternalProcess((int)pid) ||
            ownedWindows.ContainsKey(hwnd.ToInt64());
        if (source == "WindowSample" && !isOwned && !runState.CaptureUnownedStartupSamples &&
            !consoleWindows.ContainsKey(hwnd.ToInt64())) return;
        string className = ClassName(hwnd);
        bool isConsole = className == "ConsoleWindowClass" || className == "CASCADIA_HOSTING_WINDOW_CLASS";
        if (isConsole) consoleWindows.TryAdd(hwnd.ToInt64(), true);
        if (source == "WindowSample" && !isOwned && !isConsole && !runState.CaptureUnownedStartupSamples) return;
        var title = new StringBuilder(1024);
        if (isOwned || isConsole)
        {
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString().IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                titles.TryAdd(title.ToString(), true);
                // Terminal can multiplex unrelated windows in the same process.
                ownedWindows.TryAdd(hwnd.ToInt64(), true);
                if (className == "ConsoleWindowClass") consoleOwners.TryAdd((int)pid, true);
                isOwned = true;
            }
        }
        if (source == "WindowSample" && !isOwned && !runState.CaptureUnownedStartupSamples) return;
        // A SHOW event can be delivered after a fast hide. Preserve the event
        // instead of losing exactly the transient startup flash being tested.
        if (showEvent && GetAncestor(hwnd, 2) != hwnd) return;
        if (!showEvent && !IsWindowVisible(hwnd)) return;
        Rect rect;
        bool onScreen = GetWindowRect(hwnd, out rect) && rect.right > rect.left && rect.bottom > rect.top &&
            MonitorFromRect(ref rect, 0) != IntPtr.Zero;
        events.Enqueue(new Observation {
            Source = source, Pid = (int)pid, Handle = hwnd.ToInt64(), ClassName = className,
            OnScreen = onScreen, ShowEvent = showEvent, ElapsedMs = clock.Elapsed.TotalMilliseconds
        });
    }

    private void WatchEvents()
    {
        eventThreadId = GetCurrentThreadId();
        eventCallback = (hook, ev, hwnd, obj, child, thread, time) => {
            if (hwnd != IntPtr.Zero && obj == 0 && child == 0 && (ev == 0x8000 || ev == 0x8002))
                ObserveWindow("WinEvent", hwnd, ev == 0x8002);
        };
        var handle = SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, eventCallback, 0, 0, 0);
        if (handle == IntPtr.Zero) errors.Enqueue("SetWinEventHook failed.");
        eventReady.Set();
        try
        {
            if (handle != IntPtr.Zero)
            {
                Message message;
                while (GetMessage(out message, IntPtr.Zero, 0, 0)) { }
            }
        }
        finally { if (handle != IntPtr.Zero) UnhookWinEvent(handle); }
    }

    private void WatchTaskbar()
    {
        var taskbars = new List<CleanupUiaElement>();
        CleanupUia automation = null;
        CleanupUiaCacheRequest cache = null;
        object condition = null;
        try
        {
            automation = (CleanupUia)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), true));
            condition = automation.CreateTrueCondition();
            cache = automation.CreateCacheRequest();
            cache.SetAutomationElementMode(0);
            foreach (int property in new[] { 30002, 30005, 30011, 30012, 30013, 30022, 30026, 30092, 30093 })
                cache.AddProperty(property);
            EnumWindows((hwnd, state) => {
                var name = ClassName(hwnd);
                Rect bounds;
                bool customBar = name == "Tauri Window" && IsWindowVisible(hwnd) &&
                    (GetWindowLong(hwnd, -20) & 0x80) != 0 && // WS_EX_TOOLWINDOW, not an ordinary app window.
                    GetWindowRect(hwnd, out bounds) && bounds.right - bounds.left > 1 && bounds.bottom - bounds.top > 1;
                if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd" || customBar)
                    taskbars.Add(automation.ElementFromHandle(hwnd));
                return true;
            }, IntPtr.Zero);
            taskbarsFound = taskbars.Count;
            if (taskbars.Count == 0) throw new InvalidOperationException("No interactive shell taskbar was found.");
            bool baselineComplete = false;
            double previous = clock.Elapsed.TotalMilliseconds;
            do
            {
                foreach (var taskbar in taskbars)
                {
                    var elements = taskbar.FindAllBuildCache(4, condition, cache);
                    try
                    {
                        int count = elements.GetLength();
                        maximumTaskbarElements = Math.Max(maximumTaskbarElements, count);
                        for (int index = 0; index < count; index++)
                        {
                            var element = elements.GetElement(index);
                            try
                            {
                                string name = Convert.ToString(element.GetCachedPropertyValue(30005));
                                string detail = name + "\n" + element.GetCachedPropertyValue(30013) + "\n" +
                                    element.GetCachedPropertyValue(30026) + "\n" + element.GetCachedPropertyValue(30011) +
                                    "\n" + element.GetCachedPropertyValue(30092) + "\n" + element.GetCachedPropertyValue(30093);
                                int pid = Convert.ToInt32(element.GetCachedPropertyValue(30002));
                                bool tokenMatched = detail.IndexOf(token, StringComparison.Ordinal) >= 0;
                                bool titleMatched = titles.ContainsKey(name);
                                if (baselineComplete && (owned.ContainsKey(pid) ||
                                    tokenMatched || titleMatched))
                                {
                                    events.Enqueue(new Observation {
                                        Source = "TaskbarUIA", Pid = pid,
                                        Match = tokenMatched ? "UniqueToken" : titleMatched ? "OwnedWindowTitle" : "OwnedPid",
                                        ClassName = Convert.ToString(element.GetCachedPropertyValue(30012)),
                                        OnScreen = !Convert.ToBoolean(element.GetCachedPropertyValue(30022)),
                                        ElapsedMs = clock.Elapsed.TotalMilliseconds
                                    });
                                }
                            }
                            finally { Marshal.ReleaseComObject(element); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(elements); }
                }
                double now = clock.Elapsed.TotalMilliseconds;
                maximumTaskbarGap = Math.Max(maximumTaskbarGap, now - previous);
                previous = now;
                Interlocked.Increment(ref taskbarSamples);
                baselineComplete = true;
                taskbarReady.Set();
                // UIA provider traversals are expensive. Window/WinEvent sampling owns flash detection;
                // taskbar snapshots are throttled even when shell structure events arrive in a burst.
                if (stop.WaitOne(TaskbarCoalesceIntervalMs)) break;
                taskbarChanged.WaitOne(TaskbarCoalesceIntervalMs);
            } while (!stop.WaitOne(0));
        }
        catch (Exception error) { errors.Enqueue("Taskbar UIA: " + error); taskbarReady.Set(); }
        finally
        {
            foreach (var taskbar in taskbars) Marshal.ReleaseComObject(taskbar);
            if (condition != null) Marshal.ReleaseComObject(condition);
            if (cache != null) Marshal.ReleaseComObject(cache);
            if (automation != null) Marshal.ReleaseComObject(automation);
        }
    }

    private void RefreshTree()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entries = new List<ProcessEntry>();
            var entry = new ProcessEntry { size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception();
            do { entries.Add(entry); } while (Process32Next(snapshot, ref entry));
            bool changed;
            do
            {
                changed = false;
                foreach (var item in entries)
                    if (owned.ContainsKey((int)item.pid) || owned.ContainsKey((int)item.parent))
                    {
                        if (string.Equals(item.name, "WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Parent PIDs can outlive their process and be reused by this probe.
                        if (!owned.ContainsKey((int)item.pid) && StartedDuringProbe((int)item.pid) &&
                            owned.TryAdd((int)item.pid, true)) changed = true;
                    }
            } while (changed);
        }
        finally { CloseHandle(snapshot); }
    }

    public Result Run(string executable, string arguments, string directory, string readyName, string releaseName,
        bool positiveControl, int timeoutSeconds, string externalPidFile, bool noWindowStarter)
    {
        return Run(executable, arguments, directory, readyName, releaseName, positiveControl,
            timeoutSeconds, externalPidFile, noWindowStarter, null);
    }

    private static int ParentPid(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entry = new ProcessEntry { size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception();
            do { if (entry.pid == (uint)pid) return checked((int)entry.parent); }
            while (Process32Next(snapshot, ref entry));
            throw new InvalidOperationException("External worker process identity disappeared.");
        }
        finally { CloseHandle(snapshot); }
    }

    private void RegisterExternalWorker(int workerPid, string expectedHostPath)
    {
        var worker = Process.GetProcessById(workerPid);
        Process host = null;
        try
        {
            if (!worker.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("External proof worker is not Windows PowerShell.");
            var workerHandle = worker.Handle;
            if (!string.IsNullOrEmpty(expectedHostPath))
            {
                host = Process.GetProcessById(ParentPid(workerPid));
                var hostHandle = host.Handle;
                runState.RegisterExternalWorker(workerPid, host.Id, host.MainModule.FileName,
                    (host.StartTime.ToUniversalTime() - startedAtUtc).TotalMilliseconds,
                    (worker.StartTime.ToUniversalTime() - startedAtUtc).TotalMilliseconds);
                owned.TryAdd(host.Id, true);
                externalProcesses.Add(host);
                host = null;
            }
            else runState.MarkWorkerReady();
            owned.TryAdd(workerPid, true);
            externalProcesses.Add(worker);
            worker = null;
        }
        finally
        {
            if (worker != null) worker.Dispose();
            if (host != null) host.Dispose();
        }
    }

    public Result Run(string executable, string arguments, string directory, string readyName, string releaseName,
        bool positiveControl, int timeoutSeconds, string externalPidFile, bool noWindowStarter,
        string expectedExternalHostPath)
    {
        runState.ConfigureExternalHost(expectedExternalHostPath);
        var startup = new Startup {
            cb = Marshal.SizeOf(typeof(Startup)), flags = positiveControl ? 1 : 0, show = 1, title = token
        };
        ProcessInfo info;
        // Register the root PID before resuming, so startup events remain attributable.
        if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments), IntPtr.Zero,
            IntPtr.Zero, false, noWindowStarter ? 0x08000004 : 0x14, IntPtr.Zero, directory, ref startup, out info))
            throw new System.ComponentModel.Win32Exception();
        owned.TryAdd(info.pid, true);
        int samples = 0, afterReady = 0, readyTaskbarSamples = 0;
        long nextTreeRefresh = 0;
        bool workerReady = false, released = false;
        double previous = clock.Elapsed.TotalMilliseconds, maximumGap = 0;
        using (var ready = EventWaitHandle.OpenExisting(readyName))
        using (var release = EventWaitHandle.OpenExisting(releaseName))
        using (var process = Process.GetProcessById(info.pid))
        {
            try
            {
                ResumeThread(info.thread);
                while (!process.HasExited)
                {
                    ThrowObserverErrors();
                    if (clock.ElapsedMilliseconds >= nextTreeRefresh)
                    {
                        RefreshTree();
                        nextTreeRefresh = clock.ElapsedMilliseconds + ProcessTreeIntervalMs;
                    }
                    EnumWindows((hwnd, state) => { ObserveWindow("WindowSample", hwnd); return true; }, IntPtr.Zero);
                    samples++;
                    windowSamples = samples;
                    double now = clock.Elapsed.TotalMilliseconds;
                    maximumGap = Math.Max(maximumGap, now - previous);
                    maximumWindowGap = maximumGap;
                    previous = now;
                    if (!workerReady && ready.WaitOne(0))
                    {
                        readyTaskbarSamples = taskbarSamples;
                        if (!string.IsNullOrEmpty(externalPidFile))
                        {
                            int workerPid = int.Parse(System.IO.File.ReadAllText(externalPidFile),
                                System.Globalization.CultureInfo.InvariantCulture);
                            RegisterExternalWorker(workerPid, expectedExternalHostPath);
                        }
                        else runState.MarkWorkerReady();
                        workerReady = true;
                    }
                    if (workerReady)
                    {
                        afterReady++;
                        var recorded = events.ToArray();
                        bool eventSeen = Array.Exists(recorded, e => e.Source == "WinEvent" && e.OnScreen && IsOwnedWindow(e));
                        bool sampleSeen = Array.Exists(recorded, e => e.Source == "WindowSample" && e.OnScreen && IsOwnedWindow(e));
                        bool buttonSeen = Array.Exists(recorded, e => e.Source == "TaskbarUIA" && e.OnScreen);
                        if (!released && runState.CanRelease(afterReady, taskbarSamples - readyTaskbarSamples,
                            positiveControl, eventSeen, sampleSeen, buttonSeen))
                        {
                            release.Set();
                            released = true;
                            runState.MarkReleased();
                        }
                    }
                    if (clock.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
                        throw new TimeoutException("Launch visibility probe timed out; positive controls require both window observers and taskbar UIA.");
                    stop.WaitOne(WindowSampleIntervalMs);
                }
                process.WaitForExit();
                uint exitCode;
                if (!GetExitCodeProcess(info.process, out exitCode))
                    throw new System.ComponentModel.Win32Exception();
                runState.RecordRootExit(unchecked((int)exitCode));
                runState.RequireCompleteHandshake();
                var result = new Result {
                    ExitCode = unchecked((int)exitCode), WindowSamples = samples, TaskbarSamples = taskbarSamples,
                    TaskbarsFound = taskbarsFound, MaximumWindowSampleGapMs = maximumGap,
                    MaximumTaskbarElements = maximumTaskbarElements,
                    MaximumTaskbarSampleGapMs = maximumTaskbarGap,
                    ElapsedMs = clock.Elapsed.TotalMilliseconds,
                    ObserverCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - initialCpu).TotalMilliseconds
                };
                Dispose();
                result.StartedAtUtc = startedAtUtc.ToString("o");
                result.CompletedAtUtc = DateTime.UtcNow.ToString("o");
                result.OwnedProcessIds = GetOwnedProcessIds();
                result.Errors = errors.ToArray();
                result.Observations = Array.FindAll(events.ToArray(), e => e.Source == "TaskbarUIA" || IsOwnedWindow(e));
                ThrowObserverErrors();
                return result;
            }
            finally
            {
                release.Set();
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); }
                }
                CloseHandle(info.thread);
                CloseHandle(info.process);
            }
        }
    }

    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Set();
        PostThreadMessage(eventThreadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        if (!eventThread.Join(10000) || !taskbarThread.Join(10000))
            throw new TimeoutException("A visibility observer did not stop.");
        foreach (var process in externalProcesses) process.Dispose();
        stop.Dispose();
        eventReady.Dispose();
        taskbarReady.Dispose();
        taskbarChanged.Dispose();
    }
}
