// Temporary out-of-process VSTest observer. It never starts a process or requests memory access.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
namespace OpenClaw.ObserveOnly;

[DataCollectorFriendlyName("OpenClawObserveOnly")]
[DataCollectorTypeUri("datacollector://openclaw/observe-only/1")]
public sealed class Observer : DataCollector
{
    private const uint QuerySynchronize = 0x00101000; // QUERY_LIMITED_INFORMATION | SYNCHRONIZE, never VM_READ.
    private readonly object gate = new();
    private readonly Binding state = new();
    private readonly ReceiptCustody custody = new();
    private SafeProcessHandle? host;
    private DataCollectionEvents? events;
    private string invocation = "", source = "", sourceHash = "", hostHash = "", dotnetHash = "", blameHash = "", jobName = "";
    private long creation;
    private int pid;
    private bool disposed, processRetained, jobMatched;
    private readonly List<BinaryIdentity> binaries = new();

    public override void Initialize(XmlElement? configurationElement, DataCollectionEvents collectionEvents,
        DataCollectionSink dataSink, DataCollectionLogger logger, DataCollectionEnvironmentContext? environmentContext)
    {
        try
        {
            var configuration = ObserverConfiguration.Parse(configurationElement);
            custody.Bind(configuration.Root, configuration.Invocation);
            invocation = configuration.Invocation;
            // One invocation-wide claim, only after private-root and nonce custody is established.
            custody.CreateClaim();
            source = Canonical(configuration.ExpectedSource); sourceHash = DigestText(configuration.ExpectedSourceSha256);
            blameHash = DigestText(configuration.ExpectedBlameSha256);
            hostHash = DigestText(configuration.ExpectedHostImageSha256); dotnetHash = DigestText(configuration.ExpectedDotnetSha256);
            var model = Binary(typeof(DataCollector).Assembly.Location, "object-model", "loaded");
            var self = Binary(typeof(Observer).Assembly.Location, "observer", "loaded");
            binaries.Add(model); binaries.Add(self);
            if (model.sha256 != DigestText(configuration.ExpectedObjectModelSha256) || self.sha256 != DigestText(configuration.ExpectedCollectorSha256)) throw new InvalidDataException();
            binaries.Add(Binary(typeof(object).Assembly.Location, "collector-corelib", "loaded"));
            var core = GetModuleHandle("coreclr.dll"); var path = new StringBuilder(32768);
            if (core == IntPtr.Zero || GetModuleFileName(core, path, path.Capacity) == 0) throw new InvalidDataException();
            binaries.Add(Binary(path.ToString(), "collector-coreclr", "loaded"));
            jobName = Environment.GetEnvironmentVariable("OPENCLAW_OBSERVE_JOB_NAME") ?? "";
            if (!Regex.IsMatch(jobName, @"^Local\\OpenClawObserve-[a-f0-9]{32}$")) throw new InvalidDataException();
            // The launcher created the job before resuming any process. Merely matching a process name is never used.
            using var me = OpenProcess(QuerySynchronize, false, Environment.ProcessId);
            if (me.IsInvalid || !InOwnedJob(me)) throw new InvalidDataException();
            events = collectionEvents;
            events.TestHostLaunched += OnHost; events.TestCaseStart += OnCase;
            events.TestCaseEnd += OnEnd; events.SessionEnd += OnSessionEnd;
            WriteNew("observer-ready.json", new { schema = 1, invocation, captureEnabled = Binding.CaptureEnabled });
        }
        catch { Reject("initialization"); } // Never emit exception text into VSTest or its attachments.
    }

