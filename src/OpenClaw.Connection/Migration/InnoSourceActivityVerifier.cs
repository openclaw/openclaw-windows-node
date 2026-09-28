using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace OpenClaw.Connection.Migration;

public enum InnoSourceActivityStatus
{
    Stopped,
    Running,
    InspectionFailed
}

public interface IInnoSourceActivityVerifier
{
    InnoSourceActivityStatus VerifyStopped();
}

/// <summary>
/// Inspects the exact source image across all sessions, including older instances
/// that do not participate in the runtime file lock. This scan is not itself a lease.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoSourceActivityVerifier : IInnoSourceActivityVerifier
{
    private readonly string _executable;
    private readonly IInnoSourceProcessInspector _processes;

    public InnoSourceActivityVerifier(string executable)
        : this(executable, CreateInspector())
    {
    }

    private static IInnoSourceProcessInspector CreateInspector()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsInnoSourceProcessInspector(identity.User?.Value
            ?? throw new InvalidOperationException("Cannot resolve the current Windows user."));
    }

    internal InnoSourceActivityVerifier(string executable, IInnoSourceProcessInspector processes)
    {
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Source executable must be absolute.", nameof(executable));
        _executable = Path.GetFullPath(executable);
        _processes = processes;
    }

    public InnoSourceActivityStatus VerifyStopped()
    {
        try
        {
            foreach (var process in _processes.FindSameNameProcesses())
            {
                switch (process.State)
                {
                    case InnoSourceProcessState.Exited:
                    case InnoSourceProcessState.OwnedByOtherUser:
                        continue;
                    case InnoSourceProcessState.ImageResolved:
                        if (string.IsNullOrWhiteSpace(process.ImagePath))
                            return InnoSourceActivityStatus.InspectionFailed;
                        if (string.Equals(Path.GetFullPath(process.ImagePath), _executable, StringComparison.OrdinalIgnoreCase))
                            return InnoSourceActivityStatus.Running;
                        continue;
                    default:
                        return InnoSourceActivityStatus.InspectionFailed;
                }
            }
            return InnoSourceActivityStatus.Stopped;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         SecurityException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            return InnoSourceActivityStatus.InspectionFailed;
        }
    }
}
