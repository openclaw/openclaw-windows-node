namespace OpenClaw.Shared;

/// <summary>
/// One in-flight wizard answer. Continue and More both send wizard.next,
/// so a second click must not start another request.
/// </summary>
public static class WizardAnswerGate
{
    public static bool TryBegin(ref int inFlight) =>
        Interlocked.CompareExchange(ref inFlight, 1, 0) == 0;

    public static void End(ref int inFlight) =>
        Interlocked.Exchange(ref inFlight, 0);

    public static bool AllowsContinue(int inFlight) => inFlight == 0;
}
