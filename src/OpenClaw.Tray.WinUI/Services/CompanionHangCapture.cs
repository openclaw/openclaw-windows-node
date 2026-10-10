using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>Outcome of one external hang-capture attempt and where its local receipt was written.</summary>
internal sealed record HangCaptureReceipt(string? Path, UiThreadCaptureStatus Status, bool Written, string? Failure);

/// <summary>
/// EXTERNAL entrypoint for a bounded UI-thread stack + wait-chain capture of an EXACT target (pid + process
/// creation ticks + thread id).
/// <para>
/// ADMISSION: an exclusive capture admission is taken for the target identity BEFORE any worker is launched and
/// is held until the PHYSICAL worker drains (including across a caller timeout). A busy call returns a bounded
/// failure receipt immediately and launches NO worker, so repeated timeout captures cannot accumulate concurrent
/// held identity-query workers. The admission is striped by target identity so exclusion also covers separate
/// source instances pointed at the same target process; the stripe table is FIXED-SIZE (bounded memory).
/// </para>
/// <para>
/// DEADLINE SCOPE: the budget bounds WAITING on the capture worker, not the local receipt file IO, which can
/// block and is therefore NOT covered by a hard filesystem deadline. No auto-dump/kill/restart/listener/telemetry.
/// </para>
/// </summary>
internal static class CompanionHangCapture
{
    internal const int MaxReceiptChars = 128 * 1024;
    internal const int MaxReceiptAttempts = 64;

