using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Win32 probe for "am I currently running inside an AppContainer?".
/// No SDK API exists for this (verified in the sq-agos-tessera-process detection
/// thread). Reads the same data Task Manager's "Isolation" column reads, via
/// <c>GetTokenInformation(TokenIsAppContainer)</c>.
/// </summary>
public static class MxcEnvironment
{
    private const int TOKEN_QUERY = 0x0008;
    private const int TokenIsAppContainer = 29;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Returns true if the calling process token has <c>TokenIsAppContainer == 1</c>.
    /// Not all sandboxes set this (LPAC, isolation_session may differ); this is a
    /// best-effort probe matching MXC's confirmed AppContainer backend.
    /// Returns false on non-Windows or if any Win32 call fails.
    /// </summary>
    public static bool IsInsideAppContainer()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var processHandle = GetCurrentProcess();
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle))
            return false;

        try
        {
            if (!GetTokenInformation(tokenHandle, TokenIsAppContainer, out var isAppContainer, sizeof(uint), out _))
                return false;

            return isAppContainer != 0;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }
}
