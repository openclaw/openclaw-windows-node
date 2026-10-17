using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Resolves an assistant file's ticketed download URL and hands it straight to the
/// browser launcher. The URL never leaves this call: callers receive only an
/// <see cref="AssistantFileDownloadStatus"/>, which keeps the Gateway ticket inside the
/// connection-owned chat bridge (see the assistant-media-resolver row in
/// docs/ARCHITECTURE.md).
/// </summary>
internal static class AssistantFileDownloadLauncher
{
    public static async Task<AssistantFileDownloadStatus> DownloadAsync(
        Func<CancellationToken, Task<Uri?>> resolveDownloadUri,
        Func<Uri, Task<bool>> launchUri,
        CancellationToken cancellationToken)
    {
        Uri? uri;
        try
        {
            uri = await resolveDownloadUri(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"Assistant file download resolution failed ({ex.GetType().Name}).");
            return AssistantFileDownloadStatus.Unavailable;
        }

        if (uri is null)
            return AssistantFileDownloadStatus.Unavailable;

        try
        {
            return await launchUri(uri).ConfigureAwait(false)
                ? AssistantFileDownloadStatus.Started
                : AssistantFileDownloadStatus.Failed;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Assistant file download launch failed ({ex.GetType().Name}).");
            return AssistantFileDownloadStatus.Failed;
        }
    }
}
