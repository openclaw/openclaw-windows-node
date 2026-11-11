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

    [Fact]
    public void DelayedPartial_AfterFreshRecording_CannotRestoreTheOldPreview()
    {
        var queued = new Queue<Action>();
        string? shown = null;
        float level = -1f;
        var first = new VoiceCapturePreviewGate();
        first.Post(queued.Enqueue, () => true, () => shown = "old partial");
        first.Post(queued.Enqueue, () => true, () => level = 0.8f);
        first.Complete(surfaceStillCurrent: true, () =>
        {
            shown = null;
            level = 0f;
        });
        Assert.Null(shown);
        Assert.Equal(0f, level);

        var second = new VoiceCapturePreviewGate();
        second.Post(apply => apply(), () => true, () => shown = "fresh partial");
        second.Post(apply => apply(), () => true, () => level = 0.2f);
        Assert.Equal("fresh partial", shown);
        Assert.Equal(0.2f, level);

        while (queued.TryDequeue(out var delayed))
            delayed();

        Assert.Equal("fresh partial", shown);
        Assert.Equal(0.2f, level);
    }
}
