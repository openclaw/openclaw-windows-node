using System;
using System.Threading;

public sealed class AttributionContinuationPolicy
{
    private readonly bool explicitlyAuthorized;
    private int limitations;

    public AttributionContinuationPolicy(bool explicitlyAuthorized)
    {
        this.explicitlyAuthorized = explicitlyAuthorized;
    }

    public int Limitations => Volatile.Read(ref limitations);

    public bool TryContinue(string origin, long callbackSequence, int? objectId, int? childId,
        bool validAtEntry, bool visible, int classLength, int classError, uint ownerPid,
        bool unresolvedInvalidAtEnd)
    {
        if (!explicitlyAuthorized || origin != "WinEvent-object-show" || callbackSequence <= 0 ||
            objectId != 0 || childId != 0 || validAtEntry || visible ||
            classLength != 0 || classError != 1400 || ownerPid != 0 || !unresolvedInvalidAtEnd)
            return false;

        // A single unresolved observation may unblock execution, never establish foreign ownership.
        return Interlocked.CompareExchange(ref limitations, 1, 0) == 0;
    }
}
