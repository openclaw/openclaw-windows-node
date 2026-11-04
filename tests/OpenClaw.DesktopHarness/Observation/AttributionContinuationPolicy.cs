using System;
using System.Threading;

public sealed class AttributionContinuationPolicy
{
    private readonly bool explicitlyAuthorized;
    private readonly bool countEveryEligibleShow;
    private int limitations;

    public AttributionContinuationPolicy(bool explicitlyAuthorized)
        : this(explicitlyAuthorized, false)
    {
    }

    public AttributionContinuationPolicy(bool explicitlyAuthorized, bool countEveryEligibleShow)
    {
        this.explicitlyAuthorized = explicitlyAuthorized;
        this.countEveryEligibleShow = countEveryEligibleShow;
    }

    public int Limitations => Volatile.Read(ref limitations);

    public bool TryContinue(string origin, long callbackSequence, int? objectId, int? childId,
        bool validAtEntry, bool visible, int classLength, int classError, uint ownerPid,
        bool unresolvedInvalidAtEnd, bool knownIdentityOrReuse = false)
    {
        if (!explicitlyAuthorized || origin != "WinEvent-object-show" || callbackSequence <= 0 ||
            objectId != 0 || childId != 0 || validAtEntry || visible ||
            classLength != 0 || classError != 1400 || ownerPid != 0 || !unresolvedInvalidAtEnd ||
            knownIdentityOrReuse)
            return false;

        // Both policies preserve raw attribution failure, never establish foreign ownership.
        if (countEveryEligibleShow)
        {
            Interlocked.Increment(ref limitations);
            return true;
        }
        return Interlocked.CompareExchange(ref limitations, 1, 0) == 0;
    }
}
