using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

/// <summary>
/// One current-profile, short-lived completion. The exclusive file lease spans native presentation.
/// Only local verified completion creates a record; public activation supplies an opaque lookup handle.
/// </summary>
internal sealed class SetupDashboardHandoffStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly string _directory;
    private readonly TimeProvider _time;
    private string PendingPath => Path.Combine(_directory, "pending.json");
    private string LockPath => Path.Combine(_directory, "pending.lock");

    public SetupDashboardHandoffStore(string dataDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "setup-dashboard-handoff");
        _time = time ?? TimeProvider.System;
    }

    public string Issue(SetupNativeCompletion completion)
    {
        if (!completion.Target.Matches(completion.Verification))
            throw new SetupNativeOwnershipException();
        if (!IsValidCompletion(completion.Verification))
            throw new InvalidOperationException("AI completion has no verified Gateway binding.");
        Directory.CreateDirectory(_directory);
        RequireOwnedDirectory();
        using var gate = OpenGate();
        var handle = SetupDashboardHandoff.NativePrefix +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = _time.GetUtcNow();
        Write(new PendingRecord(Guid.NewGuid().ToString("N"), Hash(handle), completion.Verification,
            now, now + Lifetime, "ready", completion.Target));
        return handle;
    }

    public Lease? Acquire(string? handle, bool explicitRetry = false)
    {
        if (SetupDashboardHandoff.ParseHandle(handle) is null || !Directory.Exists(_directory))
            return null;
        RequireOwnedDirectory();
        FileStream gate;
        try { gate = OpenGate(); }
        catch (IOException) { return null; } // A concurrent launch owns the file lease; never queue another launch.
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            if ((File.GetAttributes(PendingPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The pending handoff is not an owned regular file.");
            using var input = File.OpenRead(PendingPath);
            if (input.Length > 16384)
                throw new InvalidDataException("The pending handoff is too large.");
            PendingRecord? pending;
            try { pending = JsonSerializer.Deserialize<PendingRecord>(input); }
            catch (JsonException) { return null; }
            input.Dispose();
            if (pending is null || !IsValidCompletion(pending.Completion) ||
                pending.NativeTarget is not { } target || !target.Matches(pending.Completion) ||
                pending.HandleHash != Hash(handle!) || !Guid.TryParseExact(pending.RunId, "N", out _) ||
                pending.ExpiresUtc - pending.IssuedUtc != Lifetime)
                return null;
            var now = _time.GetUtcNow();
            if (now < pending.IssuedUtc || now >= pending.ExpiresUtc)
            {
                File.Delete(PendingPath);
                return null;
            }
            if (pending.State != (explicitRetry ? "retry" : "ready"))
                return null;
            var inFlight = pending with { State = "inflight" };
            Write(inFlight);
            var lease = new Lease(this, gate, inFlight);
            gate = null!;
            return lease;
        }
        finally { gate?.Dispose(); }
    }

    private FileStream OpenGate() =>
        new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void RequireOwnedDirectory()
    {
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(LockPath) && (File.GetAttributes(LockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The pending handoff directory is not owned by this profile.");
    }

    private void Write(PendingRecord pending)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, pending);
                output.Flush(flushToDisk: true);
            }
            File.Move(path, PendingPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string Hash(string handle) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));

    private static bool IsValidCompletion(GatewayAiSetupCompletion? value) =>
        value is { VerifiedGeneration: > 0, ModelTarget: null } &&
        SetupCompletionAuthority.IsValid(value.IdentityBinding, value.SessionKey, value.AgentId) &&
        Enum.IsDefined(value.Intent) && !string.IsNullOrWhiteSpace(value.GatewayId) &&
        !string.IsNullOrWhiteSpace(value.ModelRef) &&
        value.EndpointBinding is { Length: 64 } binding && binding.All(Uri.IsHexDigit);

    internal sealed record PendingRecord(
        string RunId, string HandleHash, GatewayAiSetupCompletion Completion,
        DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, string State, SetupNativeTarget? NativeTarget = null);

    internal sealed class Lease : IDisposable
    {
        private readonly SetupDashboardHandoffStore _owner;
        private readonly FileStream _gate;
        private readonly PendingRecord _pending;
        private bool _settled;
        private bool _disposed;
        public GatewayAiSetupCompletion Completion => _pending.Completion;
        public SetupNativeTarget? NativeTarget => _pending.NativeTarget;
        public bool IsExpired => _owner._time.GetUtcNow() >= _pending.ExpiresUtc ||
            _owner._time.GetUtcNow() < _pending.IssuedUtc;

        internal Lease(SetupDashboardHandoffStore owner, FileStream gate, PendingRecord pending)
        {
            _owner = owner;
            _gate = gate;
            _pending = pending;
        }

        public void Consume()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_settled)
                throw new InvalidOperationException("The pending handoff was already settled.");
            File.Delete(_owner.PendingPath);
            _settled = true;
        }

        public void RetainForExplicitRetry()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_settled)
                throw new InvalidOperationException("The pending handoff was already settled.");
            if (IsExpired)
                File.Delete(_owner.PendingPath);
            else
                _owner.Write(_pending with { State = "retry" });
            _settled = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
            // An interrupted/unsettled launch remains inflight until expiry, never replayable.
        }
    }
}
