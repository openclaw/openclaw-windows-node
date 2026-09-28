// Proof-only read-only mirror of producer6dd WindowsAuthority.CheckAcl/Admit predicates. No grants or mutation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class BrowserNativePathAudit
{
    public sealed class Row
    {
        public string Role, Component, Rule, Principal, Mask, MatchedMask;
        public int AceIndex = -1, AceFlags, Win32;
        public bool Accepted;
    }
    private static string Kind(string sid, string user)
    {
        if (sid == user) return "current_user";
        switch (sid) {
            case "S-1-5-18": return "system";
            case "S-1-5-32-544": return "administrators";
            case "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464": return "trusted_installer";
            case "S-1-5-32-545": return "builtin_users";
            case "S-1-5-11": return "authenticated_users";
            case "S-1-1-0": return "everyone";
            default: return "other_principal";
        }
    }
    private static bool Allowed(string kind, bool ancestor) {
        return kind == "current_user" || kind == "system" || kind == "administrators" || (ancestor && kind == "trusted_installer");
    }
    public static Row[] Inspect(string role, string target, bool privacy, bool directory, bool allowMissing)
    {
        string user = WindowsIdentity.GetCurrent().User.Value;
        var chain = new List<string>();
        for (string p = target; p != null; p = Path.GetDirectoryName(p)) chain.Add(p);
        chain.Reverse();
        var rows = new List<Row>();
        bool missing = false;
        for (int i = 0; i < chain.Count; i++) {
            bool leaf = i == chain.Count - 1;
            var row = new Row { Role = role, Component = leaf ? "leaf" : "ancestor-" + (chain.Count - 1 - i), Rule = "accepted" };
            rows.Add(row);
            using (var h = CreateFile(chain[i], 0x00020080, 3, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero)) {
                if (h.IsInvalid) {
                    row.Win32 = Marshal.GetLastWin32Error();
                    row.Accepted = allowMissing && (row.Win32 == 2 || row.Win32 == 3);
                    row.Rule = row.Accepted ? "missing_allowed" : "open_failed";
                    if (row.Accepted) missing = true;
                    continue;
                }
                if (missing) { row.Rule = "missing_chain_changed"; continue; }
                FileInformation info;
                if (!GetFileInformationByHandle(h, out info)) { row.Rule = "file_info_failed"; row.Win32 = Marshal.GetLastWin32Error(); continue; }
                if ((info.Attributes & 0x400) != 0) { row.Rule = "reparse_point"; continue; }
                if (((info.Attributes & 0x10) != 0) != (!leaf || directory)) { row.Rule = "kind_mismatch"; continue; }
                var chars = new char[32768]; uint count = GetFinalPathNameByHandle(h, chars, (uint)chars.Length, 0);
                if (count == 0 || count >= chars.Length) { row.Rule = "canonical_query_failed"; continue; }
                string canonical = new string(chars, 0, (int)count);
                if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) canonical = canonical.Substring(4);
                if (!String.Equals(canonical, chain[i], StringComparison.OrdinalIgnoreCase)) { row.Rule = "canonical_mismatch"; continue; }
                IntPtr owner, group, dacl, sacl, descriptor;
                uint error = GetSecurityInfo(h, 1, 5, out owner, out group, out dacl, out sacl, out descriptor);
                if (error != 0) { row.Rule = "security_query_failed"; row.Win32 = (int)error; continue; }
                try {
                    uint length = GetSecurityDescriptorLength(descriptor);
                    if (length == 0 || length > 1048576) { row.Rule = "descriptor_bound"; continue; }
                    var bytes = new byte[(int)length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                    var sd = new RawSecurityDescriptor(bytes, 0);
                    row.Principal = sd.Owner == null ? "missing_owner" : Kind(sd.Owner.Value, user);
                    if (!Allowed(row.Principal, !leaf)) { row.Rule = "untrusted_owner"; continue; }
                    if (sd.DiscretionaryAcl == null) { row.Rule = "null_dacl"; continue; }
                    uint writes = leaf ? 0x500d0156u : 0x500d0150u;
                    row.Accepted = true;
                    for (int a = 0; a < sd.DiscretionaryAcl.Count; a++) {
                        GenericAce ace = sd.DiscretionaryAcl[a];
                        if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                        var q = ace as CommonAce;
                        if (q == null || q.IsCallback || (q.AceQualifier != AceQualifier.AccessAllowed && q.AceQualifier != AceQualifier.AccessDenied)) {
                            row.Accepted = false; row.Rule = "unsupported_ace"; row.AceIndex = a; row.AceFlags = (int)ace.AceFlags; break;
                        }
                        if (q.AceQualifier != AceQualifier.AccessAllowed) continue;
                        string principal = Kind(q.SecurityIdentifier.Value, user);
                        uint mask = unchecked((uint)q.AccessMask);
                        if (!Allowed(principal, !leaf) && ((leaf && privacy) || (mask & writes) != 0)) {
                            row.Accepted = false; row.Rule = leaf && privacy ? "foreign_private_allow" : "foreign_allow_write";
                            row.Principal = principal; row.AceIndex = a; row.AceFlags = (int)ace.AceFlags;
                            row.Mask = "0x" + mask.ToString("x8"); row.MatchedMask = "0x" + (mask & writes).ToString("x8"); break;
                        }
                    }
                } finally { LocalFree(descriptor); }
            }
        }
        return rows.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateFileW")]
    private static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sd, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInformation i);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, EntryPoint="GetFinalPathNameByHandleW", SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h, [Out] char[] p, uint n, uint f);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle h, uint t, uint f, out IntPtr o, out IntPtr g, out IntPtr d, out IntPtr s, out IntPtr sd);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr sd);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
}
