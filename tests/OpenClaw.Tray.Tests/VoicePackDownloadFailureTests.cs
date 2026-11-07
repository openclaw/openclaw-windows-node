using System.Net.Http;
using System.Security;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class VoicePackDownloadFailureTests
{
    public static TheoryData<Exception, string> Cases => new()
    {
        { new IOException("disk full", unchecked((int)0x80070070)), nameof(VoicePackDownloadFailureKind.DiskFull) },
        { new IOException("handle disk full", unchecked((int)0x80070027)), nameof(VoicePackDownloadFailureKind.DiskFull) },
        { new HttpRequestException("copy failed", new IOException("disk full", unchecked((int)0x80070070))), nameof(VoicePackDownloadFailureKind.DiskFull) },
        { new HttpRequestException("dns"), nameof(VoicePackDownloadFailureKind.Network) },
        { new IOException("connection reset"), nameof(VoicePackDownloadFailureKind.Network) },
        { new TaskCanceledException("HTTP timeout"), nameof(VoicePackDownloadFailureKind.Network) },
        { new SecurityException("hash mismatch"), nameof(VoicePackDownloadFailureKind.Integrity) },
        { new InvalidOperationException("no pinned hash"), nameof(VoicePackDownloadFailureKind.Unknown) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_MapsExceptionToActionableKind(Exception ex, string expectedKind)
    {
        // The kind enum is internal, so cases carry its member name.
        Assert.Equal(expectedKind, VoicePackDownloadFailure.Classify(ex).ToString());
    }
}
