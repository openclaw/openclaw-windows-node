namespace OpenClawTray.Chat;

/// <summary>Rejects a partial that the UI queue runs after this listen has returned.</summary>
internal sealed class VoiceCapturePreviewGate
{
    private int _ended;

    public bool TryApply(bool captureIsCurrent) =>
        Volatile.Read(ref _ended) == 0 && captureIsCurrent;

    public void End() => Volatile.Write(ref _ended, 1);
}
