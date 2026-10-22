using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests.Chat;

public sealed class PendingVoiceActivationTests
{
    [Fact]
    public void DelayedComposer_ConsumesExactlyOnceWhenReady()
    {
        var activation = new PendingVoiceActivation();
        activation.Request(nativeSurface: true);
        for (var attempt = 0; attempt < 100; attempt++)
            Assert.False(activation.TryConsume(pageActive: true, composerReady: false));
        Assert.False(activation.TryConsume(pageActive: false, composerReady: true));
        Assert.True(activation.TryConsume(pageActive: true, composerReady: true));
        Assert.False(activation.TryConsume(pageActive: true, composerReady: true));
    }

    [Fact]
    public void PageExit_CancelsDeferredRecording()
    {
        var activation = new PendingVoiceActivation();
        activation.Request(nativeSurface: true);
        activation.Cancel();
        Assert.False(activation.TryConsume(pageActive: true, composerReady: true));
    }

    [Fact]
    public void LegacySurface_DoesNotQueueSurpriseRecordingForLaterNativeMount()
    {
        var activation = new PendingVoiceActivation();
        activation.Request(nativeSurface: false);
        Assert.False(activation.TryConsume(pageActive: true, composerReady: true));
    }
}
