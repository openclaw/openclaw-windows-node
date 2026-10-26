namespace OpenClawTray.Pages;

/// <summary>
/// Decides what a Bindings callback should do once it reaches the UI thread.
/// A handshake can drop, or the page can unload, between the event and the callback.
/// </summary>
internal static class BindingsConnectionRecovery
{
    internal enum Decision
    {
        Ignore,
        Recover,
        ShowDisconnected,
    }

    internal static Decision DecideQueued(bool observing, bool connected)
    {
        if (!observing)
            return Decision.Ignore;
        return connected ? Decision.Recover : Decision.ShowDisconnected;
    }
}
