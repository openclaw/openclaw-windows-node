using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.Connection.LocalAi;

/// <summary>
/// A stable, current-user protected credential, separate from portable artifact receipts.
/// Corrupt credentials fail closed; replacing one requires explicit endpoint withdrawal.
/// </summary>
public sealed class LocalAiApiCredentialStore(LocalAiPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenClaw.LocalAI.ApiCredential.v1");

    internal static string RequireApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 ||
            apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidDataException("The Local AI API credential is invalid.");
        return apiKey;
    }

    public string GetOrCreate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local AI API credentials require Windows user protection.");
        paths.EnsureDirectories();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "Local AI API credential");
        if (File.Exists(path)) return Read(path);
        byte[] plain = RandomNumberGenerator.GetBytes(32);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var staging = paths.ResolveContainedPath($"api-credential-{Guid.NewGuid():N}.pending", "Local AI API credential");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(protectedBytes);
                file.Flush(flushToDisk: true);
            }
            McpAuthToken.TryRestrictSensitiveFileAcl(staging);
            try { File.Move(staging, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* Another owner won the initial creation. */ }
            return Read(path);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Read(string path)
    {
        if (new FileInfo(path).Length is < 1 or > 4096)
            throw new InvalidDataException("The protected Local AI API credential is invalid.");
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length != 32)
                throw new InvalidDataException("The protected Local AI API credential is invalid.");
            return Convert.ToHexString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
