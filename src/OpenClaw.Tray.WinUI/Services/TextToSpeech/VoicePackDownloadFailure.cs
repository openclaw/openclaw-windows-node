using System;
using System.IO;
using System.Net.Http;
using System.Security;

namespace OpenClawTray.Services;

/// <summary>User-actionable category of a failed voice-pack download. Raw exception text stays in the log.</summary>
internal enum VoicePackDownloadFailureKind
{
    Unknown,
    DiskFull,
    Network,
    Integrity,
}

internal static class VoicePackDownloadFailure
{
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;

    /// <summary>
    /// Classifies a download exception. User cancellation must be handled before calling this:
    /// an <see cref="OperationCanceledException"/> reaching here is an HTTP timeout.
    /// </summary>
    public static VoicePackDownloadFailureKind Classify(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is IOException && (e.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull)
                return VoicePackDownloadFailureKind.DiskFull;
        }
        return ex switch
        {
            SecurityException => VoicePackDownloadFailureKind.Integrity,
            HttpRequestException or OperationCanceledException or IOException => VoicePackDownloadFailureKind.Network,
            _ => VoicePackDownloadFailureKind.Unknown,
        };
    }
}