    private void OnHost(object? sender, TestHostLaunchedEventArgs e)
    {
        lock (gate)
        {
            try
            {
                if (host is not null) { state.Host(e.TestHostProcessId, false, false); return; }
                host = OpenProcess(QuerySynchronize, false, e.TestHostProcessId);
                pid = e.TestHostProcessId;
                var name = new StringBuilder(32768); var count = name.Capacity;
                bool valid = !host.IsInvalid && GetProcessId(host) == pid && GetProcessTimes(host, out creation, out _, out _, out _)
                    && creation > 0 && QueryFullProcessImageName(host, 0, name, ref count)
                    && IsWow64Process2(host, out var processMachine, out var nativeMachine) && processMachine == 0 && nativeMachine == 0x8664;
                if (!valid) { state.Host(pid, false, false); return; }
                var image = Binary(name.ToString(), "host-image", "framework-query"); binaries.Add(image);
                jobMatched = InOwnedJob(host); processRetained = true;
                state.Host(pid, jobMatched && (image.sha256 == hostHash || image.sha256 == dotnetHash), WaitForSingleObject(host, 0) == 258);
            }
            catch { Reject("host-query"); }
        }
    }
    private void OnCase(object? sender, TestCaseStartEventArgs e)
    {
        lock (gate)
        {
            try
            {
                var actual = Canonical(e.TestElement?.Source ?? "");
                bool alive = host is not null && !host.IsInvalid && WaitForSingleObject(host, 0) == 258;
                if (!alive) state.Reject("host-exited-before-case");
                state.Case(string.Equals(actual, source, StringComparison.OrdinalIgnoreCase), Hash(actual) == sourceHash);
            }
            catch { Reject("source-query"); }
        }
    }
    private void OnEnd(object? sender, TestCaseEndEventArgs e) { lock (gate) state.End(e.TestOutcome == TestOutcome.Passed); }
    private void OnSessionEnd(object? sender, SessionEndEventArgs e) { lock (gate) state.Close(); }
    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            try
            {
                if (events is not null)
                {
                    events.TestHostLaunched -= OnHost; events.TestCaseStart -= OnCase;
                    events.TestCaseEnd -= OnEnd; events.SessionEnd -= OnSessionEnd;
                }
                if (!custody.IsBound) return; // Early configuration refusal must never write relative files.
                if (host is not null && !host.IsInvalid)
                {
                    if (!GetProcessTimes(host, out var now, out _, out _, out _) || now != creation) state.Reject("host-identity");
                }
                var blame = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Microsoft.TestPlatform.Extensions.BlameDataCollector").ToArray();
                if (blame.Length != 1) state.Reject("collector-set");
                else { var identity = Binary(blame[0].Location, "blame", "loaded"); binaries.Add(identity); if (identity.sha256 != blameHash) state.Reject("collector-set"); }
                WriteNew("observer.json", new {
                    schema = 1, invocation, mode = "ObserveOnly", captureEnabled = Binding.CaptureEnabled,
                    hosts = state.Hosts, cases = state.Cases, ends = state.Ends, sourceMatched = state.SourceMatched,
                    probePassed = state.Passed, sessionEnded = state.SessionEnded, bound = state.Bound,
                    failure = state.Failure, processRetained, jobMatched, hostPid = pid, hostCreation = creation,
                    jobName, binaries
                });
            }
            catch { Reject("receipt-write"); }
            finally { host?.Dispose(); host = null; base.Dispose(disposing); }
        }
    }
    private void Reject(string reason)
    {
        state.Reject(reason);
        try { custody.MarkInvalid(); } catch { }
    }
    private bool InOwnedJob(SafeProcessHandle process)
    {
        var job = OpenJobObject(4, false, jobName);
        if (job == IntPtr.Zero) return false;
        try { return IsProcessInJob(process, job, out var inside) && inside; } finally { CloseHandle(job); }
    }
    private void WriteNew(string name, object value) => custody.WriteNew(name, value);
    internal static string Canonical(string path, bool directory = false) => ReceiptCustody.Canonical(path, directory);
    internal static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }
    private static string DigestText(string text) => Regex.IsMatch(text, "^[a-f0-9]{64}$") ? text : throw new InvalidDataException();
    private static BinaryIdentity Binary(string path, string role, string binding)
    {
        var v = FileVersionInfo.GetVersionInfo(path);
        var version = $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
        return new(role, Hash(path), version, binding);
    }
    private sealed record BinaryIdentity(string role, string sha256, string version, string binding);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort native);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int length);
}
