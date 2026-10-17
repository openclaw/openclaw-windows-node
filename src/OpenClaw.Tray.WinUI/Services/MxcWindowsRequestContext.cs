using System.Runtime.InteropServices;
using OpenClaw.Shared.Mxc;
using Windows.Management.Deployment;

namespace OpenClawTray.Services;

/// <summary>Reads current-user registration on each contained command, without activating packages.</summary>
internal static class MxcWindowsRequestContext
{
    internal static MxcRequestContext Capture(string settingsDirectory) =>
        MxcCompanionPackagePaths.Capture(
            MxcRequestContext.Capture(settingsDirectory), EnumeratePackages, UnredirectedLocalAppData);

    private static IEnumerable<MxcCompanionPackage> EnumeratePackages()
    {
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            if (package.IsFramework || package.IsResourcePackage)
                continue;
            var id = package.Id;
            yield return new(id.Name, id.Publisher, id.FamilyName);
        }
    }

    private static string UnredirectedLocalAppData()
    {
        // The ordinary known-folder result can already be inside this package's LocalCache.
        const uint KF_FLAG_NO_PACKAGE_REDIRECTION = 0x00010000;
        var hr = SHGetKnownFolderPath(new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
            KF_FLAG_NO_PACKAGE_REDIRECTION, IntPtr.Zero, out var pointer);
        try
        {
            if (hr != 0 || pointer == IntPtr.Zero)
                throw new InvalidOperationException($"Windows local app data resolution failed (0x{hr:X8}).");
            return Marshal.PtrToStringUni(pointer)
                ?? throw new InvalidOperationException("Windows local app data resolution returned no path.");
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
