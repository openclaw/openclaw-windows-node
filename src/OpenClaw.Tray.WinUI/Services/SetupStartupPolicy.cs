using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class SetupStartupPolicy
{
    public static void ApplyUnpackaged(bool enabled, Func<StartupTaskRegistrationOutcome> registerTask,
        Func<StartupTaskInspection> inspectTask,
        Func<bool> unregisterTask, Action writeRunKey, Action deleteRunKey)
    {
        if (enabled)
        {
            var result = registerTask();
            if (result == StartupTaskRegistrationOutcome.Registered) { deleteRunKey(); return; }
            var actual = inspectTask();
            if (actual == StartupTaskInspection.ExpectedEnabled) { deleteRunKey(); return; }
            if (result == StartupTaskRegistrationOutcome.Rejected && actual == StartupTaskInspection.Absent)
                writeRunKey();
            else
                throw new InvalidOperationException("Windows startup registration could not be confirmed. Retry; no fallback entry was created.");
            return;
        }
        deleteRunKey();
        var state = inspectTask();
        if (state == StartupTaskInspection.Unknown)
            throw new InvalidOperationException("The Windows startup task state is unavailable. Retry the startup preference.");
        if (state != StartupTaskInspection.Absent && !unregisterTask())
            throw new InvalidOperationException("Windows did not remove the startup task. Retry the startup preference.");
    }

    public static async Task ApplyClassicPreferenceAsync(bool? preference, Func<bool, Task> apply, Func<Task> warn)
    {
        if (preference is not { } enabled) return;
        try { await apply(enabled); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            SecurityException or Win32Exception or COMException or NotSupportedException)
        {
            await warn();
        }
    }
}
