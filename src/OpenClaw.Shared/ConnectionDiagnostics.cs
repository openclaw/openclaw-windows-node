using System;
using System.Collections.Generic;

namespace OpenClaw.Shared;

/// <summary>
/// A single diagnostic event from the connection state machine.
/// </summary>
public sealed record ConnectionEvent(
    DateTime Timestamp,
    string Direction,     // "→ GW", "← GW", "→ WSL", "INTERNAL"
    string EventType,     // "CONNECT", "CHALLENGE", "AUTH", "HELLO_OK", "ERROR",
                          // "PAIRED", "RECONNECT", "DISCONNECT", "AUTO_APPROVE",
                          // "SIGNATURE_REJECTED", "PAIRING_REQUIRED", "STATE_CHANGE"
    string Summary,
    string? Detail = null,
    string? TokenType = null,   // "shared", "bootstrap", "device", null
    string[]? Scopes = null);

/// <summary>
/// Thread-safe ring buffer of connection diagnostic events.
/// UI subscribes to <see cref="EventRecorded"/> for real-time updates.
/// </summary>
public sealed class ConnectionDiagnostics
{
    private readonly object _lock = new();
    private readonly LinkedList<ConnectionEvent> _events = new();
    private readonly int _maxEvents;

    public event EventHandler<ConnectionEvent>? EventRecorded;

    public ConnectionDiagnostics(int maxEvents = 500)
    {
        _maxEvents = maxEvents;
    }

    public void Record(string direction, string eventType, string summary,
        string? detail = null, string? tokenType = null, string[]? scopes = null)
    {
        var evt = new ConnectionEvent(
            DateTime.Now, direction, eventType, summary, detail, tokenType, scopes);

        lock (_lock)
        {
            _events.AddFirst(evt);
            while (_events.Count > _maxEvents)
                _events.RemoveLast();
        }

        EventRecorded?.Invoke(this, evt);
    }

    /// <summary>Returns a snapshot of events (newest first).</summary>
    public IReadOnlyList<ConnectionEvent> GetSnapshot()
    {
        lock (_lock)
        {
            return new List<ConnectionEvent>(_events);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    public int Count
    {
        get { lock (_lock) { return _events.Count; } }
    }

    // ── Convenience methods ──

    public void RecordConnect(string url, string? tokenType)
        => Record("→ GW", "CONNECT", $"Connecting to {url}", tokenType: tokenType);

    public void RecordChallenge(string nonce)
        => Record("← GW", "CHALLENGE", $"Challenge received", detail: $"nonce={nonce}");

    public void RecordAuthSent(string tokenType, string[]? requestedScopes, string signatureMode, string? deviceId)
        => Record("→ GW", "AUTH", $"Auth sent ({tokenType}, sig={signatureMode})",
            detail: $"deviceId={deviceId}\nscopes=[{string.Join(", ", requestedScopes ?? Array.Empty<string>())}]",
            tokenType: tokenType, scopes: requestedScopes);

    public void RecordHelloOk(string[]? grantedScopes, string? deviceToken)
        => Record("← GW", "HELLO_OK", "Connected",
            detail: deviceToken != null ? $"deviceToken={deviceToken[..Math.Min(10, deviceToken.Length)]}..." : null,
            scopes: grantedScopes);

    public void RecordSignatureRejected(string mode, string fallback)
        => Record("← GW", "SIGNATURE_REJECTED", $"Signature rejected ({mode} → {fallback})");

    public void RecordPairingRequired(string? requestId, string? reason)
        => Record("← GW", "PAIRING_REQUIRED", $"Pairing required: {reason ?? "unknown"}",
            detail: requestId != null ? $"requestId={requestId}" : null);

    public void RecordAuthFailed(string message)
        => Record("← GW", "ERROR", $"Auth failed: {message}");

    public void RecordDisconnect(string? reason)
        => Record("INTERNAL", "DISCONNECT", reason ?? "Disconnected");

    public void RecordStateChange(string role, string fromState, string toState)
        => Record("INTERNAL", "STATE_CHANGE", $"{role}: {fromState} → {toState}");

    public void RecordAutoApprove(string requestId)
        => Record("→ WSL", "AUTO_APPROVE", $"Auto-approving device", detail: $"requestId={requestId}");

    public void RecordNodeConnect(string url, string? tokenType)
        => Record("→ GW", "NODE_CONNECT", $"Node connecting to {url}", tokenType: tokenType);

    public void RecordNodePaired(string? deviceId)
        => Record("← GW", "NODE_PAIRED", $"Node paired", detail: deviceId != null ? $"deviceId={deviceId[..Math.Min(16, deviceId.Length)]}..." : null);

    public void RecordCredentialResolved(string source, string? tokenType, bool isBootstrap)
        => Record("INTERNAL", "CREDENTIAL_RESOLVED", $"Using {source} (bootstrap={isBootstrap})", tokenType: tokenType);
}
