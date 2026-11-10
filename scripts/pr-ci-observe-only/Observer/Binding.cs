// Single framework-host/source admission for this one-fact ObserveOnly probe. No capture mode exists.
namespace OpenClaw.ObserveOnly;
public sealed class Binding
{
    public const bool CaptureEnabled = false;
    public int Hosts { get; private set; }
    public int Cases { get; private set; }
    public int Ends { get; private set; }
    public bool SourceMatched { get; private set; }
    public bool Passed { get; private set; }
    public bool SessionEnded { get; private set; }
    public string Failure { get; private set; } = "none";
    public void Reject(string reason) { if (Failure == "none") Failure = reason; }
    public void Host(int pid, bool identityValid, bool alive)
    {
        if (++Hosts != 1) Reject("ambiguous-host");
        if (pid <= 0 || !identityValid || !alive) Reject("host-identity");
        if (SessionEnded || Cases != 0) Reject("event-order");
    }
    public void Case(bool canonicalPathMatched, bool digestMatched)
    {
        if (++Cases != 1) Reject("ambiguous-source");
        if (Hosts != 1 || SessionEnded) Reject("event-order");
        SourceMatched = canonicalPathMatched && digestMatched;
        if (!SourceMatched) Reject("source-mismatch");
    }
    public void End(bool passed)
    {
        if (++Ends != 1 || Cases != 1 || SessionEnded) Reject("event-order");
        Passed = passed;
        if (!passed) Reject("probe-failed");
    }
    public void Close()
    {
        if (SessionEnded) Reject("event-order");
        SessionEnded = true;
    }
    public bool Bound => Failure == "none" && Hosts == 1 && Cases == 1 && Ends == 1 && SourceMatched && Passed && SessionEnded;
}
