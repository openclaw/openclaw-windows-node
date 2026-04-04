namespace OpenClawTray.Windows;

internal sealed class WebChatVoiceDomState
{
    public WebChatVoiceDomState()
    {
    }

    public string PendingDraft { get; private set; } = string.Empty;

    public void SetDraft(string? text, bool clear)
    {
        PendingDraft = clear ? string.Empty : (text ?? string.Empty);
    }
}
