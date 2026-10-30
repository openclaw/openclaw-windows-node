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

/// <summary>
/// How long one Bindings <c>config.get</c> waits for its own reply.
/// </summary>
internal static class BindingsConfigLoadDeadline
{
    internal const int DefaultMilliseconds = 15_000;

    /// <summary>Test seam. A positive value replaces the default for that process.</summary>
    internal static int? OverrideMilliseconds { get; set; }

    internal static int CurrentMilliseconds =>
        OverrideMilliseconds is int overridden && overridden > 0
            ? overridden
            : DefaultMilliseconds;
}
