namespace OpenClaw.SetupEngine;

/// <summary>Presentation admission only. Receipt consumption is not itself current runtime authority.</summary>
public sealed class SetupReadyPresentation
{
    private long _generation;
    private bool _mounted;
    private bool _failed;
    public bool ReceiptConsumed { get; private set; }
    public bool IsReady => ReceiptConsumed && _mounted && !_failed;

    public long BeginMount()
    {
        _mounted = false;
        _failed = false;
        return ++_generation;
    }

    public bool CompleteMount(long generation)
    {
        if (generation != _generation || _failed) return false;
        _mounted = true;
        return true;
    }

    public void ConsumeReceipt() => ReceiptConsumed = true;

    public void Fail()
    {
        _failed = true;
        _mounted = false;
        ++_generation;
    }
}
