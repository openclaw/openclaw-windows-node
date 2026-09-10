using OpenClaw.Shared;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// The auto-start state Windows reports, including the case where it could not be read.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> exists so a transient query failure is never mistaken for
/// "Windows says disabled". Collapsing the two lets the app persist <c>AutoStart=false</c>
/// over an enabled preference, which the user cannot recover from without noticing and
/// re-toggling by hand.
/// </remarks>
internal enum AutoStartState
{
    Enabled,
    Disabled,
    Unknown
}

/// <summary>
/// Thrown when Windows explicitly refuses to enable the packaged startup task.
/// </summary>
/// <remarks>
/// Distinct from a transient failure: a refusal (DisabledByUser / DisabledByPolicy) is a
/// durable answer that must be surfaced as disabled rather than retried at every launch.
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch
/// that type keep working.
/// </remarks>
internal sealed class AutoStartRefusedException : InvalidOperationException
{
    public AutoStartRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Decides which auto-start value the app should report and persist, given what Windows
/// says and what the user asked for.
/// </summary>
/// <remarks>
/// Both entry points obey one rule: <b>only a definite answer from Windows may overwrite
/// the user's intent.</b> "Disabled" and an explicit refusal are definite. A failed or
/// unreadable query is not, and must leave the stored preference alone, because every
/// caller persists what it is given and the next launch treats that as intent.
///
/// Kept free of WinRT so the policy can be unit tested directly. <see cref="AutoStartManager"/>
/// owns the real StartupTask query and setter and supplies them here.
/// </remarks>
internal static class AutoStartReconciliation
{
    /// <summary>
    /// Reconciles the persisted preference against the real Windows startup state and
    /// returns the value the app should now report and store.
    /// </summary>
    /// <param name="configured">The preference currently stored in settings.</param>
    /// <param name="queryAsync">Reads what Windows reports now.</param>
    /// <param name="setEnabledAsync">Asks Windows to enable auto-start.</param>
    /// <remarks>
    /// Windows is the source of truth: the stored intent is applied when it can be, and
    /// whatever Windows reports afterwards is what gets persisted. Enabling is a request
    /// Windows may refuse, and a refusal must not be retried silently at every launch, so
    /// it is surfaced as false.
    ///
    /// A failure to read or apply the state is not a refusal, so it returns
    /// <paramref name="configured"/> unchanged and the caller persists nothing.
    /// </remarks>
    internal static async Task<bool> ReconcileAsync(
        bool configured,
        Func<Task<AutoStartState>> queryAsync,
        Func<bool, Task> setEnabledAsync)
    {
        var actual = await QueryOrUnknownAsync(queryAsync);
        if (actual == AutoStartState.Unknown)
        {
            Logger.Warn($"Auto-start state is unknown, keeping the configured value ({configured}).");
            return configured;
        }

        var enabled = actual == AutoStartState.Enabled;
        if (enabled == configured)
            return configured;

        if (enabled)
        {
            // Windows says enabled while the app setting says off, which happens when the
            // user enables the entry in Startup Apps. Report the truth instead of fighting
            // Windows; the in-app toggle still pushes changes the other way.
            Logger.Info("Auto-start is enabled in Windows; adopting that state.");
            return true;
        }

        // Configured on, Windows off: apply the stored intent.
        try
        {
            await setEnabledAsync(true);
            return true;
        }
        catch (AutoStartRefusedException ex)
        {
            Logger.Warn($"Windows refused to enable auto-start, reporting disabled: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Auto-start could not be enabled, keeping the configured value ({configured}): {ex.Message}");
            return configured;
        }
    }

    /// <summary>
    /// Decides which auto-start value to persist after an attempt to change it threw.
    /// </summary>
    /// <param name="requested">The value the user asked for, already written to settings.</param>
    /// <param name="failure">The exception the change attempt threw.</param>
    /// <param name="queryAsync">Reads what Windows reports now.</param>
    /// <remarks>
    /// The caller writes <paramref name="requested"/> optimistically and calls this to
    /// decide whether to roll that write back.
    ///
    /// A refusal is definite, so the toggle is corrected to false rather than left
    /// claiming an auto-start that will never happen. Any other failure means the change
    /// may or may not have landed, so Windows is asked; if that cannot be determined
    /// either, <paramref name="requested"/> stands.
    /// </remarks>
    internal static async Task<bool> ResolveAfterFailedChangeAsync(
        bool requested,
        Exception failure,
        Func<Task<AutoStartState>> queryAsync)
    {
        if (failure is AutoStartRefusedException)
        {
            Logger.Warn($"Windows refused the auto-start change, reporting disabled: {failure.Message}");
            return false;
        }

        var actual = await QueryOrUnknownAsync(queryAsync);
        if (actual == AutoStartState.Unknown)
        {
            Logger.Warn($"Auto-start state is unknown after a failed change, keeping the requested value ({requested}).");
            return requested;
        }

        return actual == AutoStartState.Enabled;
    }

    /// <summary>
    /// Runs the query, turning a thrown exception into <see cref="AutoStartState.Unknown"/>
    /// so callers handle "could not be read" in exactly one place.
    /// </summary>
    private static async Task<AutoStartState> QueryOrUnknownAsync(Func<Task<AutoStartState>> queryAsync)
    {
        try
        {
            return await queryAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read the auto-start state: {ex.Message}");
            return AutoStartState.Unknown;
        }
    }
}
