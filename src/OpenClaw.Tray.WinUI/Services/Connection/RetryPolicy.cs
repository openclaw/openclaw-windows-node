namespace OpenClawTray.Services.Connection;

/// <summary>
/// Retry/backoff policy per error category.
/// </summary>
public static class RetryPolicy
{
    public static readonly int[] StandardBackoffMs = [1000, 2000, 4000, 8000, 15000, 30000, 60000];
    public static readonly int[] RateLimitBackoffMs = [30000, 60000, 120000, 300000];
    public static readonly int[] ServerCloseBackoffMs = [1000, 2000, 4000];

    public static bool ShouldRetry(ConnectionErrorCategory category) => category switch
    {
        ConnectionErrorCategory.AuthFailure => false,
        ConnectionErrorCategory.PairingPending => false,
        ConnectionErrorCategory.PairingRejected => false,
        ConnectionErrorCategory.ProtocolMismatch => false,
        ConnectionErrorCategory.Cancelled => false,
        ConnectionErrorCategory.Disposed => false,
        _ => true
    };

    public static int GetBackoffMs(ConnectionErrorCategory category, int attempt)
    {
        var backoffs = category switch
        {
            ConnectionErrorCategory.RateLimited => RateLimitBackoffMs,
            ConnectionErrorCategory.ServerClose => ServerCloseBackoffMs,
            _ => StandardBackoffMs
        };
        var idx = Math.Min(attempt, backoffs.Length - 1);
        return backoffs[idx];
    }
}
