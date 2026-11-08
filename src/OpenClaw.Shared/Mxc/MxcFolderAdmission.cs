using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.Mxc;

/// <summary>Metadata-only admission of ordinary paths and non-remapping Cloud Files directories.</summary>
internal static class MxcFolderAdmission
{
    internal readonly record struct Metadata(FileAttributes Attributes, uint ReparseTag, string FinalPath);

    internal static void Validate(string path, Func<string, Metadata?> readMetadata)
    {
        var components = new Stack<string>();
        for (var current = NormalizeIdentity(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            components.Push(current);

        // Validate ancestors before opening a descendant. OPEN_REPARSE_POINT applies only to the
        // final component of each metadata handle, so a leaf-first walk could follow an unchecked alias.
        while (components.TryPop(out var component))
        {
            Metadata? metadata;
            try { metadata = readMetadata(component); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                throw new NotSupportedException(
                    $"Folder component '{component}': metadata could not be verified. " +
                    "Check folder access and availability in Node Sandbox settings.", ex);
            }
            // Missing paths remain prospective grants; unlike Exists(), query failures are not missing.
            if (metadata is { } existing && RejectionReason(component, existing) is { } reason)
                throw new NotSupportedException(
                    $"Folder component '{component}': {reason} Review Node Sandbox settings.");
        }
    }

    internal static bool IsAdmissible(string path, Metadata metadata) => RejectionReason(path, metadata) is null;

    private static string? RejectionReason(string path, Metadata metadata)
    {
        var reparse = (metadata.Attributes & FileAttributes.ReparsePoint) != 0;
        if (reparse)
        {
            // IO_REPARSE_TAG_CLOUD and CLOUD_1..F only. This excludes name-surrogate and
            // legacy OneDrive/ProjFS/WCI tags; a null LinkTarget is never an admission signal.
            if ((metadata.Attributes & FileAttributes.Directory) == 0 ||
                (metadata.ReparseTag & 0xFFFF0FFFu) != 0x9000001Au)
                return "Links, junctions and unsupported reparse points are not supported. " +
                    "Folder grants and working folders require ordinary paths or verified Cloud Files directories.";
        }
        // FILE_ATTRIBUTE_TAG_INFO.ReparseTag is undefined unless FILE_ATTRIBUTE_REPARSE_POINT is set.

        try
        {
            return string.Equals(NormalizeIdentity(path), NormalizeIdentity(metadata.FinalPath),
                StringComparison.OrdinalIgnoreCase) ? null :
                "The resolved physical path differs from the requested path. " +
                "Mapped-drive, SUBST and other physical-path aliases are not supported.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return "The resolved physical path could not be verified."; }
    }

    internal static Metadata? ReadMetadata(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("Windows folder metadata is required for MXC admission.");

        const uint fileReadAttributes = 0x80;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(path, fileReadAttributes, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new Win32Exception(error);
        }
        // FILE_ATTRIBUTE_TAG_INFO is obtained from the same no-follow handle, without opening data
        // or requesting hydration. Do not replace this with File.OpenRead or a path-following resolver.
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var capacity = 512;
        while (capacity <= 32_768)
        {
            var final = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, final, (uint)capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < capacity)
                return new Metadata((FileAttributes)info.Attributes, info.ReparseTag, final.ToString());
            capacity = checked((int)length + 1);
        }
        throw new IOException("Folder metadata exceeded the supported path length.");
    }

    internal static string NormalizeIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Folder metadata must name an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
