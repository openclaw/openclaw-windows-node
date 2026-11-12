using OpenClaw.Shared.Mxc;

namespace OpenClawTray.Presentation;

internal enum SandboxStatus
{
    Checking,
    Available,
    Compatibility,
    Unavailable,
    Disabled,
}

internal sealed record SandboxStatusSnapshot(long Version, SandboxStatus Status);

/// <summary>
/// Publishes existing app/settings-page probe results. This store neither probes nor
/// authorizes execution; the MXC runner independently admits each command.
/// </summary>
internal sealed class SandboxStatusStore
{
    private readonly object _gate = new();
    private SandboxStatusSnapshot _current = new(0, SandboxStatus.Checking);

    public SandboxStatusSnapshot Current => Volatile.Read(ref _current);
    public event EventHandler<SandboxStatusSnapshot>? Changed;

    public void Publish(MxcAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var status = availability.IsWindowsUnsupported ? SandboxStatus.Compatibility
            : availability.CanRunSystemRunSandbox ? SandboxStatus.Available
            : SandboxStatus.Unavailable;
        SandboxStatusSnapshot snapshot;
        lock (_gate)
        {
            if (_current.Status == status)
                return;
            snapshot = new(_current.Version + 1, status);
            Volatile.Write(ref _current, snapshot);
        }
        Changed?.Invoke(this, snapshot);
    }
}
