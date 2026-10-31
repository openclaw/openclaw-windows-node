using System;
using System.Security.AccessControl;
using System.Security.Principal;

public static class PrivateDesktopSecurity
{
    // Existing token-default ACEs already provide read/enumerate/switch rights.
    private const int CreateWindowAndWriteObjects = 0x0082;
    private const AceType MandatoryLabelAceType = (AceType)0x11;
    private static readonly string[] PackageGroups = { "S-1-15-2-1", "S-1-15-2-2" };

    public static byte[] CompleteNullCreatedDesktop(byte[] created)
    {
        var descriptor = new RawSecurityDescriptor(created, 0);
        if (descriptor.DiscretionaryAcl == null)
            throw new InvalidOperationException("A private desktop must have an explicit DACL.");
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            if (ace is CommonAce denied && denied.AceQualifier == AceQualifier.AccessDenied &&
                (denied.AccessMask & CreateWindowAndWriteObjects) != 0)
                throw new InvalidOperationException("An existing desktop write/create denial must not be overridden.");
        foreach (var text in PackageGroups)
        {
            var sid = new SecurityIdentifier(text);
            int allowed = 0;
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
                if (ace is CommonAce common && common.AceQualifier == AceQualifier.AccessAllowed &&
                    common.SecurityIdentifier.Equals(sid) &&
                    (common.AceFlags & AceFlags.InheritOnly) == 0)
                    allowed |= common.AccessMask;
            int missing = CreateWindowAndWriteObjects & ~allowed;
            if (missing == 0) continue;
            int insertion = descriptor.DiscretionaryAcl.Count;
            for (int index = 0; index < descriptor.DiscretionaryAcl.Count; index++)
                if ((descriptor.DiscretionaryAcl[index].AceFlags & AceFlags.Inherited) != 0)
                {
                    insertion = index;
                    break;
                }
            descriptor.DiscretionaryAcl.InsertAce(insertion,
                new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, missing, sid, false, null));
        }
        var low = new RawSecurityDescriptor("S:(ML;;NW;;;LW)").SystemAcl[0];
        var labels = new RawAcl(GenericAcl.AclRevision, (descriptor.SystemAcl?.Count ?? 0) + 1);
        if (descriptor.SystemAcl != null)
            foreach (GenericAce ace in descriptor.SystemAcl)
                if (ace.AceType != MandatoryLabelAceType)
                    labels.InsertAce(labels.Count, ace);
        labels.InsertAce(labels.Count, low);
        descriptor.SystemAcl = labels;
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.SystemAclPresent);
        var result = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(result, 0);
        return result;
    }
}
