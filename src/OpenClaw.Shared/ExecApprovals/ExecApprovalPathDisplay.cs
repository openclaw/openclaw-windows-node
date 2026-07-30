using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Expands Windows 8.3 short path components (for example <c>PROGRA~1</c>) to their long form
/// for display in the approval prompt, so the executable path the owner reviews reads the way
/// it looks on disk. Display-only: the resolved binary is unchanged (8.3 and long names refer
/// to the same file). No-op off Windows, on empty input, or when the path cannot be expanded.
/// </summary>
internal static class ExecApprovalPathDisplay
{
    public static string? ExpandShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
            return path;

        // Fast path: a path with no "~" cannot contain an 8.3 component.
        if (!path.Contains('~'))
            return path;

        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
            if (length == 0)
                return path; // path does not exist or has no long form; leave as-is

            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length);
                length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
                if (length == 0)
                    return path;
            }

            return buffer.ToString(0, (int)Math.Min(length, buffer.Length));
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
}
