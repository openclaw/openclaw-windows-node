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
