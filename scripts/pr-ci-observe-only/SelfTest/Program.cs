// Pure negative controls. Does not open a process, load WinUI, or call the native collector.
using OpenClaw.ObserveOnly;
var checks = 0;
void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("binding-selftest-failed"); }
Binding Good() { var b = new Binding(); b.Host(42, true, true); b.Case(true, true); b.End(true); b.Close(); return b; }
Check(!Binding.CaptureEnabled); Check(Good().Bound); Check(!new Binding().Bound);
foreach (var invalid in new Action<Binding>[] {
 b => b.Host(0,true,true), b => b.Host(42,false,true), b => b.Host(42,true,false),
 b => { b.Host(42,true,true); b.Host(42,true,true); },
 b => { b.Host(42,true,true); b.Host(43,true,true); },
 b => b.Case(true,true), b => { b.Host(42,true,true); b.Case(false,true); },
 b => { b.Host(42,true,true); b.Case(true,false); },
 b => { b.Host(42,true,true); b.Case(true,true); b.Case(true,true); },
 b => b.End(true), b => { b.Host(42,true,true); b.Case(true,true); b.End(false); },
 b => { b.Close(); b.Host(42,true,true); }, b => { b.Close(); b.Case(true,true); },
 b => { b.Close(); b.End(true); }, b => { b.Close(); b.Close(); }
}) { var b = new Binding(); invalid(b); Check(!b.Bound); Check(b.Failure != "none"); }
var sticky = Good(); sticky.Reject("host-identity"); sticky.Reject("source-mismatch"); Check(sticky.Failure == "host-identity"); Check(!sticky.Bound);
var missingEnd = new Binding(); missingEnd.Host(42,true,true); missingEnd.Case(true,true); missingEnd.Close(); Check(!missingEnd.Bound);
// These invalid launches must stop before creating a job, file or process.
Check(OpenClawObserve.OwnedCommand.Quote("") == "\"\"");
Check(OpenClawObserve.OwnedCommand.Quote("plain") == "\"plain\"");
Check(OpenClawObserve.OwnedCommand.Quote("a b") == "\"a b\"");
Check(OpenClawObserve.OwnedCommand.Quote("a\"b") == "\"a\\\"b\"");
foreach (var timeout in new[] {0,901,1}) {
    bool rejected = false;
    try { OpenClawObserve.OwnedCommand.Run("relative", [], ".", new Dictionary<string,string>(), "must-not-exist", timeout); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected);
}
// Observed native exits survive real secondary exceptions in the same outcome owner used by Run.
foreach (var code in new[] {17,0}) {
    var outcome = new OpenClawObserve.CommandResult { Joined=true };
    outcome.RecordExit(code);
    outcome.AttemptCleanup(() => throw new IOException("synthetic cleanup failure"));
    Check(outcome.ExitCode == code);
    Check(outcome.CleanupFailed);
    Check(!outcome.LifecycleSucceeded);
    outcome.AttemptCleanup(() => outcome.Joined=true);
    Check(!outcome.LifecycleSucceeded);
}
var unknownExit = new OpenClawObserve.CommandResult { Joined=true };
unknownExit.AttemptCleanup(() => throw new IOException("synthetic unknown-launch cleanup failure"));
Check(!unknownExit.ExitCode.HasValue); Check(!unknownExit.LifecycleSucceeded);
var healthyExit = new OpenClawObserve.CommandResult(); healthyExit.RecordExit(0);
healthyExit.AttemptCleanup(() => healthyExit.Joined=true);
Check(healthyExit.LifecycleSucceeded); Check(healthyExit.ExitCode == 0);
// Seed only the managed last-P/Invoke-error cache. These controls invoke no Windows API.
var successfulApi = new OpenClawObserve.CommandWitness(); successfulApi.Enter(OpenClawObserve.CommandPhase.AttributeList);
System.Runtime.InteropServices.Marshal.SetLastPInvokeError(122); successfulApi.NoteNative(true);
Check(!successfulApi.Failed); Check(!successfulApi.Win32ErrorAvailable); Check(successfulApi.Win32Error == null);
var noCode = new OpenClawObserve.CommandWitness(); noCode.Enter(OpenClawObserve.CommandPhase.ProcessCreate);
System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0); noCode.NoteNative(false);
Check(noCode.Failed); Check(noCode.Category == OpenClawObserve.CommandFailure.NativeApi);
Check(!noCode.Win32ErrorAvailable); Check(noCode.Win32Error == null);
var managed = new OpenClawObserve.CommandWitness(); managed.Enter(OpenClawObserve.CommandPhase.Preparation);
System.Runtime.InteropServices.Marshal.SetLastPInvokeError(87); managed.RecordManaged(new ArgumentException("PRIVATE argument"));
Check(managed.Failed); Check(managed.Category == OpenClawObserve.CommandFailure.Argument);
Check(!managed.Win32ErrorAvailable); Check(managed.Win32Error == null);
var first = new OpenClawObserve.CommandWitness(); first.Enter(OpenClawObserve.CommandPhase.ProcessCreate);
System.Runtime.InteropServices.Marshal.SetLastPInvokeError(5); bool nativeRejected=false;
try {first.RequireNative(false);} catch(InvalidOperationException) {nativeRejected=true;}
Check(nativeRejected); Check(first.Win32Error == 5); Check(first.Win32ErrorAvailable);
Check(first.Phase == OpenClawObserve.CommandPhase.ProcessCreate); Check(first.Category == OpenClawObserve.CommandFailure.NativeApi);
first.Enter(OpenClawObserve.CommandPhase.Cleanup); System.Runtime.InteropServices.Marshal.SetLastPInvokeError(87);
first.NoteNative(false); first.RecordManaged(new IOException("PRIVATE secondary"));
Check(first.Win32Error == 5); Check(first.Phase == OpenClawObserve.CommandPhase.ProcessCreate); Check(first.Category == OpenClawObserve.CommandFailure.NativeApi);
foreach(var code in new[] {17,0}) {
    var outcome = new OpenClawObserve.CommandResult {Joined=true}; outcome.RecordExit(code);
    var witness = new OpenClawObserve.CommandWitness(); witness.Enter(OpenClawObserve.CommandPhase.Cleanup);
    System.Runtime.InteropServices.Marshal.SetLastPInvokeError(123);
    outcome.AttemptCleanup(() => witness.RequireNative(false), witness);
    Check(outcome.ExitCode == code); Check(outcome.CleanupFailed); Check(!outcome.LifecycleSucceeded);
    Check(witness.Win32Error == 123); Check(witness.Category == OpenClawObserve.CommandFailure.NativeApi);
}
var unknown = new OpenClawObserve.CommandResult(); var unknownWitness = new OpenClawObserve.CommandWitness();
unknownWitness.Enter(OpenClawObserve.CommandPhase.Preparation);
unknown.AttemptCleanup(() => throw new IOException("PRIVATE unknown exit"), unknownWitness);
Check(unknown.ExitCode == null); Check(unknownWitness.Category == OpenClawObserve.CommandFailure.Io);
Check(unknownWitness.Win32Error == null); Check(!unknownWitness.Win32ErrorAvailable);
var preparation = new OpenClawObserve.CommandWitness();
try {OpenClawObserve.OwnedCommand.Run("relative", [], ".", new Dictionary<string,string>(), "must-not-exist", 1, preparation);} catch(InvalidOperationException) { }
Check(preparation.Failed); Check(preparation.Phase == OpenClawObserve.CommandPhase.Preparation); Check(!preparation.Win32ErrorAvailable);
var other = new OpenClawObserve.CommandWitness(); other.RecordManaged(new Exception("PRIVATE unrecognized"));
Check(other.Category == OpenClawObserve.CommandFailure.Other); Check(other.Win32Error == null);
if (args.Length != 1) throw new InvalidOperationException("contract-selftest-root");
var contracts = ObserverContractTests.Run(args[0]);
var blameIdentityCases = BlameIdentityTests.Run();
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { schema=1, blameIdentityCases, checks, passed=true, nativeProcessesOpened=0, configurationCases=contracts.ConfigurationCases, custodyCases=contracts.CustodyCases }));
