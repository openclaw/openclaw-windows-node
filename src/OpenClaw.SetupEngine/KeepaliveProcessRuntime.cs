using System.Diagnostics;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Narrow, setup-engine-internal seam over the raw OS primitives <see cref="KeepaliveProcessManager"/>
/// needs: PID liveness, command-line lookup, WSL process enumeration, starting the detached
/// keepalive process, and killing a process tree. This is a runtime/mechanism seam only — it
/// carries no identity policy. Whether a command line "belongs" to a given distro's keepalive is
/// decided exclusively by <see cref="OpenClaw.Shared.WslCommandLineMatcher"/> via
/// <see cref="KeepaliveProcessManager.IsKeepaliveCommandLine"/>, never by this interface or its
/// implementations. Deliberately setup-engine-scoped, not a generic cross-repo process
/// abstraction — do not reuse this outside <see cref="KeepaliveProcessManager"/>.
/// </summary>
internal interface IKeepaliveProcessRuntime
{
    /// <summary>True if a process with this PID currently exists and has not exited.</summary>
    bool IsProcessAlive(int pid);

    /// <summary>
    /// Best-effort command-line lookup for a PID. Returns null if the process is gone or the
    /// lookup otherwise fails to produce a value. May throw for exceptional OS failures — callers
    /// in <see cref="KeepaliveProcessManager"/> catch per-call so one failure doesn't abort a
    /// broader scan.
    /// </summary>
    string? GetCommandLine(int pid);

    /// <summary>Enumerates the OS process IDs with the exact process name requested by the manager.</summary>
    IReadOnlyList<int> EnumerateProcessIds(string processName);

    /// <summary>
    /// Starts the exact executable/argv selected by the manager and returns its PID, or null if the
    /// OS returned no process handle. May throw; <see cref="KeepaliveProcessManager"/> treats a
    /// thrown exception identically to a null return.
    /// </summary>
    int? StartDetached(KeepaliveProcessStartSpec startSpec);

    /// <summary>
    /// Kills the process tree rooted at <paramref name="pid"/> and waits up to
    /// <paramref name="waitTimeout"/> for exit. May throw (e.g. the process already exited, or
    /// access is denied) — callers catch per-process so one failure doesn't stop cleanup of the
    /// remaining matches.
    /// </summary>
    void KillProcessTree(int pid, TimeSpan waitTimeout);
}

internal sealed record KeepaliveProcessStartSpec(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Production <see cref="IKeepaliveProcessRuntime"/> backed by <see cref="System.Diagnostics.Process"/>
/// and a WMI/CIM command-line lookup via a spawned <c>powershell.exe</c> helper (unchanged from the
/// pre-extraction inline implementation). Every <see cref="Process"/> wrapper obtained here is
/// disposed before the method returns.
/// </summary>
internal sealed class ProcessKeepaliveRuntime : IKeepaliveProcessRuntime
{
    public bool IsProcessAlive(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }

    public string? GetCommandLine(int pid)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output.Trim();
    }

    public IReadOnlyList<int> EnumerateProcessIds(string processName)
    {
        var ids = new List<int>();
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var proc in processes)
                ids.Add(proc.Id);
        }
        finally
        {
            // Dispose every wrapper returned by GetProcessesByName, even if reading .Id on one of
            // them throws partway through — otherwise the unvisited remainder of the array leaks
            // process handles.
            foreach (var proc in processes)
                proc.Dispose();
        }
        return ids;
    }

    public int? StartDetached(KeepaliveProcessStartSpec startSpec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = startSpec.FileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in startSpec.Arguments)
            psi.ArgumentList.Add(argument);

        using var proc = Process.Start(psi);
        return proc?.Id;
    }

    public void KillProcessTree(int pid, TimeSpan waitTimeout)
    {
        using var proc = Process.GetProcessById(pid);
        proc.Kill(entireProcessTree: true);
        proc.WaitForExit((int)waitTimeout.TotalMilliseconds);
    }
}
