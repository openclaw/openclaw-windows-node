using OpenClaw.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal static class AutoStartSettingsApplier
{
    internal static async Task ApplyLatestAsync(
        SemaphoreSlim mutationGate,
        Func<bool> readPreference,
        Func<bool, Task> setEnabledAsync)
    {
        // Every preference save schedules this background refresh. Fixture-local
        // preferences may be saved without touching Windows; explicit auto-start
        // toggles still go through AutoStartManager and visibly refuse mutation.
        // Validate before waiting or reading so malformed fixture contexts fail closed.
        if (GatewayFixtureIsolation.IsEnabled)
            return;

        await mutationGate.WaitAsync();
        try
        {
            // A queued settings-save effect must read after any toggle or reconciliation finishes.
            await setEnabledAsync(readPreference());
        }
        finally
        {
            mutationGate.Release();
        }
    }
}
