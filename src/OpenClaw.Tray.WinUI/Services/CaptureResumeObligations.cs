using System;
using System.Threading;

namespace OpenClawTray.Services;

/// <summary>
/// OWNED SUSPENSION bookkeeping (not physical handle/lifetime ownership). A reservation is taken BEFORE
/// SuspendThread and becomes OWNED only on a real success. EXCLUSIVITY covers reservations AND owned slots. Token
/// states are TERMINAL and mutated only under one lock: a released token can never be promoted, and an invalid
/// promotion never mutates counters. The owned obligation is released ONLY after a confirmed successful resume.
/// <para>
/// HONEST SCOPE: a process-local bounded-wait signal. It does NOT make the process impossible to exit; the host
/// waits a bounded grace and REPORTS an unresolved owned suspension as not confirmed. A native syscall cannot be
/// hard-cancelled; the target is never killed and no owned suspension is force-cleared.
/// </para>
/// </summary>
internal static class CaptureResumeObligations
{
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Clear = new(true);
    private static int _owned;
    private static int _reservations;

    public static int Outstanding { get { lock (Gate) return _owned; } }
    public static int Reservations { get { lock (Gate) return _reservations; } }

    /// <summary>Reserve the SINGLE slot; null when a reservation or an owned suspension already exists.</summary>
    public static OwnedSuspensionReservation? TryReserve()
    {
        lock (Gate)
        {
            if (_owned > 0 || _reservations > 0) return null;   // exclusivity includes RESERVATIONS
            _reservations++;
            return new OwnedSuspensionReservation { Live = true };
        }
    }

    /// <summary>Promote a LIVE reservation to OWNED - only after SuspendThread succeeded. No-op on a terminal token.</summary>
    public static void PromoteToOwned(OwnedSuspensionReservation reservation)
    {
        lock (Gate)
        {
            if (!reservation.Live || reservation.Owned || reservation.Released) return;   // never mutate counters
            reservation.Live = false;
            if (_reservations > 0) _reservations--;
            reservation.Owned = true;
            if (++_owned == 1) Clear.Reset();
        }
    }

    /// <summary>SuspendThread failed (DWORD_MAX): no owned suspension exists - terminal release, no leak.</summary>
    public static void ReleaseReservationOnly(OwnedSuspensionReservation reservation)
    {
        lock (Gate)
        {
            if (reservation.Owned || reservation.Released) return;
            reservation.Released = true;
            if (reservation.Live)
            {
                reservation.Live = false;
                if (_reservations > 0) _reservations--;
            }
        }
    }

    /// <summary>Release ONLY after a confirmed successful resume; a resume failure RETAINS it (never blindly decremented).</summary>
    public static void ReleaseOwnedAfterConfirmedResume(OwnedSuspensionReservation reservation)
    {
        lock (Gate)
        {
            if (!reservation.Owned || reservation.Released) return;
            reservation.Released = true;
            if (_owned > 0 && --_owned == 0) Clear.Set();
        }
    }

    public static bool WaitForClear(TimeSpan grace)
    {
        try { return Clear.Wait(grace); }
        catch (ObjectDisposedException) { return true; }
    }
}

/// <summary>One owned-suspension slot. States are terminal: Live -> (Owned|Released); Owned -> Released.</summary>
internal sealed class OwnedSuspensionReservation
{
    internal bool Live;
    internal bool Owned;
    internal bool Released;
}