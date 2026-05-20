using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace OpenClawTray.Services;

internal static class SingleInstanceLaunchGuard
{
    public const string DefaultMutexName = "OpenClawTray";
    public static readonly TimeSpan PackagedLaunchRetryTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PackagedLaunchRetryDelay = TimeSpan.FromMilliseconds(250);

    public enum AcquisitionStatus
    {
        Acquired,
        AlreadyRunning,
        AcquiredAfterWait,
        TimedOut
    }

    public sealed class AcquisitionResult
    {
        public AcquisitionResult(Mutex? mutex, AcquisitionStatus status, int attempts)
        {
            Mutex = mutex;
            Status = status;
            Attempts = attempts;
        }

        public Mutex? Mutex { get; }
        public AcquisitionStatus Status { get; }
        public int Attempts { get; }
        public bool HasMutex => Mutex is not null;
    }

    public static string BuildMutexName(string? dataDirOverride)
    {
        if (dataDirOverride is null)
            return DefaultMutexName;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataDirOverride));
        return $"{DefaultMutexName}-{Convert.ToHexString(hash, 0, 4)}";
    }

    public static AcquisitionResult Acquire(
        string mutexName,
        bool retryWhenBusy,
        TimeSpan retryTimeout,
        TimeSpan retryDelay,
        Action<string>? trace = null,
        Action<TimeSpan>? delay = null)
    {
        if (retryTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryTimeout), retryTimeout, "Retry timeout cannot be negative.");
        if (retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay), retryDelay, "Retry delay must be positive.");

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;

        while (true)
        {
            var mutex = new Mutex(true, mutexName, out var createdNew);
            if (createdNew)
            {
                var status = attempts == 0 ? AcquisitionStatus.Acquired : AcquisitionStatus.AcquiredAfterWait;
                trace?.Invoke(attempts == 0 ? "acquired" : $"acquired-after-wait attempts={attempts}");
                return new AcquisitionResult(mutex, status, attempts);
            }

            // Do not keep a non-owning handle open while waiting. Holding these
            // handles can keep the kernel mutex object alive after the old process
            // exits, causing the retry loop to time out even though launch is safe.
            mutex.Dispose();

            if (!retryWhenBusy)
            {
                trace?.Invoke("already-running");
                return new AcquisitionResult(null, AcquisitionStatus.AlreadyRunning, attempts);
            }

            if (stopwatch.Elapsed >= retryTimeout)
            {
                trace?.Invoke($"timed-out attempts={attempts}");
                return new AcquisitionResult(null, AcquisitionStatus.TimedOut, attempts);
            }

            attempts++;
            trace?.Invoke($"busy-retry attempts={attempts}");

            var remaining = retryTimeout - stopwatch.Elapsed;
            var sleepFor = remaining < retryDelay ? remaining : retryDelay;
            if (sleepFor <= TimeSpan.Zero)
                continue;

            if (delay is null)
                Thread.Sleep(sleepFor);
            else
                delay(sleepFor);
        }
    }
}
