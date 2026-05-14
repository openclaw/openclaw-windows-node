using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenClawTray.Services;

internal sealed record UpdateSignatureVerificationResult(
    bool IsTrusted,
    string Message,
    string? PublisherSubject = null);

internal static class UpdateSignatureVerifier
{
    internal const string PinnedPublisherSubject = "CN=Scott Hanselman";
    internal const string UpdateExecutableName = "OpenClaw.Tray.WinUI.exe";

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static UpdateSignatureVerificationResult VerifyUpdatePackage(string packagePath)
    {
        return VerifyUpdatePackage(packagePath, VerifyAuthenticodeSignature);
    }

    internal static UpdateSignatureVerificationResult VerifyUpdatePackage(
        string packagePath,
        Func<string, UpdateSignatureVerificationResult> verifyFile)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return new(false, "Update package is missing.");

        if (!string.Equals(Path.GetExtension(packagePath), ".zip", StringComparison.OrdinalIgnoreCase))
            return verifyFile(packagePath);

        return VerifyZipPackage(packagePath, verifyFile);
    }

    internal static bool PublisherSubjectMatches(string publisherSubject)
    {
        if (string.IsNullOrWhiteSpace(publisherSubject))
            return false;

        return publisherSubject
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, PinnedPublisherSubject, StringComparison.OrdinalIgnoreCase));
    }

    private static UpdateSignatureVerificationResult VerifyZipPackage(
        string packagePath,
        Func<string, UpdateSignatureVerificationResult> verifyFile)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var executableEntry = archive.Entries.SingleOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.FullName), UpdateExecutableName, StringComparison.OrdinalIgnoreCase));

        if (executableEntry == null)
            return new(false, $"Update package does not contain {UpdateExecutableName}.");

        var tempPath = Path.Combine(Path.GetTempPath(), "OpenClawUpdateVerify", Guid.NewGuid().ToString("N"), UpdateExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            executableEntry.ExtractToFile(tempPath);
            return verifyFile(tempPath);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(tempPath)!, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only; verification result should not depend on temp deletion.
            }
        }
    }

    private static UpdateSignatureVerificationResult VerifyAuthenticodeSignature(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Authenticode verification requires Windows.");

        var trustResult = WinVerifyTrustFile(filePath);
        if (trustResult != 0)
            return new(false, $"WinVerifyTrust failed with 0x{trustResult:X8}.");

        try
        {
#pragma warning disable SYSLIB0057 // Authenticode signer certificate extraction has no X509CertificateLoader equivalent.
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var certificate2 = new X509Certificate2(certificate);
            var subject = certificate2.Subject;

            if (!PublisherSubjectMatches(subject))
                return new(false, $"Unexpected update publisher: {subject}", subject);

            return new(true, "Update signature verified.", subject);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            return new(false, $"Could not read update signing certificate: {ex.Message}");
        }
    }

    private static int WinVerifyTrustFile(string filePath)
    {
        var fileInfo = new WintrustFileInfo(filePath);
        var fileInfoPtr = IntPtr.Zero;
        var trustData = new WintrustData();

        try
        {
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            trustData = new WintrustData(fileInfoPtr);
            var result = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref trustData);

            trustData.StateAction = WintrustDataStateAction.Close;
            _ = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref trustData);

            return result;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId,
        ref WintrustData pWvtData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WintrustFileInfo
    {
        private readonly int _structSize = Marshal.SizeOf<WintrustFileInfo>();
        private readonly string _filePath;
        private readonly IntPtr _file = IntPtr.Zero;
        private readonly IntPtr _knownSubject = IntPtr.Zero;

        public WintrustFileInfo(string filePath)
        {
            _filePath = filePath;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        private int _structSize;
        private IntPtr _policyCallbackData;
        private IntPtr _sipClientData;
        private WintrustDataUiChoice _uiChoice;
        private WintrustDataRevocationChecks _revocationChecks;
        private WintrustDataChoice _unionChoice;
        private IntPtr _file;
        public WintrustDataStateAction StateAction;
        private IntPtr _stateData;
        private string? _urlReference;
        private WintrustDataProvFlags _provFlags;
        private WintrustDataUiContext _uiContext;

        public WintrustData(IntPtr file)
        {
            _structSize = Marshal.SizeOf<WintrustData>();
            _policyCallbackData = IntPtr.Zero;
            _sipClientData = IntPtr.Zero;
            _uiChoice = WintrustDataUiChoice.None;
            _revocationChecks = WintrustDataRevocationChecks.WholeChain;
            _unionChoice = WintrustDataChoice.File;
            _file = file;
            StateAction = WintrustDataStateAction.Verify;
            _stateData = IntPtr.Zero;
            _urlReference = null;
            _provFlags = WintrustDataProvFlags.Safer;
            _uiContext = WintrustDataUiContext.Execute;
        }
    }

    private enum WintrustDataUiChoice : uint
    {
        None = 2
    }

    private enum WintrustDataRevocationChecks : uint
    {
        WholeChain = 1
    }

    private enum WintrustDataChoice : uint
    {
        File = 1
    }

    internal enum WintrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2
    }

    [Flags]
    private enum WintrustDataProvFlags : uint
    {
        Safer = 0x00000100
    }

    private enum WintrustDataUiContext : uint
    {
        Execute = 0
    }
}
