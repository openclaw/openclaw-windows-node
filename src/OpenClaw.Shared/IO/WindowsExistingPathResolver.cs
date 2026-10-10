using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.IO;

/// <summary>
/// Resolves an existing file or directory through its open handle to obtain the
/// physical path behind MSIX filesystem virtualization. Callers own containment policy.
/// </summary>
public static class WindowsExistingPathResolver
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return fullPath;

        using SafeFileHandle handle = Directory.Exists(fullPath)
            ? CreateFileW(
                fullPath,
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero)
            : File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"The existing Windows path could not be opened (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        int capacity = 512;
        while (capacity <= 32_768)
        {
            var builder = new StringBuilder(capacity);
            uint length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
            {
                throw new IOException(
                    $"The existing Windows path could not be resolved (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (length < builder.Capacity)
                return WindowsPathSafety.NormalizePath(NormalizeFinalPath(builder.ToString()));

            capacity = checked((int)length + 1);
        }

        throw new IOException("The existing Windows path exceeded the supported length.");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUncPrefix.Length..];

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
