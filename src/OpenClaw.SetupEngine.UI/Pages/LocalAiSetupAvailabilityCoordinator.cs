namespace OpenClaw.SetupEngine.UI.Pages;

internal enum LocalAiSetupAvailabilityStatus
{
    Checking,
    Unknown,
    Available,
    Unsupported,
}

internal readonly record struct LocalAiSetupAvailabilitySnapshot(
    int Generation,
    LocalAiSetupAvailabilityStatus Status,
    string? Reason)
{
    public bool IsChecking => Status == LocalAiSetupAvailabilityStatus.Checking;
    public bool IsUnknown => Status == LocalAiSetupAvailabilityStatus.Unknown;
    public bool IsAvailable => Status == LocalAiSetupAvailabilityStatus.Available;
    public bool IsUnsupported => Status == LocalAiSetupAvailabilityStatus.Unsupported;
    public bool CanRecheck => Status == LocalAiSetupAvailabilityStatus.Unknown;
}

internal sealed class LocalAiSetupAvailabilityCoordinator
{
    private int _generation;
    private LocalAiSetupAvailabilitySnapshot _current;

    public LocalAiSetupAvailabilitySnapshot Current => _current;

    public LocalAiSetupAvailabilitySnapshot StartProbe()
    {
        int generation = unchecked(++_generation);
        _current = new(generation, LocalAiSetupAvailabilityStatus.Checking, Reason: null);
        return _current;
    }

    public bool TryStartRecheck(out LocalAiSetupAvailabilitySnapshot snapshot)
    {
        if (!_current.CanRecheck)
        {
            snapshot = _current;
            return false;
        }

        snapshot = StartProbe();
        return true;
    }

    public bool TryApplyAvailable(int generation, out LocalAiSetupAvailabilitySnapshot snapshot) =>
        TryApply(generation, LocalAiSetupAvailabilityStatus.Available, reason: null, out snapshot);

    public bool TryApplyUnsupported(
        int generation,
        string reason,
        out LocalAiSetupAvailabilitySnapshot snapshot) =>
        TryApply(generation, LocalAiSetupAvailabilityStatus.Unsupported, reason, out snapshot);

    public bool TryApplyProbeFailure(
        int generation,
        string reason,
        out LocalAiSetupAvailabilitySnapshot snapshot) =>
        TryApply(generation, LocalAiSetupAvailabilityStatus.Unknown, reason, out snapshot);

    public void CancelCurrent()
    {
        unchecked { _generation++; }
    }

    public bool IsCurrent(int generation) => generation == _generation;

    private bool TryApply(
        int generation,
        LocalAiSetupAvailabilityStatus status,
        string? reason,
        out LocalAiSetupAvailabilitySnapshot snapshot)
    {
        if (!IsCurrent(generation))
        {
            snapshot = _current;
            return false;
        }

        _current = new(generation, status, reason);
        snapshot = _current;
        return true;
    }
}
