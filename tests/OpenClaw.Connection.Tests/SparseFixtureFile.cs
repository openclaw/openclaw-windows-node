using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Creates an isolated zero-filled test file with its real logical length, without
/// allocating model-sized storage. This is not a model download or hash substitute.
/// </summary>
internal static class SparseFixtureFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    public static void Create(string path, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        // Windows requires the sparse attribute before extending the file. Unix
        // SetLength already leaves the unwritten range as a filesystem hole.
        if (OperatingSystem.IsWindows() &&
            !DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, 0, 0, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not mark the test fixture sparse.");
        }

        stream.SetLength(length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint input,
        uint inputSize,
        nint output,
        uint outputSize,
        out uint bytesReturned,
        nint overlapped);
}
