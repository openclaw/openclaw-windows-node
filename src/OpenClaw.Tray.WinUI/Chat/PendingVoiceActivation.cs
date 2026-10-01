namespace OpenClawTray.Chat;

/// <summary>Keeps a voice launch until the active native composer is ready, never across page exit.</summary>
internal sealed class PendingVoiceActivation
{
    private bool _requested;

    public void Request(bool nativeSurface) => _requested = nativeSurface;

    public void Cancel() => _requested = false;

    public bool TryConsume(bool pageActive, bool composerReady)
    {
        if (!_requested || !pageActive || !composerReady)
            return false;
        _requested = false;
        return true;
    }
}
