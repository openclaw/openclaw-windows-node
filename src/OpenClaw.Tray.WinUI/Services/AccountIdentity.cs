using System.Buffers.Binary;

namespace OpenClawTray.Services;

/// <summary>Why one field of the Windows account identity is or is not available. Never carries the value.</summary>
internal enum AccountFieldStatus
{
    Available,
    /// <summary>The account has no value for this field, or the value is unusable.</summary>
    Empty,
    /// <summary>Windows privacy settings or policy refused access.</summary>
    Denied,
    /// <summary>The API is not present on this Windows version.</summary>
    Unsupported,
    /// <summary>Elevated, impersonated, or non-interactive process: the current user cannot be trusted, so nothing is read.</summary>
    ContextUnsupported,
    TooLarge,
    Failed,
}

/// <summary>A bounded, validated PNG or JPEG held in memory for the profile fill.</summary>
internal sealed record AccountPhotoDraft(byte[] Bytes, string Mime);

/// <summary>Per-field result so a refused photo never discards a valid name and the reverse.</summary>
internal sealed record AccountIdentityResult(
    AccountFieldStatus NameStatus,
    string? DisplayName,
    AccountFieldStatus PhotoStatus,
    AccountPhotoDraft? Photo)
{
    public static AccountIdentityResult Unavailable(AccountFieldStatus status) => new(status, null, status, null);
}

/// <summary>
/// Reads the display name and picture of the current Windows user. Implementations must never
/// enumerate other users and must not log or persist any value.
/// </summary>
internal interface IWindowsAccountIdentityProvider
{
    Task<AccountIdentityResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Display-name rules for the Gateway profile: users.setDisplayName takes a string of at most 256 UTF-16 units.</summary>
internal static class ProfileNamePolicy
{
    // packages/gateway-protocol/src/schema/users.ts @ v2026.10.2: UserProfileDisplayNameSchema maxLength 256.
    public const int MaxLength = 256;

    public static bool TryNormalize(string? raw, out string name)
    {
        name = "";
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength || trimmed.Any(char.IsControl))
            return false;
        name = trimmed;
        return true;
    }
}

/// <summary>
/// Input and output bounds for the account photo, checked from the encoded header before any pixel is
/// allocated. A compressed size is not a safe decode bound, so dimensions are checked separately.
/// </summary>
internal static class AccountPhotoPolicy
{
    public const int MaxSourceBytes = 2 * 1024 * 1024;
    public const int MaxSourceDimension = 1024;
    // The picture is requested at 208 px and is never upscaled; larger sources are only ever scaled down to it.
    public const int OutputEdge = 208;
    // MAX_USER_PROFILE_AVATAR_BYTES and users.setAvatar avatarBase64 maxLength @ v2026.10.2.
    public const int MaxEncodedBytes = 512 * 1024;
    public const int MaxBase64Length = 700_000;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    public static int Base64Length(int byteCount) => checked((byteCount + 2) / 3 * 4);

    /// <summary>Reads format and pixel size from the header only. False when the bytes are neither PNG nor JPEG.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out string mime, out int width, out int height)
    {
        mime = "";
        width = height = 0;
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= 24 && data[..8].SequenceEqual(pngSignature) &&
            data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R')
        {
            mime = Png;
            width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue);
            height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[20..]), int.MaxValue);
            return true;
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var offset = 2;
            while (offset + 4 <= data.Length)
            {
                if (data[offset] != 0xFF) return false;
                var marker = data[offset + 1];
                if (marker == 0xFF) { offset++; continue; }
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { offset += 2; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                if (length < 2) return false;
                var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
                if (isFrame)
                {
                    if (offset + 9 > data.Length) return false;
                    mime = Jpeg;
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..]);
                    return true;
                }
                offset += 2 + length;
            }
        }
        return false;
    }

    /// <summary>Pre-decode gate: encoded length and header dimensions within the input caps.</summary>
    public static AccountFieldStatus CheckSource(long length, ReadOnlySpan<byte> data)
    {
        if (length <= 0) return AccountFieldStatus.Empty;
        if (length > MaxSourceBytes) return AccountFieldStatus.TooLarge;
        if (!TryReadHeader(data, out _, out var width, out var height)) return AccountFieldStatus.Unsupported;
        return width is > 0 and <= MaxSourceDimension && height is > 0 and <= MaxSourceDimension
            ? AccountFieldStatus.Available
            : AccountFieldStatus.TooLarge;
    }

    /// <summary>Output gate applied to the bytes that would be uploaded.</summary>
    public static bool TryCreateDraft(byte[] bytes, out AccountPhotoDraft? draft)
    {
        draft = null;
        if (bytes.Length is 0 or > MaxEncodedBytes || Base64Length(bytes.Length) > MaxBase64Length ||
            CheckSource(bytes.Length, bytes) != AccountFieldStatus.Available ||
            !TryReadHeader(bytes, out var mime, out _, out _))
            return false;
        draft = new AccountPhotoDraft(bytes, mime);
        return true;
    }
}
