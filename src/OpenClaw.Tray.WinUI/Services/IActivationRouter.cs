using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Reason a launch, toast, or forwarded activation candidate did not reach a dispatchable route.
/// </summary>
internal enum ActivationRejection
{
    /// <summary>The input failed to parse as a valid deep link for this build's protocol scheme.</summary>
    InvalidUri,

    /// <summary>The route required confirmation and the user denied or canceled it.</summary>
    Unconfirmed,
}

/// <summary>
/// Prompt content for a state-changing activation route. Never carries a Window, Frame, or page;
/// the sink owns showing the actual confirmation UI.
/// </summary>
internal sealed record ActivationConfirmation(string ActionDisplayName, string RedactedInput);

/// <summary>
/// Inputs captured by App at the platform activation boundary (WinUI launch args, packaged
/// protocol retrieval, and post-setup restart flags). ActivationRouter normalizes these into a
/// single activation candidate and, when present, a semantic plan.
/// </summary>
internal sealed record LaunchActivationInput(
    string? ProtocolUri,
    IReadOnlyList<string> CommandLineArguments,
    string? PostSetupLaunch,
    bool SetupShownDuringStartup);

/// <summary>
/// Closed outcome of planning one activation attempt. ActivationRouter never applies a route
/// itself; App's <see cref="IActivationPlanSink"/> implementation is the only place a
/// <see cref="ActivationRoute"/> is turned into a call against an A2 owner or existing service.
/// </summary>
internal abstract record ActivationPlan
{
    /// <summary>No activation candidate was present (e.g. plain launch, or setup was shown).</summary>
    internal sealed record Ignore : ActivationPlan;

    /// <summary>The candidate was rejected before any route was produced.</summary>
    internal sealed record Reject(ActivationRejection Reason, string RedactedInput) : ActivationPlan;

    /// <summary>A non-state-changing route ready to dispatch without confirmation.</summary>
    internal sealed record Dispatch(ActivationRoute Route) : ActivationPlan;

    /// <summary>A state-changing route that must be confirmed before it dispatches.</summary>
    internal sealed record Confirm(ActivationRoute Route, ActivationConfirmation Prompt) : ActivationPlan;

    private ActivationPlan()
    {
    }
}

/// <summary>
/// App-owned sink that applies a planned <see cref="ActivationRoute"/>. Implemented once by App
/// using its one typed route switch; ActivationRouter calls this for both the primary listener
/// loop and any launch/toast plan the caller chooses to dispatch immediately.
/// </summary>
internal interface IActivationPlanSink
{
    /// <summary>Applies a route that already cleared confirmation (or never required it).</summary>
    Task DispatchAsync(ActivationRoute route, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the confirmation prompt for a state-changing route and returns whether the user
    /// allowed it. Denial, cancellation, and a missing UI surface must all return <see
    /// langword="false"/> without dispatching.
    /// </summary>
    Task<bool> ConfirmAsync(ActivationConfirmation confirmation, CancellationToken cancellationToken);
}

/// <summary>
/// Owns launch/protocol/command-line/post-setup normalization, toast argument planning,
/// current-user activation IPC (listener + forwarding), and the confirmation decision for
/// state-changing routes. Reuses <see cref="OpenClaw.Shared.DeepLinkParser"/>,
/// <see cref="DeepLinkSecurityPolicy"/>, <see cref="DeepLinkHandler"/>, and
/// <see cref="ToastActivationRouter"/> as the single route tables; never holds a Window, Frame,
/// concrete page, gateway client, NodeService, or connection manager.
/// </summary>
internal interface IActivationRouter : IAsyncDisposable
{
    /// <summary>Resolves precedence (protocol, then command line, then post-setup chat) and plans it.</summary>
    ActivationPlan PlanLaunch(LaunchActivationInput input);

    /// <summary>Plans a toast activation argument. Toast routes are never state-changing today.</summary>
    ActivationPlan PlanToast(string? argument);

    /// <summary>
    /// Starts the current-user-only named-pipe listener that receives deep links forwarded by a
    /// secondary instance. Every accepted, confirmed route dispatches through <paramref
    /// name="sink"/>. Safe to call once; a second call is a no-op while already started.
    /// </summary>
    Task StartForwardedActivationListenerAsync(IActivationPlanSink sink, CancellationToken cancellationToken);

    /// <summary>
    /// Validates <paramref name="uri"/> and forwards it to the primary instance's listener.
    /// Returns <see langword="false"/> without connecting when the input is oversized or invalid.
    /// </summary>
    Task<bool> ForwardToPrimaryAsync(string uri, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the same launch-precedence candidate as <see cref="PlanLaunch"/> (protocol, then
    /// command line, then post-setup chat) without planning it. Used by the secondary-instance
    /// path, which forwards the raw candidate text rather than dispatching it locally.
    /// </summary>
    string? ResolveLaunchCandidate(LaunchActivationInput input);

    /// <summary>
    /// Applies a plan against <paramref name="sink"/>: dispatches immediately, asks for
    /// confirmation before dispatching, or does nothing for <see cref="ActivationPlan.Ignore"/>
    /// and <see cref="ActivationPlan.Reject"/>. Returns whether a route was dispatched.
    /// </summary>
    Task<bool> DispatchPlanAsync(ActivationPlan plan, IActivationPlanSink sink, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects new dispatches, cancels every admitted direct or forwarded dispatch plus the
    /// listener loop, and drains them. Idempotent; a late route never dispatches after this returns.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}
