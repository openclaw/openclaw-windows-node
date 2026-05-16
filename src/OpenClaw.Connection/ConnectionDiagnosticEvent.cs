namespace OpenClaw.Connection;

/// <summary>
/// Timestamped diagnostic event for the connection diagnostics ring buffer.
/// </summary>
public sealed record ConnectionDiagnosticEvent(
    DateTime Timestamp,
    string Category,     // "state", "credential", "websocket", "pairing", "node", "error"
    string Message,
    string? Detail);
