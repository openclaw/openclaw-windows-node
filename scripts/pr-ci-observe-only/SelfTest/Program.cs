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
Console.WriteLine($"{{\"schema\":1,\"checks\":{checks},\"passed\":true,\"nativeProcessesOpened\":0}}");
