using System.Globalization;
using System.Text;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public sealed record GatewayMediaMessageProjectionResult(
    string ReconciliationText,
    string ResidualText,
    IReadOnlyList<ChatAttachmentPresentation> Attachments,
    bool HasMediaEnvelope)
{
    public string AttachmentPresentationSignature =>
        GatewayMediaMessageProjection.BuildAttachmentPresentationSignature(Attachments);

    public string AttachmentCorrelationSignature =>
        GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(Attachments);
}

/// <summary>
/// Projects trusted gateway media envelopes into inert presentation metadata.
/// It never emits the tray's private zero-width attachment marker syntax.
/// </summary>
public static class GatewayMediaMessageProjection
{
    private const string EnvelopePrefix = "[media attached: ";
    private const string MediaPrefix = "media://inbound/";
    private const int MaxDisplayFileNameLength = 160;

    public static GatewayMediaMessageProjectionResult Project(string? text)
    {
        var source = text ?? string.Empty;
        var attachments = new List<ChatAttachmentPresentation>();
        var offset = 0;

        while (offset < source.Length)
        {
            var lineEnd = source.IndexOf('\n', offset);
            var contentEnd = lineEnd >= 0 ? lineEnd : source.Length;
            var line = source.AsSpan(offset, contentEnd - offset);
            if (line.EndsWith("\r", StringComparison.Ordinal))
                line = line[..^1];

            if (!TryParseEnvelopeLine(line, out var attachment))
                break;

            attachments.Add(attachment);
            offset = lineEnd >= 0 ? lineEnd + 1 : source.Length;
        }

        if (attachments.Count == 0)
        {
            return new GatewayMediaMessageProjectionResult(
                source.Trim(),
                source,
                Array.Empty<ChatAttachmentPresentation>(),
                HasMediaEnvelope: false);
        }

        var residual = source[offset..];
        return new GatewayMediaMessageProjectionResult(
            residual.Trim(),
            residual,
            attachments.ToArray(),
            HasMediaEnvelope: true);
    }

    public static IReadOnlyList<ChatAttachmentPresentation> CreateLocalPresentations(
        IReadOnlyList<ChatAttachment>? attachments,
        Func<string> previewKeyFactory)
    {
        if (attachments is null || attachments.Count == 0)
            return Array.Empty<ChatAttachmentPresentation>();

        var presentations = new List<ChatAttachmentPresentation>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var displayName = NormalizeDisplayFileName(attachment.FileName);
            if (displayName.Length == 0)
                displayName = string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase)
                    ? "image"
                    : "attachment";

