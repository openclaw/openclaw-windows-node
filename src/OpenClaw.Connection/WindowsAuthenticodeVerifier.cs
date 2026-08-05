using System;
using System.IO;
using Microsoft.Security.Extensions;

namespace OpenClaw.Connection;

internal readonly record struct AuthenticodeTrustResult(bool IsTrusted, string? Detail)
{
    public static AuthenticodeTrustResult Trusted() => new(true, null);

    public static AuthenticodeTrustResult Rejected(string detail) => new(false, detail);
}

internal static class WindowsAuthenticodeVerifier
{
    public static AuthenticodeTrustResult VerifyMicrosoftSignedFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var signature = FileSignatureInfo.GetFromFileStream(stream);
            using var signingCertificate = signature.SigningCertificate;
            using var timestampCertificate = signature.TimestampCertificate;

            if (signature.State != SignatureState.SignedAndTrusted)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"WSL relay Authenticode verification failed ({signature.State}).");
            }
            if (signingCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer could not be read.");
            }

            return HasMicrosoftPublisherIdentity(signingCertificate.Subject)
                ? AuthenticodeTrustResult.Trusted()
                : AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer is not Microsoft Corporation.");
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }
    }

    internal static bool HasMicrosoftPublisherIdentity(string subject) =>
        subject.Split(',')
            .Select(part => part.Trim())
            .Any(part =>
                string.Equals(
                    part,
                    "O=Microsoft Corporation",
                    StringComparison.OrdinalIgnoreCase));
}
