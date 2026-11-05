using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

public sealed class CleanupAppLockerCapture : IDisposable
{
    private readonly object gate = new object();
    private readonly AutoResetEvent changed = new AutoResetEvent(false);
    private readonly EventLogWatcher watcher;
    private Exception callbackError;
    private bool disposed;
    public readonly CleanupAppLockerEvidence Evidence;
    private long reconciledRecordId;
    private const int ReconciliationIntervalMilliseconds = 100;

    public CleanupAppLockerCapture(long minimumRecordId)
    {
        Evidence = new CleanupAppLockerEvidence(minimumRecordId);
        reconciledRecordId = minimumRecordId;
        var query = new EventLogQuery(CleanupAppLockerEvidence.Channel, PathType.LogName, Evidence.Query);
        // Replay only records after the caller's boundary, including subscription startup.
        watcher = new EventLogWatcher(query, null, true);
        watcher.EventRecordWritten += OnRecord;
        try { watcher.Enabled = true; }
        catch { watcher.Dispose(); changed.Dispose(); throw; }
    }

    private void OnRecord(object sender, EventRecordWrittenEventArgs args)
    {
        lock (gate)
        {
            try
            {
                using (var record = args.EventRecord)
                {
                    if (disposed) return;
                    if (args.EventException != null) throw args.EventException;
                    if (record != null) Evidence.Observe(record.ToXml());
                }
            }
            catch (Exception error) { if (!disposed) callbackError = error; }
            finally { if (!disposed) changed.Set(); }
        }
    }

    private string WaitFor(Func<string> read, int timeoutMilliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("CleanupAppLockerCapture");
                if (callbackError != null) throw new InvalidOperationException("AppLocker event subscription failed.", callbackError);
            }
            var result = Evidence.ReconcileAndRead(QueryAvailable, read);
            int remaining = timeoutMilliseconds - checked((int)clock.ElapsedMilliseconds);
            if (remaining <= 0) throw new TimeoutException("No qualified AppLocker event within the unchanged observation budget.");
            if (result.Length != 0) return result;
            changed.WaitOne(Math.Min(remaining, ReconciliationIntervalMilliseconds));
        }
    }

    private IEnumerable<string> QueryAvailable()
    {
        var query = new EventLogQuery(CleanupAppLockerEvidence.Channel, PathType.LogName,
            Evidence.QueryAfter(reconciledRecordId));
        using (var reader = new EventLogReader(query))
        {
            EventRecord record;
            while ((record = reader.ReadEvent(TimeSpan.Zero)) != null)
            {
                using (record)
                {
                    // A readable record must not depend on live subscription notification latency.
                    yield return record.ToXml();
                    reconciledRecordId = Math.Max(reconciledRecordId, record.RecordId.GetValueOrDefault());
                }
            }
        }
    }

    public void WaitForPolicyApplied(int timeoutMilliseconds)
    {
        WaitFor(delegate
        {
            Evidence.RequireHealthyPolicy();
            return Evidence.PolicyApplied ? "applied" : string.Empty;
        }, timeoutMilliseconds);
    }

    public string WaitForExecution(string path, int eventId, string actorSid, string systemDrive,
        string ruleId, int timeoutMilliseconds)
    {
        return WaitFor(delegate { return Evidence.FindExecution(path, eventId, actorSid, systemDrive, ruleId); },
            timeoutMilliseconds);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        watcher.Enabled = false;
        watcher.Dispose();
        lock (gate) changed.Dispose();
    }
}
