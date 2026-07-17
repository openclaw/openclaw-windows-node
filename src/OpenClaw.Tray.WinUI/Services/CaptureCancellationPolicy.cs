namespace OpenClawTray.Services;

internal static class CaptureCancellationPolicy
{
    public static void ThrowIfCancellationRequested(
        Exception? primaryException,
        CancellationToken cancellationToken)
    {
        if (primaryException == null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
