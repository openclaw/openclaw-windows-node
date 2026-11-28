using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests.Chat;

public sealed class VoiceCapturePreviewGateTests
{
    [Fact]
    public void QueuedPartial_AfterComplete_DoesNotRestoreTheClearedPreview()
    {
        var queued = new Queue<Action>();
        string? shown = null;
        var gate = new VoiceCapturePreviewGate();
        gate.Post(queued.Enqueue, () => true, () => shown = "partial");
        Assert.Single(queued);
        Assert.Null(shown);
        queued.Dequeue()();
        Assert.Equal("partial", shown);

        gate.Post(queued.Enqueue, () => true, () => shown = "late partial");
        gate.Complete(surfaceStillCurrent: true, () => shown = null);
        Assert.Null(shown);
        queued.Dequeue()();

        Assert.Null(shown);
    }

    [Fact]
    public void QueuedPartial_IsDroppedWhenTheSurfaceChangesBeforeItRuns()
    {
        var queued = new Queue<Action>();
        var current = true;
        var applied = false;
        new VoiceCapturePreviewGate().Post(queued.Enqueue, () => current, () => applied = true);
        Assert.Single(queued);

        current = false;
        queued.Dequeue()();

        Assert.False(applied);
    }

    [Fact]
    public void Complete_AfterTheSurfaceMovedOn_EndsTheGateWithoutClearing()
    {
        var gate = new VoiceCapturePreviewGate();
        var cleared = false;

        gate.Complete(surfaceStillCurrent: false, () => cleared = true);

        Assert.False(cleared);
        Assert.False(gate.TryApply(captureIsCurrent: true));
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
        var dispatcher = new Queue<Action>();
        string? shown = null;
        float level = -1f;
        var first = new VoiceCapturePreviewGate();
        first.Post(dispatcher.Enqueue, () => true, () => shown = "old partial");
        first.Post(dispatcher.Enqueue, () => true, () => level = 0.8f);
        first.Complete(surfaceStillCurrent: true, () =>
        {
            shown = null;
            level = 0f;
        });

        var second = new VoiceCapturePreviewGate();
        second.Post(dispatcher.Enqueue, () => true, () => shown = "fresh partial");
        second.Post(dispatcher.Enqueue, () => true, () => level = 0.2f);
        // The old listen's producer can still report after the fresh recording started.
        first.Post(dispatcher.Enqueue, () => true, () => shown = "late old partial");
        first.Post(dispatcher.Enqueue, () => true, () => level = 0.9f);

        while (dispatcher.TryDequeue(out var delayed))
            delayed();

        Assert.Equal("fresh partial", shown);
        Assert.Equal(0.2f, level);
    }
}
