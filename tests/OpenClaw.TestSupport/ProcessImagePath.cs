using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.TestSupport;

/// <summary>Reads the observed Windows executable image without depending on loader initialization.</summary>
public static class ProcessImagePath
{
    private const int MaxPathCharacters = 32768;

    public static string Read(SafeProcessHandle processHandle)
    {
        var path = new StringBuilder(MaxPathCharacters);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the fixture process image.");
        if (length == 0)
            throw new InvalidOperationException("The fixture process image query returned an empty path.");
        return path.ToString(0, length);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder imagePath, ref int size);
}