            var mimeType = NormalizeMimeType(attachment.MimeType);
            var isImage = string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase) ||
                mimeType.StartsWith("image/", StringComparison.Ordinal);
            presentations.Add(new ChatAttachmentPresentation(
                ChatAttachmentOrigin.Local,
                displayName,
                mimeType,
                isImage,
                isImage ? previewKeyFactory() : null));
        }

        return presentations.ToArray();
    }

    public static string BuildAttachmentPresentationSignature(
        IEnumerable<ChatAttachmentPresentation>? attachments)
    {
        if (attachments is null)
            return string.Empty;

        var items = attachments.ToArray();
        if (items.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append(items.Length.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var attachment in items)
        {
            AppendSignaturePart(builder, NormalizeMimeType(attachment.MimeType));
            AppendSignaturePart(builder, NormalizeDisplayFileName(attachment.DisplayFileName));
        }
        return builder.ToString();
    }

    public static string BuildAttachmentCorrelationSignature(
        IEnumerable<ChatAttachmentPresentation>? attachments)
    {
        if (attachments is null)
            return string.Empty;

        var items = attachments.ToArray();
        if (items.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append(items.Length.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var attachment in items)
        {
            AppendSignaturePart(builder, NormalizeMimeType(attachment.MimeType));
            AppendSignaturePart(builder, attachment.IsImage ? "image" : "file");
        }
        return builder.ToString();
    }

    public static string NormalizeDisplayFileName(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalizedSeparators = value.Replace('\\', '/');
        var leaf = normalizedSeparators[(normalizedSeparators.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(Math.Min(leaf.Length, MaxDisplayFileNameLength));
        var elements = StringInfo.GetTextElementEnumerator(leaf);
        while (elements.MoveNext() && builder.Length < MaxDisplayFileNameLength)
        {
            var element = elements.GetTextElement();
            if (!IsPrintableSingleLine(element))
                continue;
            if (builder.Length + element.Length > MaxDisplayFileNameLength)
                break;
            builder.Append(element);
        }

        return builder.ToString().Trim();
    }

    public static string NormalizeMimeType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return IsValidMimeType(normalized) ? normalized : "application/octet-stream";
    }

    private static bool TryParseEnvelopeLine(
        ReadOnlySpan<char> line,
        out ChatAttachmentPresentation attachment)
    {
        attachment = null!;
        if (!line.StartsWith(EnvelopePrefix, StringComparison.Ordinal) ||
            !line.EndsWith(")]", StringComparison.Ordinal))
        {
            return false;
        }

        var body = line[EnvelopePrefix.Length..^1];
        var annotationStart = body.LastIndexOf(" (", StringComparison.Ordinal);
        if (annotationStart <= 0)
            return false;

        var mediaUri = body[..annotationStart].ToString();
        var mimeType = body[(annotationStart + 2)..^1].ToString();
        if (!mediaUri.StartsWith(MediaPrefix, StringComparison.Ordinal) ||
            !IsValidMimeType(mimeType) ||
            mediaUri.IndexOfAny(['?', '#', '\r', '\n', '\t', ' ']) >= 0)
        {
            return false;
        }

        var encodedPath = mediaUri[MediaPrefix.Length..];
        var encodedLeaf = encodedPath[(encodedPath.LastIndexOf('/') + 1)..];
        if (encodedLeaf.Length == 0)
            return false;

        string decodedLeaf;
        try
        {
            decodedLeaf = Uri.UnescapeDataString(encodedLeaf);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var displayName = RemoveCanonicalStorageSuffix(NormalizeDisplayFileName(decodedLeaf));
        if (displayName.Length == 0)
            return false;

        var normalizedMimeType = mimeType.ToLowerInvariant();
        attachment = new ChatAttachmentPresentation(
            ChatAttachmentOrigin.GatewayReference,
            displayName,
            normalizedMimeType,
            normalizedMimeType.StartsWith("image/", StringComparison.Ordinal),
            PreviewCacheKey: null);
        return true;
    }

    private static bool IsValidMimeType(string value)
    {
        var slash = value.IndexOf('/');
        return slash > 0 &&
            slash == value.LastIndexOf('/') &&
            slash < value.Length - 1 &&
            IsMimeToken(value.AsSpan(0, slash)) &&
            IsMimeToken(value.AsSpan(slash + 1));
    }

    private static bool IsMimeToken(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                continue;
            if ("!#$%&'*+-.^_`|~".IndexOf(ch, StringComparison.Ordinal) >= 0)
                continue;
            return false;
        }
        return value.Length > 0;
    }

    private static bool IsPrintableSingleLine(string value)
    {
        foreach (var ch in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsControl(ch) ||
                category is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }
        return true;
    }

    private static string RemoveCanonicalStorageSuffix(string fileName)
    {
        var extensionStart = fileName.LastIndexOf('.');
        var stem = extensionStart > 0 ? fileName[..extensionStart] : fileName;
        var extension = extensionStart > 0 ? fileName[extensionStart..] : string.Empty;
        if (stem.Length < 39)
            return fileName;

        var suffix = stem[^39..];
        if (!suffix.StartsWith("---", StringComparison.Ordinal) ||
            !Guid.TryParseExact(suffix[3..], "D", out _))
        {
            return fileName;
        }

        var cleanedStem = stem[..^39];
        return cleanedStem.Length == 0 ? fileName : cleanedStem + extension;
    }

    private static void AppendSignaturePart(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
}
