using System.ComponentModel;
using System.Diagnostics;

namespace OpenClawTray.Services;

/// <summary>
/// Opens a dashboard URL with the shell and returns a browser process id when Windows provides one.
/// Launch failures are not logged: the exception text can contain the credential URL.
/// </summary>
internal static class DashboardBrowserShell
{
    internal static bool TryOpen(string url, out int? processId)
    {
        processId = null;
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return InterpretStartedProcess(process, out processId);
        }
        catch (Exception)
        {
            processId = null;
            return false;
        }
        finally
        {
            try
            {
                process?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Dashboard browser handle release failed: {ex.GetType().Name}");
            }
        }
    }

    internal static bool InterpretStartedProcess(Process? process, out int? processId)
    {
        processId = null;
        if (process is null)
            return true;

        processId = TryReadProcessId(process);
        return true;
    }

    private static int? TryReadProcessId(Process process)
    {
        try
        {
            if (process.HasExited)
                return null;
            return process.Id > 0 ? process.Id : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
