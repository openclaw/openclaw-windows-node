namespace OpenClaw.SetupEngine;

/// <summary>Serializes wizard answers and prevents an invalidated lease from releasing a newer answer.</summary>
internal sealed class WizardAnswerGate
{
    private long _activeLease;
    private long _lastLease;

    public bool TryBegin(out long lease)
    {
        lease = Interlocked.Increment(ref _lastLease);
        if (lease == 0)
            lease = Interlocked.Increment(ref _lastLease);

        if (Interlocked.CompareExchange(ref _activeLease, lease, 0) == 0)
            return true;

        lease = 0;
        return false;
    }

    public bool End(long lease) =>
        lease != 0 && Interlocked.CompareExchange(ref _activeLease, 0, lease) == lease;

    public void Invalidate() =>
        Interlocked.Exchange(ref _activeLease, 0);

    public bool AllowsContinue => Volatile.Read(ref _activeLease) == 0;
}
