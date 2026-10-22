using System.ComponentModel;
using System.Runtime.Versioning;
using OpenClaw.Connection.Migration;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class InnoSourceActivityVerifierTests
{
    [Theory]
    [InlineData(InnoSourceProcessState.ImageResolved, @"C:\source\OpenClaw.Tray.WinUI.exe", InnoSourceActivityStatus.Running)]
    [InlineData(InnoSourceProcessState.ImageResolved, @"c:\SOURCE\OpenClaw.Tray.WinUI.exe", InnoSourceActivityStatus.Running)]
    [InlineData(InnoSourceProcessState.ImageResolved, @"C:\other\OpenClaw.Tray.WinUI.exe", InnoSourceActivityStatus.Stopped)]
    [InlineData(InnoSourceProcessState.ImageResolved, null, InnoSourceActivityStatus.InspectionFailed)]
    [InlineData(InnoSourceProcessState.ImageResolved, "", InnoSourceActivityStatus.InspectionFailed)]
    [InlineData(InnoSourceProcessState.Unresolved, null, InnoSourceActivityStatus.InspectionFailed)]
    [InlineData(InnoSourceProcessState.OwnedByOtherUser, null, InnoSourceActivityStatus.Stopped)]
    [InlineData(InnoSourceProcessState.Exited, null, InnoSourceActivityStatus.Stopped)]
    public void Candidates_AreClassifiedWithoutSessionLocalAssumptions(
        object state, string? image, InnoSourceActivityStatus expected)
    {
        var verifier = new InnoSourceActivityVerifier(@"C:\source\OpenClaw.Tray.WinUI.exe",
            new Inspector(() => [new((InnoSourceProcessState)state, image)]));
        Assert.Equal(expected, verifier.VerifyStopped());
    }

    [Fact]
    public void EnumerationFailure_FailsClosed()
    {
        var verifier = new InnoSourceActivityVerifier(@"C:\source\OpenClaw.Tray.WinUI.exe",
            new Inspector(() => throw new Win32Exception(5)));
        Assert.Equal(InnoSourceActivityStatus.InspectionFailed, verifier.VerifyStopped());
    }

    private sealed class Inspector(Func<IEnumerable<InnoSourceProcessCandidate>> inspect) : IInnoSourceProcessInspector
    {
        public IEnumerable<InnoSourceProcessCandidate> FindSameNameProcesses() => inspect();
    }
}
