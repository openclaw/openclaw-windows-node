using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests.Chat;

public sealed class VoiceCapturePreviewGateTests
{
    [Fact]
    public void QueuedPartial_AfterEnd_DoesNotRestoreTheClearedPreview()
    {
        var gate = new VoiceCapturePreviewGate();
        string? shown = null;
        var queued = new Queue<Action>();
        queued.Enqueue(() =>
        {
            if (gate.TryApply(captureIsCurrent: true))
                shown = "late partial";
        });

        gate.End();
        shown = null;
        while (queued.TryDequeue(out var apply))
            apply();

        Assert.Null(shown);
    }

    [Fact]
    public void PartialBeforeEnd_StillReachesThePreview()
    {
        var gate = new VoiceCapturePreviewGate();
        string? shown = null;
        if (gate.TryApply(captureIsCurrent: true))
            shown = "hello";
        gate.End();
        Assert.Equal("hello", shown);
        Assert.False(gate.TryApply(captureIsCurrent: true));
    }

    [Fact]
    public void LeftSurface_DropsThePartial()
    {
        var gate = new VoiceCapturePreviewGate();
        Assert.False(gate.TryApply(captureIsCurrent: false));
    }
}
