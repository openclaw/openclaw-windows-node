using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Coordinates same-user Inno runtime, uninstall, and Store writes across Windows sessions.
/// Runtime readers retain their handle until shutdown; Store writers require exclusive access.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MigrationOperationLock
{
    public static FileStream AcquireRuntime(MigrationBinding binding) => Open(binding, FileAccess.Read, FileShare.Read);

    internal static FileStream AcquireExclusive(MigrationBinding binding) => Open(binding, FileAccess.ReadWrite, FileShare.None);

    public static bool IsBusy(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    private static FileStream Open(MigrationBinding binding, FileAccess access, FileShare share)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Cannot resolve the current Windows user.");
        if (binding.UserSid != owner.Value)
            throw new InvalidOperationException("Migration locking must use the current Windows user.");

        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        MigrationRecordCodec.RejectReparsePoints(directory);
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
        else
        {
            if (RequiresHardening(info, owner))
                info.SetAccessControl(security);
            ScrubExistingEntries(info, owner);
        }

        var path = Path.Combine(directory, "prepare.lock");
        MigrationRecordCodec.RejectReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, access, share);
    }

    /// <summary>
    /// Rewriting the DACL on every acquisition needs WRITE_DAC and WRITE_OWNER, which an ordinary
    /// launch can lack on a roaming or administratively hardened profile. Reading is cheap and
    /// always permitted to the owner, so only correct a directory that actually drifted.
    /// </summary>
    private static bool RequiresHardening(DirectoryInfo info, SecurityIdentifier owner)
    {
        try
        {
            var current = info.GetAccessControl();
            if (!current.AreAccessRulesProtected
                || current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actual
                || actual != owner)
                return true;

            return GrantsAnyForeignAccess(current, owner);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// A protected DACL with the expected owner can still carry an Allow rule for an unrelated
    /// principal. That principal could delete completed.dpapi, which the uninstall checker reads
    /// as an absent receipt and therefore as permission to destroy the local gateway. Treat any
    /// grant outside the three principals this type writes as drift worth correcting.
    /// </summary>
    private static bool GrantsAnyForeignAccess(FileSystemSecurity security, SecurityIdentifier owner)
    {
        var trusted = TrustedPrincipals(owner);
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Array.IndexOf(trusted, sid) >= 0)
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// An owner keeps implicit WRITE_DAC no matter what the DACL says, so trusted-looking rules on
    /// an entry owned by another principal are not durable: that owner can rewrite the ACL and
    /// delete completed.dpapi, which the uninstall checker reads as an absent receipt and therefore
    /// as permission to destroy the local gateway. Ownership is part of the guarantee here for the
    /// same reason it is for the directory.
    /// </summary>
    internal static bool RequiresEntryHardening(FileSecurity current, SecurityIdentifier owner)
        => !current.AreAccessRulesProtected
            || current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actual
            || actual != owner
            || GrantsAnyForeignAccess(current, owner);

    /// <summary>
    /// Hardening the directory rewrites inherited access only, so an explicit Allow rule placed
    /// directly on completed.dpapi or prepare.lock survives it. Deleting the receipt is the exact
    /// escalation this hardening exists to prevent, so the files carry the same guarantee.
    /// </summary>
    private static void ScrubExistingEntries(DirectoryInfo directory, SecurityIdentifier owner)
    {
        foreach (var file in directory.GetFiles())
        {
            FileSecurity current;
            try
            {
                current = file.GetAccessControl();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                          or IOException)
            {
                current = null!;
            }

            if (current is not null && !RequiresEntryHardening(current, owner))
                continue;

            var hardened = new FileSecurity();
            hardened.SetOwner(owner);
            hardened.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in TrustedPrincipals(owner))
            {
                hardened.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            // A foreign owner is exactly the case where the rewrite can be refused. Startup must not
            // fail for state it cannot correct, so leave the entry and let the uninstall decision
            // treat an unverifiable receipt as uncertain rather than as consent to remove.
            try
            {
                file.SetAccessControl(hardened);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                          or IOException)
            {
            }
        }
    }

    /// <summary>
    /// Trust asks a narrower question than repair. An entry that merely inherits the directory's
    /// protected, trusted-only rules is sound even though it is not independently protected, which
    /// is the ordinary state of prepare.lock right after creation. What must hold is authority: a
    /// trusted owner, because an owner keeps implicit WRITE_DAC, and no grant to anyone else that
    /// could change or delete the receipt. This mirrors Test-AclAuthority in
    /// scripts/Test-InnoMigration.ps1, which owns the uninstall decision; keep the two in step.
    /// </summary>
    internal static bool EntryAuthorityIsSound(FileSystemSecurity current, SecurityIdentifier owner)
        => current.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier actual
            && Array.IndexOf(TrustedPrincipals(owner), actual) >= 0
            && !GrantsForeignMutatingAccess(current, owner);

    /// <summary>
    /// Only a principal that can change or delete the receipt affects the uninstall decision.
    /// Read, list, and traverse grants inherit harmlessly into this directory on managed profiles,
    /// and treating those as tampering would make every ordinary uninstall unverifiable. The mask
    /// names individual mutating bits because FullControl and Modify are composites that also carry
    /// the read bits, so testing against them would match a read-only grant.
    /// </summary>
    private static bool GrantsForeignMutatingAccess(FileSystemSecurity security, SecurityIdentifier owner)
    {
        const FileSystemRights mutating = FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        // An ACE may also carry the generic rights, which FileSystemRights has no names for and
        // which intersect none of the specific bits above. The kernel maps GENERIC_ALL to
        // FILE_ALL_ACCESS (DELETE, FILE_DELETE_CHILD, WRITE_DAC) and GENERIC_WRITE on a directory
        // to FILE_ADD_FILE, so a grant in that form can delete or replace the receipt while every
        // named bit reads as clear. GENERIC_READ and GENERIC_EXECUTE are harmless.
        const int genericAll = 0x10000000;
        const int genericWrite = 0x40000000;
        const int rejected = (int)mutating | genericAll | genericWrite;

        var trusted = TrustedPrincipals(owner);
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Array.IndexOf(trusted, sid) >= 0)
                continue;
            // An inherit-only entry describes children and grants nothing on this object.
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;
            if (((int)rule.FileSystemRights & rejected) != 0)
                return true;
        }

        return false;
    }

    private static SecurityIdentifier[] TrustedPrincipals(SecurityIdentifier owner) =>
    [
        owner,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];
}
