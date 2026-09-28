using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap;

/// <summary>Owns current-user Windows identity, handle-bound filesystem admission and private creation.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthority
{
    public string Sid { get; }
    public string Root { get; }
    private readonly HashSet<string> trusted;
    private readonly string trustedInstallerSid;
    public WindowsAuthority()
    {
        Sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new ContractException("unsafe_acl");
        if (!ManagementContract.IsSid(Sid) || Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") throw new ContractException("unsafe_acl");
        trusted = new(StringComparer.Ordinal) { Sid, "S-1-5-18", "S-1-5-32-544" };
        trustedInstallerSid = ((SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value;
        // On Windows this BCL API resolves the user's shell Known Folder, not LOCALAPPDATA env overrides.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Root = Path.Combine(local, "OpenClawTray", "browser-native", "generations");
        if (!ManagementContract.IsPath(Root)) throw new ContractException("unsafe_path");
    }
    public bool IsTrusted(string sid) => trusted.Contains(sid);
    public void CheckAcl(RawSecurityDescriptor sd, bool privacy, bool registry = false, bool protectedAncestor = false)
    {
        bool Allowed(string sid) => trusted.Contains(sid) || (!privacy && !registry && protectedAncestor && sid == trustedInstallerSid);
        if (sd.Owner is null || !Allowed(sd.Owner.Value) || sd.DiscretionaryAcl is null) throw new ContractException("unsafe_acl");
        // Creating unrelated siblings is not authority to replace an admitted existing child.
        var fileWrite = protectedAncestor ? 0x500d0150 : 0x500d0156;
        const int registryWrite = 0x000d0006 | 0x10000000 | 0x40000000;
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not CommonAce q || q.IsCallback || q.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new ContractException("unsafe_acl");
            if(q.AceQualifier != AceQualifier.AccessAllowed) continue;
            if (!Allowed(q.SecurityIdentifier.Value) &&
                (privacy || (q.AccessMask & (registry ? registryWrite : fileWrite)) != 0)) throw new ContractException("unsafe_acl");
        }
    }
    public Lease Admit(string path, bool directory, bool privacy, bool allowMissing = false)
    {
        if (!ManagementContract.IsPath(path)) throw new ContractException("unsafe_path");
        var lease = new Lease();
        try
        {
            var chain = new List<string>();
            for (string? p = path; p is not null; p = Path.GetDirectoryName(p)) chain.Add(p);
            chain.Reverse();
            var missing = false;
            foreach (var p in chain)
            {
                var isLeaf = ManagementContract.PathEquals(p, path);
                var h = CreateFile(p, 0x00020080, 3, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error(); h.Dispose();
                    if (allowMissing && error is 2 or 3) { missing = true; continue; }
                    throw new ContractException(error == 5 ? "unsafe_acl" : "unsafe_path");
                }
                lease.Handles.Add(h);
                if (missing || !GetFileInformationByHandle(h, out var info) || (info.Attributes & 0x400) != 0 ||
                    ((info.Attributes & 0x10) != 0) != (!isLeaf || directory)) throw new ContractException("unsafe_path");
                var final = new char[32768];
                var count = GetFinalPathNameByHandle(h, final, (uint)final.Length, 0);
                if (count == 0 || count >= final.Length) throw new ContractException("unsafe_path");
                var canonical = new string(final, 0, (int)count);
                if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) canonical = canonical[4..];
                if (!ManagementContract.PathEquals(canonical, p)) throw new ContractException("unsafe_path");
                CheckHandle(h, isLeaf && privacy, protectedAncestor: !isLeaf);
            }
            lease.Exists = !missing;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public void CheckHandle(SafeFileHandle handle, bool privacy, bool protectedAncestor = false)
    {
        var code = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var sd);
        if (code != 0) throw new ContractException("unsafe_acl");
        try
        {
            var length = GetSecurityDescriptorLength(sd);
            if (length == 0 || length > 1024 * 1024) throw new ContractException("unsafe_acl");
            var bytes = new byte[length]; Marshal.Copy(sd, bytes, 0, bytes.Length);
            CheckAcl(new RawSecurityDescriptor(bytes, 0), privacy, protectedAncestor: protectedAncestor);
        }
        finally { LocalFree(sd); }
    }
    public string FileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new ContractException("unsafe_path");
        return $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
    }
    public byte[] Read(string path, int limit, bool privacy)
    {
        using var parents = Admit(path, false, privacy);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckHandle(stream.SafeFileHandle, privacy);
        if (stream.Length is <= 0 || stream.Length > limit) throw new ContractException("binding_invalid");
        var data = new byte[(int)stream.Length]; stream.ReadExactly(data); return data;
    }
    public void EnsurePrivateDirectory(string path)
    {
        using var current = Admit(path, true, true, allowMissing: true);
        if (current.Exists) return;
        var parent = Path.GetDirectoryName(path) ?? throw new ContractException("unsafe_path");
        if (!Directory.Exists(parent)) EnsurePrivateDirectory(parent);
        using var admittedParent = Admit(parent, true, false);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier(Sid));
        foreach (var sid in trusted) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
        using var verified = Admit(path, true, true);
    }
    public IDisposable Lock()
    {
        // Mutex ACL APIs are supplied by the Windows access-control package.
        var acl = new MutexSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier(Sid));
        foreach (var sid in trusted) acl.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid), MutexRights.FullControl, AccessControlType.Allow));
        var mutex = MutexAcl.Create(false, @"Global\OpenClaw.Browser.NativeRegistration." + Sid, out _, acl);
        try
        {
            var bytes = mutex.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var sd = new RawSecurityDescriptor(bytes, 0);
            if (sd.Owner is null || !trusted.Contains(sd.Owner.Value) || sd.DiscretionaryAcl is null) throw new ContractException("unsafe_acl");
            foreach (GenericAce ace in sd.DiscretionaryAcl)
                if (ace is QualifiedAce q && q.AceQualifier == AceQualifier.AccessAllowed && !trusted.Contains(q.SecurityIdentifier.Value)) throw new ContractException("unsafe_acl");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new ContractException("busy");
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    internal sealed class Lease : IDisposable
    {
        public bool Exists { get; set; }
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var h in Handles) h.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h, [Out] char[] path, uint length, uint flags);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle h, uint type, uint flags, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
