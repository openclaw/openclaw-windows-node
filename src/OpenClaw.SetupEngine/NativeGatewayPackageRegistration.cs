using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Outcome of registering the already staged Store Gateway for the current user.</summary>
public enum NativeGatewayStagedRegistrationResult
{
    Registered,
    NotStaged,
}

/// <summary>Classification and user guidance for current-user registration of the staged Store Gateway.</summary>
public static class NativeGatewayPackageRegistration
{
    internal const int ErrorInstallPackageNotFound = unchecked((int)0x80073CF1);
    internal const int ErrorNotFound = unchecked((int)0x80070490);

    /// <summary>Manual equivalent shown to users. Current user only, no elevation, no -AllUsers.</summary>
    public const string ManualRegistrationCommand =
        "Add-AppxPackage -RegisterByFamilyName -MainPackage " + NativeGatewayPackageIdentity.StoreFamilyName;

    /// <summary>
    /// True when Windows has nothing staged for the family. On Windows 11 26100, an unstaged family
    /// fails with 0x80073CF9 and DeploymentResult.ExtendedErrorCode 0x80070490; 0x80073CF1 is the
    /// documented "package couldn't be found" code. When unsure, return true: the caller keeps the
    /// existing bounded wait instead of failing a deployment that may still finish.
    /// </summary>
    public static bool IsNotStaged(int errorCode, int extendedErrorCode) =>
        errorCode == ErrorInstallPackageNotFound ||
        extendedErrorCode is ErrorNotFound or ErrorInstallPackageNotFound;
}

/// <summary>Windows refused to register the staged Store Gateway for a reason other than "not staged".</summary>
public sealed class NativeGatewayPackageRegistrationException : InvalidOperationException
{
    public NativeGatewayPackageRegistrationException(int errorCode, int extendedErrorCode, Guid activityId)
        : base("Windows could not register the staged OpenClaw Gateway package for this Windows user " +
               $"(error 0x{errorCode:X8}, extended error 0x{extendedErrorCode:X8}). Install OpenClaw Gateway from " +
               $"Microsoft Store, or run {NativeGatewayPackageRegistration.ManualRegistrationCommand} in PowerShell, " +
               "then retry native setup.")
    {
        ErrorCode = errorCode;
        ExtendedErrorCode = extendedErrorCode;
        ActivityId = activityId;
        HResult = errorCode;
    }

    public int ErrorCode { get; }

    public int ExtendedErrorCode { get; }

    /// <summary>Windows deployment activity; <c>Get-AppPackageLog -ActivityID</c> shows its events.</summary>
    public Guid ActivityId { get; }
}
