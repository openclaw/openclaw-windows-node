// Drops a voice partial that the UI dispatcher runs after its listen has returned, so a late
// partial cannot restore a cleared preview or overwrite a newer recording's preview.
// Each listen owns one gate. Post() re-checks the gate and the capture surface when the queued
// write runs, and Complete() ends the gate before it clears the preview.
// Usage:
//   var preview = new VoiceCapturePreviewGate();
//   preview.Post(run => DispatcherQueue.TryEnqueue(() => run()), IsCurrentCapture, () => host.SetVoiceTranscript(text));
//   preview.Complete(surfaceStillCurrent, () => host.SetVoiceTranscript(null));

namespace OpenClawTray.Chat;

/// <summary>Rejects a partial that the UI queue runs after this listen has returned.</summary>
internal sealed class VoiceCapturePreviewGate
{
    private int _ended;

    public bool TryApply(bool captureIsCurrent) =>
        Volatile.Read(ref _ended) == 0 && captureIsCurrent;

    public void End() => Volatile.Write(ref _ended, 1);

    public void Post(Action<Action> enqueue, Func<bool> isCurrent, Action apply) =>
        enqueue(() =>
        {
            if (TryApply(isCurrent()))
                apply();
        });

    public void Complete(bool surfaceStillCurrent, Action clear)
    {
        End();
        if (surfaceStillCurrent)
            clear();
    }
}
