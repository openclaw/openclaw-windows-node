using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal readonly record struct DownloadAttempt(bool Installed, bool IndependentCancellation);

/// <summary>
/// Separates a window close or download deadline from an HttpClient timeout.
/// Updatum reports that timeout as <see cref="TaskCanceledException"/> without
/// canceling the caller token.
/// </summary>
internal static class UpdateDownloadCancellation
{
    internal static async Task<DownloadAttempt> FinishAsync(Task<bool> download, CancellationToken callerToken)
    {
        try
        {
            return new DownloadAttempt(await download.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return new DownloadAttempt(false, true);
        }
    }
}
