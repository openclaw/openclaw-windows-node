namespace OpenClaw.Connection;

/// <summary>Provenance of the process accepting connections for a gateway endpoint.</summary>
public enum GatewayEndpointProvenanceKind
{
    /// <summary>The probe does not apply (for example, a non-loopback remote endpoint).</summary>
    NotApplicable,

    /// <summary>No listener currently owns the endpoint.</summary>
    NoListener,

    /// <summary>The listener is the expected OS-owned WSL relay for this managed gateway.</summary>
    ExpectedManagedGateway,

    /// <summary>A fully proven, obsolete native OpenClaw gateway owns the WSL gateway endpoint.</summary>
    ConflictingOpenClawGateway,

    /// <summary>A listener exists, but its ownership cannot be proven safe.</summary>
    UnknownListener,
}

/// <summary>
/// Address-specific endpoint provenance. Process/task details are diagnostics only; no credential
/// material is ever included.
/// </summary>
public sealed record GatewayEndpointProvenance(
    GatewayEndpointProvenanceKind Kind,
    int Port,
    int? ProcessId = null,
    string? ProcessName = null,
    DateTime? ProcessStartTimeUtc = null,
    string? ProcessPath = null,
    string? ScheduledTaskName = null,
    string? Detail = null);
