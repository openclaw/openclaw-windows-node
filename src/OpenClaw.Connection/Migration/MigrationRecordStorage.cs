using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenClaw.Connection.Migration;

[SupportedOSPlatform("windows")]
internal static class MigrationRecordStorage
{
    /// <summary>
    /// An existing directory is only re-hardened when it actually drifted. Rewriting the DACL needs
    /// WRITE_DAC and WRITE_OWNER, which a roaming or administratively hardened profile can withhold
    /// from an ordinary launch, and a refusal here is not an invalid record: it would surface as a
    /// generic inspection failure and block a migration whose state was already correct.
    /// </summary>
    internal static void CreateProtectedDirectory(string directory)
    {
        MigrationRecordCodec.RejectReparsePoints(directory);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Cannot resolve the current Windows user.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            owner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            info.Create(security);
        else if (MigrationOperationLock.RequiresDirectoryHardening(info, owner))
            info.SetAccessControl(security);
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        MigrationRecordCodec.RejectReparsePoints(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
