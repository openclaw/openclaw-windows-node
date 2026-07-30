using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Filesystem-integrity guards for the exec-approvals policy store, mirroring the macOS
/// store's symlink and hard-link protections on Windows:
/// <list type="bullet">
/// <item>reparse-point rejection (symlink/junction) on the policy file and its immediate
/// parent directory, the Windows analogue of <c>O_NOFOLLOW</c> plus symlink-parent
/// rejection, bounded to the store's own file and data dir;</item>
/// <item>hard-link detection (<c>nNumberOfLinks == 1</c>), so a second path alias cannot be
/// used to observe or divert the policy file.</item>
/// </list>
/// Both checks fail closed: any inspection error is treated as "not trustworthy".
/// </summary>
internal static class ExecApprovalsPathGuard
{
    /// <summary>
    /// True when neither <paramref name="filePath"/> nor its immediate parent directory (the
    /// OpenClaw data dir) is a reparse point, so the store cannot be redirected by swapping the
    /// file for a symlink or the data dir for a junction. The check is bounded to the store's
    /// own file and directory: ancestors above the data dir are OS-controlled and may
    /// legitimately be junctions on some Windows profiles.
    /// </summary>
    public static bool IsPathTrustworthy(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);

            if (File.Exists(full) && IsReparsePoint(File.GetAttributes(full)))
                return false;

            var parent = Directory.GetParent(full);
            if (parent is not null && parent.Exists && IsReparsePoint(parent.Attributes))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the file is referenced by exactly one directory entry (no hard-link alias).
    /// Windows-only; on other platforms it returns true (the store runs on the Windows node,
    /// and the reparse-point guard still applies everywhere).
    /// </summary>
    public static bool HasSingleHardLink(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            using var handle = CreateFileW(
                filePath,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            if (!GetFileInformationByHandle(handle, out var info))
                return false;

            return info.NumberOfLinks == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(FileAttributes attributes)
        => (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