    // Fixed-size striped admission: bounds outstanding outer capture workers by TARGET identity (across source
    // instances too) without an unbounded map. Two distinct targets may share a stripe; that only makes exclusion
    // more conservative, never less.
    private const int AdmissionStripes = 64;
    private static readonly SemaphoreSlim[] Admissions =
        Enumerable.Range(0, AdmissionStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static HangCaptureReceipt Run(
        IUiThreadStackSource source,
        UiThreadTarget target,
        string receiptDirectory,
        int maxWaitNodes = UiThreadStackCollector.DefaultMaxWaitNodes,
        int maxFrames = UiThreadStackCollector.DefaultMaxFrames,
        TimeSpan? budget = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(receiptDirectory)) throw new ArgumentException("receiptDirectory required", nameof(receiptDirectory));

        var boundedBudget = UiThreadStackCollector.ClampDeadline(budget);
        var stripe = StripeFor(target);

        // ADMIT BEFORE LAUNCHING: a busy target must not start another held identity worker.
        if (!Admissions[stripe].Wait(TimeSpan.Zero))
        {
            var busy = new UiThreadStackReport(
                UiThreadCaptureStatus.Unavailable, target.Describe(),
                Array.Empty<WaitChainNodeView>(), Array.Empty<StackFrameView>(), false,
                new[] { "an outer capture worker is already active for this target; no new worker launched" });
            var busyText = Cap(UiThreadStackCollector.FormatSection(busy));
            var busyPath = WriteExclusive(receiptDirectory, target, now ?? DateTimeOffset.UtcNow, busyText, out var busyFailure);
            return new HangCaptureReceipt(busyPath, UiThreadCaptureStatus.Unavailable, busyPath is not null,
                busyFailure ?? "capture admission busy for this target");
        }

        var collector = new UiThreadStackCollector(source, maxWaitNodes, maxFrames, budget);
        CaptureDrain.Register();   // host Main must not exit while this worker still drains
        var worker = Task.Run(() => collector.Collect(target));
        _ = worker.ContinueWith(
            drained => { _ = drained.Exception; CaptureDrain.Complete(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        UiThreadStackReport report;
        try
        {
            if (worker.Wait(boundedBudget))
            {
                try
                {
                    report = worker.Result;
                }
                catch (Exception exception)
                {
                    report = FailureReport(target, exception);
                }
                Admissions[stripe].Release();   // the worker physically drained
            }
            else
            {
                // TIMEOUT: the caller returns now, but the admission is HELD until the worker physically drains,
                // and the worker is OBSERVED (its exception is read; it is never late-published).
                _ = worker.ContinueWith(
                    drained => { _ = drained.Exception; Admissions[stripe].Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                report = TimeoutReport(target);
                var timeoutText = Cap(UiThreadStackCollector.FormatSection(report));
                var timeoutPath = WriteExclusive(receiptDirectory, target, now ?? DateTimeOffset.UtcNow, timeoutText, out var timeoutFailure);
                return new HangCaptureReceipt(timeoutPath, UiThreadCaptureStatus.Unavailable, timeoutPath is not null,
                    timeoutFailure ?? "capture exceeded the external deadline (owned worker still draining)");
            }
        }
        catch
        {
            Admissions[stripe].Release();
            throw;
        }

        var text = Cap(UiThreadStackCollector.FormatSection(report));
        var path = WriteExclusive(receiptDirectory, target, now ?? DateTimeOffset.UtcNow, text, out var failure);
        return new HangCaptureReceipt(path, report.Status, path is not null, failure);
    }

    private static int StripeFor(UiThreadTarget target)
    {
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"{target.ProcessId}:{target.ProcessBirthTimeUtcTicks}");
        var hash = 0;
        foreach (var c in identity) hash = unchecked(hash * 31 + c);
        return (hash & int.MaxValue) % AdmissionStripes;
    }

    private static string Cap(string text) => text.Length > MaxReceiptChars ? text[..MaxReceiptChars] : text;

    private static UiThreadStackReport FailureReport(UiThreadTarget target, Exception exception) =>
        new(UiThreadCaptureStatus.Unavailable, target.Describe(),
            Array.Empty<WaitChainNodeView>(), Array.Empty<StackFrameView>(), false,
            new[] { $"capture failed: {exception.GetType().Name}" });

    private static UiThreadStackReport TimeoutReport(UiThreadTarget target) =>
        new(UiThreadCaptureStatus.Unavailable, target.Describe(),
            Array.Empty<WaitChainNodeView>(), Array.Empty<StackFrameView>(), false,
            new[] { "capture exceeded the external deadline; owned worker still draining" });

    private static string? WriteExclusive(
        string directory, UiThreadTarget target, DateTimeOffset now, string text, out string? failure)
    {
        failure = null;
        try
        {
            Directory.CreateDirectory(directory);
            var baseName = string.Create(CultureInfo.InvariantCulture,
                $"hang-{now:yyyyMMddTHHmmssZ}-pid{target.ProcessId}");
            var expectedBytes = Encoding.UTF8.GetByteCount(text);
            for (var attempt = 0; attempt < MaxReceiptAttempts; attempt++)
            {
                var candidate = Path.Combine(directory, attempt == 0 ? baseName + ".txt" : baseName + "-" + attempt + ".txt");
                if (File.Exists(candidate)) continue;   // never overwrite an existing receipt
                var temp = candidate + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, text);
                    if (new FileInfo(temp).Length != expectedBytes)
                    {
                        PreserveFailed(temp);
                        failure = "receipt write was short; artifact preserved";
                        return null;
                    }
                    File.Move(temp, candidate, overwrite: false);   // exclusive create: collision-safe
                    return candidate;
                }
                catch (Exception) when (File.Exists(candidate))
                {
                    // NAME COLLISION (another writer won the race): not a write failure.
                    TryDelete(temp);
                }
                catch (Exception exception)
                {
                    // REAL write failure: PRESERVE the artifact and report; never silently delete it.
                    PreserveFailed(temp);
                    failure = $"receipt could not be written: {exception.GetType().Name}";
                    return null;
                }
            }
            failure = $"no exclusive receipt name was available after {MaxReceiptAttempts} attempts";
            return null;
        }
        catch (Exception exception)
        {
            failure = $"receipt could not be written: {exception.GetType().Name}";
            return null;
        }
    }

    private static void PreserveFailed(string temp)
    {
        try { if (File.Exists(temp)) File.Move(temp, temp + ".failed", overwrite: false); }
        catch { /* best effort; the artifact stays in place if it cannot be renamed */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}