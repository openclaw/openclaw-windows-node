using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Marks a test that can only run on Windows (e.g. tests that exercise
/// Windows Data Protection API, NTFS reparse points, or other Win32 surfaces).
/// The test is automatically skipped on non-Windows platforms.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: requires a Windows platform API.";
        }
    }
}
