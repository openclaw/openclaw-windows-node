using System;
using System.Collections.Concurrent;
using System.IO;

public sealed class CleanupVisibilityRunState
{
    public const int RequiredWindowSamplesAfterReady = 20;
    public const int RequiredTaskbarSamplesAfterReady = 2;
    private readonly ConcurrentDictionary<int, double> externalStarts = new ConcurrentDictionary<int, double>();
    private volatile bool workerReady;
    private string expectedHostPath = string.Empty;

    public int? RootExitCode { get; private set; }
    public bool WorkerReady { get { return workerReady; } }
    public bool Released { get; private set; }
    public bool CaptureUnownedStartupSamples { get { return expectedHostPath.Length != 0 && !workerReady; } }

    public void ConfigureExternalHost(string path)
    {
        expectedHostPath = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);
    }

    public void MarkWorkerReady()
    {
        if (expectedHostPath.Length != 0)
            throw new InvalidOperationException("The actual external cleanup host must be registered before readiness.");
        workerReady = true;
    }

    public void RegisterExternalWorker(int workerPid, int hostPid, string actualHostPath,
        double hostCreatedElapsedMs, double workerCreatedElapsedMs)
    {
        if (expectedHostPath.Length == 0 || !string.Equals(expectedHostPath, Path.GetFullPath(actualHostPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External worker parent is not the expected cleanup host.");
        if (workerPid <= 0 || hostPid <= 0 || workerPid == hostPid ||
            double.IsNaN(hostCreatedElapsedMs) || double.IsNaN(workerCreatedElapsedMs) ||
            double.IsInfinity(hostCreatedElapsedMs) || double.IsInfinity(workerCreatedElapsedMs) ||
            hostCreatedElapsedMs < 0 || workerCreatedElapsedMs < hostCreatedElapsedMs)
            throw new InvalidOperationException("External worker/host process identities are not from this probe.");
        externalStarts[hostPid] = hostCreatedElapsedMs;
        externalStarts[workerPid] = workerCreatedElapsedMs;
        workerReady = true;
    }

    public bool CanAttributeObservation(int pid, double observedElapsedMs)
    {
        double createdElapsedMs;
        return !externalStarts.TryGetValue(pid, out createdElapsedMs) || observedElapsedMs >= createdElapsedMs;
    }

    public bool IsExternalProcess(int pid)
    {
        return externalStarts.ContainsKey(pid);
    }

    public bool CanRelease(int windowSamplesAfterReady, int taskbarSamplesAfterReady, bool positiveControl,
        bool showEventSeen, bool sampledWindowSeen, bool taskbarButtonSeen)
    {
        return workerReady && !Released &&
            windowSamplesAfterReady >= RequiredWindowSamplesAfterReady &&
            taskbarSamplesAfterReady >= RequiredTaskbarSamplesAfterReady &&
            (!positiveControl || (showEventSeen && sampledWindowSeen && taskbarButtonSeen));
    }

    public void MarkReleased()
    {
        Released = true;
    }

    public void RecordRootExit(int exitCode)
    {
        RootExitCode = exitCode;
    }

    public void RequireCompleteHandshake()
    {
        if (!workerReady)
            throw new InvalidOperationException("Worker did not signal ready. Root native exit: " +
                (RootExitCode.HasValue ? RootExitCode.Value.ToString() : "unknown") + ".");
        if (!Released)
            throw new InvalidOperationException("Worker exited before synchronized observer release.");
    }
}
