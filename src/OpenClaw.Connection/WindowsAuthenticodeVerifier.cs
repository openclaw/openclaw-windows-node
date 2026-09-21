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
    internal const string OpenClawPublisherSubject =
        "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US";

    public static AuthenticodeTrustResult VerifyMicrosoftSignedFile(string path) =>
        VerifySignedFile(path, HasMicrosoftPublisherIdentity, "WSL relay", "Microsoft Corporation", false);

    public static AuthenticodeTrustResult VerifyOpenClawSignedFile(string path) =>
        VerifySignedFile(path,
            subject => string.Equals(subject, OpenClawPublisherSubject, StringComparison.OrdinalIgnoreCase),
            "OpenClaw update", "OpenClaw Foundation", true);

    private static AuthenticodeTrustResult VerifySignedFile(
        string path,
        Func<string, bool> hasExpectedPublisher,
        string purpose,
        string publisher,
        bool requireTimestamp)
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
                    $"{purpose} Authenticode verification failed ({signature.State}).");
            }
            if (signingCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"{purpose} Authenticode signer could not be read.");
            }
            if (!hasExpectedPublisher(signingCertificate.Subject))
            {
                return AuthenticodeTrustResult.Rejected(
                    $"{purpose} Authenticode signer is not {publisher}.");
            }
            if (requireTimestamp && timestampCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"{purpose} signature has no Authenticode timestamp.");
            }
            return AuthenticodeTrustResult.Trusted();
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                $"{purpose} Authenticode verification could not complete.");
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
