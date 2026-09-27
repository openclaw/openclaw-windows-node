using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Shared;

/// <summary>Coordinates synchronous cooperating writers from final read/CAS through replacement.
/// Acquire instance locks first; do not await or invoke application callbacks while holding this lease.</summary>
public static class PersistenceFileLease
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IDisposable Acquire(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var gate = Gates.GetOrAdd(normalized, static _ => new object());
        Monitor.Enter(gate);
        Mutex? mutex = null;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            mutex = new Mutex(false, (OperatingSystem.IsWindows() ? @"Global\" : "") + "OpenClaw.Persistence." + hash);
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out waiting for the settings or registry writer lease.");
            return new Lease(gate, mutex);
        }
        catch
        {
            mutex?.Dispose();
            Monitor.Exit(gate);
            throw;
        }
    }

    private sealed class Lease(object gate, Mutex mutex) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); Monitor.Exit(gate); }
        }
    }
}
