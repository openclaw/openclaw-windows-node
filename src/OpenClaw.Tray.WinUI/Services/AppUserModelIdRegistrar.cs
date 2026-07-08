using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClawTray.Services;

internal readonly record struct AppUserModelIdRegistrationResult(bool Attempted, int HResult);

internal static class AppUserModelIdRegistrar
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;

    public static AppUserModelIdRegistrationResult RegisterCurrentProcess(string appUserModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        if (HasPackageIdentity())
            return new AppUserModelIdRegistrationResult(Attempted: false, HResult: ErrorSuccess);

        return new AppUserModelIdRegistrationResult(
            Attempted: true,
            HResult: SetCurrentProcessExplicitAppUserModelID(appUserModelId));
    }

    private static bool HasPackageIdentity()
    {
        var packageFullNameLength = 0;
        var result = GetCurrentPackageFullName(ref packageFullNameLength, null);
        if (result == AppModelErrorNoPackage)
            return false;

        return result is ErrorSuccess or ErrorInsufficientBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder? packageFullName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);
}
