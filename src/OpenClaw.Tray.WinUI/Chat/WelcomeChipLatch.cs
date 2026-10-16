namespace OpenClawTray.Chat;

/// <summary>
/// Welcome chips stay disabled only while their send is in flight.
/// A failed send clears the latch. A user row on the thread that was
/// sent also clears it, even when that thread is not the compose target.
/// </summary>
public static class WelcomeChipLatch
{
    public static bool BlocksAnotherChip(bool inFlight) => inFlight;

    public static bool ClearsBecauseSendReturned(bool inFlight) => inFlight;

    public static bool ClearsBecauseSentThreadHasUserRow(
        string? sentThreadId,
        string timelineThreadId,
        bool hasUserRow)
        => !string.IsNullOrEmpty(sentThreadId)
           && string.Equals(sentThreadId, timelineThreadId, StringComparison.Ordinal)
           && hasUserRow;
}
