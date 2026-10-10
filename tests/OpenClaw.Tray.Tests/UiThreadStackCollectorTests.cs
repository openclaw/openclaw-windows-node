using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// U4 portable controls for the bounded UI-thread stack + wait-chain collector and its ACTUAL bundle caller seam.
/// No native UI runs here: the native source is faked, and the Windows native source compiles in the WinUI project.
/// </summary>
// Serialized: these controls share the process-local capture admission/ownership, so they must not overlap.
[Collection("native-capture")]
public class UiThreadStackCollectorTests
{
    // ---- Adopted verbatim from Mini-Actual-U4-Worker-32a-Counterexample. ----
    [Fact]
    public async System.Threading.Tasks.Task MiniReview_OuterCaptureWorkersRemainExclusiveUntilPhysicalDrain()
    {
        using var release = new System.Threading.ManualResetEventSlim(false);
        var source = new MiniReviewCountingIdentitySource(release);
        var root = System.IO.Directory.CreateTempSubdirectory("mini-u4-workers-").FullName;
        var runs = Enumerable.Range(0, 4).Select(_ => System.Threading.Tasks.Task.Run(() =>
            CompanionHangCapture.Run(source, Target(), root, budget: TimeSpan.FromMilliseconds(50)))).ToArray();
        int entered;
        try { await System.Threading.Tasks.Task.WhenAll(runs); entered = source.Entered; }
        finally { release.Set(); }
        await System.Threading.Tasks.Task.Delay(150);
        Assert.True(entered <= 1, $"Repeated timeout captures launched {entered} concurrent held identity workers before physical drain");
        Assert.True(CaptureDrain.WaitForIdle(TimeSpan.FromSeconds(10)));   // drain before the next serialized control
    }
    private sealed class MiniReviewCountingIdentitySource : IUiThreadStackSource
    {
        private readonly System.Threading.ManualResetEventSlim _release;
        public int Entered;
        public MiniReviewCountingIdentitySource(System.Threading.ManualResetEventSlim release) { _release=release; }
        public bool TryRevalidate(UiThreadTarget target,out string? failure) { System.Threading.Interlocked.Increment(ref Entered); _release.Wait(); failure=null; return true; }
        public bool TryCaptureWaitChain(UiThreadTarget target,int maxNodes,TimeSpan deadline,out IReadOnlyList<WaitChainNodeView> nodes,out bool truncated,out string? failure) { nodes=Array.Empty<WaitChainNodeView>(); truncated=false; failure="synthetic unavailable"; return false; }
        public bool TryCaptureStack(UiThreadTarget target,int maxFrames,TimeSpan deadline,out IReadOnlyList<StackFrameView> frames,out bool truncated,out string? failure) { frames=Array.Empty<StackFrameView>(); truncated=false; failure="synthetic unavailable"; return false; }
    }
    // ---- Adopted verbatim from Mini-Actual-U4-Deadline-5aca-Counterexample. ----
    [Fact]
    public async System.Threading.Tasks.Task MiniReview_RunDeadlineReturnsWhileIdentityQueryIsHeld()
    {
        using var entered = new System.Threading.ManualResetEventSlim(false);
        using var release = new System.Threading.ManualResetEventSlim(false);
        var source = new MiniReviewHeldIdentitySource(entered, release);
        var root = System.IO.Directory.CreateTempSubdirectory("mini-u4-deadline-").FullName;
        var run = System.Threading.Tasks.Task.Run(() => CompanionHangCapture.Run(source, Target(), root, budget: TimeSpan.FromMilliseconds(50)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        bool returnedWithinBound;
        try
        {
            var completed = await System.Threading.Tasks.Task.WhenAny(run, System.Threading.Tasks.Task.Delay(300));
            returnedWithinBound = ReferenceEquals(completed, run);
        }
        finally { release.Set(); }
        _ = await run;
        Assert.True(returnedWithinBound, "External Run stayed blocked after its50msbudget while identitysourceheld300ms");
        Assert.True(CaptureDrain.WaitForIdle(TimeSpan.FromSeconds(10)));   // drain before the next serialized control
    }
    private sealed class MiniReviewHeldIdentitySource : IUiThreadStackSource
    {
        private readonly System.Threading.ManualResetEventSlim _entered, _release;
        public MiniReviewHeldIdentitySource(System.Threading.ManualResetEventSlim entered, System.Threading.ManualResetEventSlim release) { _entered=entered; _release=release; }
        public bool TryRevalidate(UiThreadTarget target,out string? failure) { _entered.Set(); _release.Wait(); failure=null; return true; }
        public bool TryCaptureWaitChain(UiThreadTarget target,int maxNodes,TimeSpan deadline,out IReadOnlyList<WaitChainNodeView> nodes,out bool truncated,out string? failure) { nodes=Array.Empty<WaitChainNodeView>(); truncated=false; failure="synthetic unavailable"; return false; }
        public bool TryCaptureStack(UiThreadTarget target,int maxFrames,TimeSpan deadline,out IReadOnlyList<StackFrameView> frames,out bool truncated,out string? failure) { frames=Array.Empty<StackFrameView>(); truncated=false; failure="synthetic unavailable"; return false; }
    }

    // ---- Adopted verbatim from Mini-Actual-U4-34fe-Counterexamples: the two FAILED cases. ----
    [Fact]
    public void MiniReview_U4IdentityFailureReceiptMustBeRedactedAndBounded()
    {
        var source = new FakeSource { Revalidate = false, RevalidateFailure = "C:\\Users\\alice\\private\\file " + new string('x', 10000) };
        var report = new UiThreadStackCollector(source).Collect(Target());
        var note = Assert.Single(report.Notes);
        Assert.DoesNotContain("alice", note);
        Assert.True(note.Length <= UiThreadStackCollector.MaxNameChars);
    }
    [Fact]
    public void MiniReview_U4CallerCannotRequestUnboundedNativeBufferSizes()
    {
        var source = new FakeSource();
        _ = new UiThreadStackCollector(source, maxWaitNodes: int.MaxValue, maxFrames: int.MaxValue).Collect(Target());
        Assert.InRange(source.LastWaitMaxNodes, 1, UiThreadStackCollector.DefaultMaxWaitNodes);
        Assert.InRange(source.LastStackMaxFrames, 1, UiThreadStackCollector.DefaultMaxFrames);
    }

    private static UiThreadTarget Target() => new(ProcessId: 4321, ProcessBirthTimeUtcTicks: 638_000_000_000_000_000L, ThreadId: 77);

    [Fact]
    public void IdentityMismatch_recycled_pid_is_never_captured()
    {
        var source = new FakeSource { Revalidate = false, RevalidateFailure = "process birth time differs (pid reused)" };
        var collector = new UiThreadStackCollector(source);
        var report = collector.Collect(Target());

        Assert.Equal(UiThreadCaptureStatus.IdentityMismatch, report.Status);
        Assert.Empty(report.WaitChain);
        Assert.Empty(report.Frames);
        Assert.Contains("reused", string.Join(" ", report.Notes));
        Assert.Equal(0, source.WaitCalls);   // a mismatched identity is never captured
    }

    [Fact]
    public void Collects_and_truncates_to_the_bounds()
    {
        var source = new FakeSource
        {
            WaitNodes = Enumerable.Range(0, 5).Select(i => new WaitChainNodeView(100 + i, 4321, "Blocked", "core.dll", "Wait" + i)).ToArray(),
            Frames = Enumerable.Range(0, 5).Select(i => new StackFrameView("core.dll", "Frame" + i)).ToArray(),
        };
        var collector = new UiThreadStackCollector(source, maxWaitNodes: 2, maxFrames: 3, deadline: TimeSpan.FromSeconds(2));
        var report = collector.Collect(Target());

        Assert.Equal(UiThreadCaptureStatus.Collected, report.Status);
        Assert.Equal(2, report.WaitChain.Count);
        Assert.Equal(3, report.Frames.Count);
        Assert.True(report.Truncated);
        Assert.Equal(2, source.LastWaitMaxNodes);   // the bound is forwarded to the native source
        Assert.Equal(3, source.LastStackMaxFrames);
    }

    [Fact]
    public void Unavailable_capture_preserves_the_failure_receipt()
    {
        var source = new FakeSource { WaitFailure = "wait-chain API not available", StackFailure = "stack symbolization requires an approved native host" };
        var report = new UiThreadStackCollector(source).Collect(Target());

        Assert.Equal(UiThreadCaptureStatus.Unavailable, report.Status);
        Assert.Empty(report.WaitChain);
        Assert.Contains("wait-chain API not available", string.Join(" ", report.Notes));
        Assert.Contains("approved native host", string.Join(" ", report.Notes));
    }

    [Fact]
    public void Throwing_source_does_not_throw_and_records_a_failed_receipt()
    {
        var source = new FakeSource { ThrowWait = true, ThrowStack = true };
        var report = new UiThreadStackCollector(source).Collect(Target());   // must NOT throw

        Assert.Equal(UiThreadCaptureStatus.Unavailable, report.Status);
        Assert.Contains("failed", string.Join(" ", report.Notes));
    }

    [Fact]
    public void Names_are_redacted_and_bounded()
    {
        var longFunction = new string('f', 500);
        var source = new FakeSource
        {
            WaitNodes = new[] { new WaitChainNodeView(100, 4321, "Blocked", "C:\\Users\\alice\\secret-app\\core.dll", longFunction) },
        };
        var report = new UiThreadStackCollector(source).Collect(Target());

        var node = Assert.Single(report.WaitChain);
        Assert.DoesNotContain("alice", node.Module ?? string.Empty);
        Assert.True((node.Function ?? string.Empty).Length < 500);
    }

    // The ACTUAL caller seam: the existing bundle builder invokes the collector and emits the redacted section.
    [Fact]
    public void Bundle_caller_seam_emits_the_collector_section()
    {
        var source = new FakeSource
        {
            WaitNodes = new[] { new WaitChainNodeView(77, 4321, "Blocked", "core.dll", "WaitForSingleObject") },
            Frames = new[] { new StackFrameView("tray.dll", "OnButtonClick") },
        };
        var collector = new UiThreadStackCollector(source);
        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(), connectionEvents: null,
            paths: new DiagnosticsBundlePaths(null, null, null, null, null),
            hangCollector: collector, hangTarget: Target());

        Assert.Contains("## UI Thread Stack & Wait Chain", bundle);
        Assert.Contains("WaitForSingleObject", bundle);
        Assert.Contains("OnButtonClick", bundle);
        Assert.Equal(1, source.WaitCalls);   // the seam actually called the collector
    }




/// <summary>U4 isolated diagnostic host entrypoint controls (arg parsing + real receipt via a fake source).</summary>
[Collection("native-capture")]
public class HangCaptureHostTests
{
    [Fact]
    public void Valid_args_write_a_receipt_and_return_zero()
    {
        var root = Directory.CreateTempSubdirectory("oc-u4-host-").FullName;
        var writer = new StringWriter();
        var result = HangCaptureHost.Run(
            new[] { "--pid", "4321", "--birth", "638000000000000000", "--thread", "77", "--dir", root },
            new FakeSource(), writer);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ReceiptPath);
        Assert.True(File.Exists(result.ReceiptPath!));
    }

    [Fact]
    public void Missing_required_args_return_usage_error()
    {
        var writer = new StringWriter();
        var result = HangCaptureHost.Run(new[] { "--pid", "4321" }, new FakeSource(), writer);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--birth", writer.ToString());
    }

    [Fact]
    public void Unknown_argument_is_rejected()
    {
        var writer = new StringWriter();
        var result = HangCaptureHost.Run(
            new[] { "--pid", "1", "--birth", "0", "--thread", "2", "--dir", "x", "--bogus", "1" },
            new FakeSource(), writer);
        Assert.Equal(2, result.ExitCode);
    }

    private sealed class FakeSource : IUiThreadStackSource
    {
        public bool TryRevalidate(UiThreadTarget target, out string? failure) { failure = null; return true; }
        public bool TryCaptureWaitChain(UiThreadTarget target, int maxNodes, TimeSpan deadline, out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
        { nodes = new[] { new WaitChainNodeView(77, 4321, "Blocked", "core.dll", "Wait") }; truncated = false; failure = null; return true; }
        public bool TryCaptureStack(UiThreadTarget target, int maxFrames, TimeSpan deadline, out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
        { frames = Array.Empty<StackFrameView>(); truncated = false; failure = "unavailable"; return false; }
    }
}

/// <summary>
/// U4 external entrypoint + bounded exclusive redacted writer controls (real filesystem, isolated temp dirs).
/// No native UI/native execution runs here.
/// </summary>
[Collection("native-capture")]
public class CompanionHangCaptureTests
{
    private static string NewRoot() => Directory.CreateTempSubdirectory("oc-u4-hang-").FullName;

    private static UiThreadTarget Target() => new(ProcessId: 4321, ProcessBirthTimeUtcTicks: 638_000_000_000_000_000L, ThreadId: 77);

    [Fact]
    public void Writes_a_redacted_bounded_receipt()
    {
        var root = NewRoot();
        var source = new FakeSource
        {
            WaitNodes = new[] { new WaitChainNodeView(77, 4321, "Blocked", "core.dll", "WaitForSingleObject") },
        };
        var receipt = CompanionHangCapture.Run(source, Target(), root);

        Assert.True(receipt.Written);
        Assert.NotNull(receipt.Path);
        var text = File.ReadAllText(receipt.Path!);
        Assert.Contains("WaitForSingleObject", text);
        Assert.True(text.Length <= CompanionHangCapture.MaxReceiptChars);
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public void Never_overwrites_an_existing_receipt()
    {
        var root = NewRoot();
        var source = new FakeSource();
        var first = CompanionHangCapture.Run(source, Target(), root, now: DateTimeOffset.UnixEpoch);
        var second = CompanionHangCapture.Run(source, Target(), root, now: DateTimeOffset.UnixEpoch);

        Assert.True(first.Written);
        Assert.True(second.Written);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, Directory.GetFiles(root).Length);   // both receipts preserved
    }

    [Fact]
    public void Identity_mismatch_still_writes_a_redacted_receipt()
    {
        var root = NewRoot();
        var source = new FakeSource { Revalidate = false, RevalidateFailure = "C:\\Users\\alice\\private\\file " + new string('x', 10000) };
        var receipt = CompanionHangCapture.Run(source, Target(), root);

        Assert.True(receipt.Written);
        Assert.Equal(UiThreadCaptureStatus.IdentityMismatch, receipt.Status);
        var text = File.ReadAllText(receipt.Path!);
        Assert.DoesNotContain("alice", text);
    }

    private sealed class FakeSource : IUiThreadStackSource
    {
        public bool Revalidate = true;
        public string? RevalidateFailure;
        public IReadOnlyList<WaitChainNodeView> WaitNodes = Array.Empty<WaitChainNodeView>();

        public bool TryRevalidate(UiThreadTarget target, out string? failure)
        {
            failure = RevalidateFailure;
            return Revalidate;
        }

        public bool TryCaptureWaitChain(
            UiThreadTarget target, int maxNodes, TimeSpan deadline,
            out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
        {
            truncated = false;
            failure = null;
            nodes = WaitNodes;
            return true;
        }

        public bool TryCaptureStack(
            UiThreadTarget target, int maxFrames, TimeSpan deadline,
            out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
        {
            truncated = false;
            failure = "stack unavailable";
            frames = Array.Empty<StackFrameView>();
            return false;
        }
    }
}

    private sealed class FakeSource : IUiThreadStackSource
    {
        public bool Revalidate = true;
        public string? RevalidateFailure;
        public IReadOnlyList<WaitChainNodeView> WaitNodes = Array.Empty<WaitChainNodeView>();
        public IReadOnlyList<StackFrameView> Frames = Array.Empty<StackFrameView>();
        public string? WaitFailure;
        public string? StackFailure;
        public bool ThrowWait;
        public bool ThrowStack;
        public int WaitCalls;
        public int LastWaitMaxNodes;
        public int LastStackMaxFrames;

        public bool TryRevalidate(UiThreadTarget target, out string? failure)
        {
            failure = RevalidateFailure;
            return Revalidate;
        }

        public bool TryCaptureWaitChain(
            UiThreadTarget target, int maxNodes, TimeSpan deadline,
            out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
        {
            WaitCalls++;
            LastWaitMaxNodes = maxNodes;
            truncated = false;
            failure = WaitFailure;
            if (ThrowWait) throw new InvalidOperationException("native wait capture crashed");
            nodes = WaitNodes;
            return WaitFailure is null;
        }

        public bool TryCaptureStack(
            UiThreadTarget target, int maxFrames, TimeSpan deadline,
            out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
        {
            LastStackMaxFrames = maxFrames;
            truncated = false;
            failure = StackFailure;
            if (ThrowStack) throw new InvalidOperationException("native stack capture crashed");
            frames = Frames;
            return StackFailure is null;
        }
    }
}