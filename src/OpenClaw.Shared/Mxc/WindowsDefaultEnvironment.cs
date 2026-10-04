using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.Mxc;

/// <summary>Matches MXC's clean current-user defaults, without process inheritance.</summary>
internal static class WindowsDefaultEnvironment
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    internal static Dictionary<string, string?> Read()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows default environment requires Windows.");
        // CreateEnvironmentBlock requires both rights for a primary token.
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!CreateEnvironmentBlock(out var block, token, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var cursor = block; Marshal.ReadInt16(cursor) != 0;)
                {
                    var entry = Marshal.PtrToStringUni(cursor)!;
                    // Hidden drive-current-directory keys begin with '='.
                    var separator = entry.IndexOf('=', 1);
                    if (separator > 0) entries[entry[..separator]] = entry[(separator + 1)..];
                    cursor += checked((entry.Length + 1) * sizeof(char));
                }
                return entries;
            }
            finally
            {
                DestroyEnvironmentBlock(block);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
