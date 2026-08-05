using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// A single named shutdown step. <see cref="Execute"/> performs one resource's teardown; the
/// coordinator logs before/after, catches and continues past a failing step, and never lets one
/// step's exception stop the remaining steps or the final exit.
/// </summary>
internal sealed record AppShutdownStep(string Name, Func<ValueTask> Execute);

/// <summary>
/// The immutable shutdown plan. App constructs this from the resources it owns each time exit is
/// requested; only the first caller's plan is ever executed (first-wins), so building it more than
/// once is harmless. <see cref="BeginShutdown"/> runs once, synchronously, before any step (window
/// manager and tray shutdown gating); <see cref="ExitApplication"/> runs once after every step
/// completes.
/// </summary>
internal sealed record AppShutdownPlan(
    Action BeginShutdown,
    IReadOnlyList<AppShutdownStep> Steps,
    Action ExitApplication);

/// <summary>
/// Owns first-wins/shared-task exactly-once shutdown semantics, the synchronous begin-shutdown
/// gate, ordered step execution with per-step log/catch/continue, and the single final
/// <see cref="AppShutdownPlan.ExitApplication"/> call. Holds no service references once a call
/// completes and is never used as a service locator; App remains the sole owner of startup.
/// </summary>
internal interface IAppShutdownCoordinator
{
    /// <summary>True once the first <see cref="ShutdownAsync"/> call has begun.</summary>
    bool IsShuttingDown { get; }

    /// <summary>
    /// Runs <paramref name="plan"/> exactly once. A caller after the first receives the same
    /// in-flight (or already completed) task rather than starting a second run.
    /// </summary>
    Task ShutdownAsync(AppShutdownPlan plan);
}
