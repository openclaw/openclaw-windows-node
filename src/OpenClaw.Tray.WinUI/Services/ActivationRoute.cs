namespace OpenClawTray.Services;

/// <summary>
/// Closed semantic union of every activation destination reachable from a deep link, a toast
/// action, or a forwarded single-instance activation. <see cref="DeepLinkHandler.PlanRoute"/> and
/// <see cref="ToastActivationRouter.PlanRoute"/> are the only two route tables that produce these
/// values; App owns one typed switch (its <c>IActivationPlanSink.DispatchAsync</c> implementation)
/// that applies a route to the existing A2 owners and services. No other production code should
/// build or interpret an <see cref="ActivationRoute"/> case list.
/// </summary>
internal abstract record ActivationRoute
{
    internal sealed record OpenHub(string? Page) : ActivationRoute;
    internal sealed record OpenSetup : ActivationRoute;
    internal sealed record OpenDashboard(string? Path) : ActivationRoute;
    internal sealed record OpenChat(string? SessionKey) : ActivationRoute;
    internal sealed record OpenUrl(string Uri) : ActivationRoute;
    internal sealed record OpenTrayMenu : ActivationRoute;
    internal sealed record OpenLogFile : ActivationRoute;
    internal sealed record OpenLogFolder : ActivationRoute;
    internal sealed record OpenConfigFolder : ActivationRoute;
    internal sealed record OpenDiagnosticsFolder : ActivationRoute;
    internal sealed record CopyDiagnostics(DiagnosticsCopyKind Kind) : ActivationRoute;
    internal sealed record CopyPairingCommand(string Command) : ActivationRoute;
    internal sealed record ReviewPairing : ActivationRoute;
    internal sealed record RestartSshTunnel : ActivationRoute;
    internal sealed record RunHealthCheck : ActivationRoute;
    internal sealed record CheckForUpdates : ActivationRoute;
    internal sealed record OpenVoice : ActivationRoute;
    internal sealed record StopVoice : ActivationRoute;
    internal sealed record SendMessage(string Message) : ActivationRoute;

    private ActivationRoute()
    {
    }
}

/// <summary>Grouped clipboard-diagnostics destinations reachable via deep link only.</summary>
internal enum DiagnosticsCopyKind
{
    SupportContext,
    DebugBundle,
    BrowserSetupGuidance,
    PortDiagnostics,
    CapabilityDiagnostics,
    NodeInventory,
    ChannelSummary,
    ActivitySummary,
    ExtensibilitySummary,
}
