using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

internal sealed record AssistantMediaDirectiveProjection(
    string Text,
    IReadOnlyList<ChatMessageContentPartInfo> ContentParts,
    bool HasDirective);

/// <summary>
/// Projects assistant-only legacy MEDIA directives into typed transport
/// references. This is deliberately shared by history and live parsing so raw
/// Gateway paths cannot leak through notification or presentation text.
/// </summary>
internal static class AssistantMediaDirectiveParser
{
    private const int MaxSourceLength = 4096;
    private const int MaxFileNameLength = 255;
    internal const int MaxMediaReferences = 16;

    private static readonly Regex s_hasFileExtension =
        new(@"\.\w{1,10}$", RegexOptions.CultureInvariant);
    private static readonly Regex s_traversalSegment =
        new(@"(?:^|[/\\])\.\.(?:[/\\]|$)", RegexOptions.CultureInvariant);
    private static readonly Regex s_windowsDrive =
        new(@"^[a-zA-Z]:[\\/]", RegexOptions.CultureInvariant);
    private static readonly Regex s_scheme =
        new(@"^[a-zA-Z][a-zA-Z0-9+.-]*:", RegexOptions.CultureInvariant);

    public static AssistantMediaDirectiveProjection Project(string? role, string? raw)
    {
        var text = raw ?? string.Empty;
        if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            || text.IndexOf("MEDIA:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return new(text, TextOnly(text), HasDirective: false);
        }

        var trimmedRaw = text.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmedRaw))
            return new(string.Empty, Array.Empty<ChatMessageContentPartInfo>(), HasDirective: false);

        var keptLines = new List<string>();
        var parts = new List<ChatMessageContentPartInfo>();
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var foundDirective = false;
        var mediaReferenceCount = 0;

        foreach (var line in trimmedRaw.Split('\n'))
        {
            var lineWithoutCarriageReturn = line.EndsWith('\r') ? line[..^1] : line;
            if (TryReadFence(lineWithoutCarriageReturn, out var currentFence, out var currentLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceCharacter = currentFence;
                    fenceLength = currentLength;
                }
                else if (currentFence == fenceCharacter && currentLength >= fenceLength)
                {
                    inFence = false;
                }

                KeepTextLine(lineWithoutCarriageReturn, keptLines, parts);
                continue;
            }

            var trimmedStart = lineWithoutCarriageReturn.TrimStart();
            if (inFence || !trimmedStart.StartsWith("MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                KeepTextLine(lineWithoutCarriageReturn, keptLines, parts);
                continue;
            }

            foundDirective = true;
            var payload = trimmedStart["MEDIA:".Length..];
            var projection = ParsePayload(
                payload,
                Math.Max(0, MaxMediaReferences - mediaReferenceCount));
            if (projection.Media.Count > 0)
            {
                foreach (var media in projection.Media)
                {
                    parts.Add(new ChatMessageContentPartInfo
                    {
                        Kind = ChatMessageContentPartKind.Media,
                        Media = media,
                    });
                }
                mediaReferenceCount += projection.Media.Count;
            }

            if (!string.IsNullOrWhiteSpace(projection.ResidualText))
            {
                var residual = projection.ResidualText.Trim();
                keptLines.Add(residual);
                AppendTextPart(parts, residual);
            }
            else if (!projection.StripLine)
            {
                keptLines.Add(lineWithoutCarriageReturn);
                AppendTextPart(parts, lineWithoutCarriageReturn);
            }
        }

        var visibleText = string.Join('\n', keptLines);
        visibleText = Regex.Replace(visibleText, @"^(?:[ \t]*\n)+", string.Empty).TrimEnd();
        return new(visibleText, parts, foundDirective);
    }

    private static IReadOnlyList<ChatMessageContentPartInfo> TextOnly(string text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<ChatMessageContentPartInfo>()
            : new[]
            {
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Text,
                    Text = text,
                },
            };

    private static void KeepTextLine(
        string line,
        List<string> keptLines,
        List<ChatMessageContentPartInfo> parts)
    {
        keptLines.Add(line);
        AppendTextPart(parts, line);
    }

    private static void AppendTextPart(List<ChatMessageContentPartInfo> parts, string text)
    {
        if (parts.Count > 0 && parts[^1].Kind == ChatMessageContentPartKind.Text)
        {
            parts[^1].Text = $"{parts[^1].Text}\n{(text.Trim().Length > 0 ? text : string.Empty)}";
            return;
        }

        if (text.Trim().Length == 0)
            return;

        parts.Add(new ChatMessageContentPartInfo
        {
            Kind = ChatMessageContentPartKind.Text,
            Text = text,
        });
    }

    private static bool TryReadFence(string line, out char fenceCharacter, out int fenceLength)
    {
        fenceCharacter = '\0';
        fenceLength = 0;
        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;
        if (index >= line.Length || line[index] is not ('`' or '~'))
            return false;

        fenceCharacter = line[index];
        while (index + fenceLength < line.Length && line[index + fenceLength] == fenceCharacter)
            fenceLength++;
        return fenceLength >= 3;
    }

    private static PayloadProjection ParsePayload(string rawPayload, int remainingReferences)
    {
        var unwrapped = UnwrapQuoted(rawPayload);
        var payload = unwrapped ?? rawPayload;
        var candidates = unwrapped is not null
            ? new[] { unwrapped }
            : SplitUnquotedParts(rawPayload);
        var media = new List<ChatMediaContentInfo>();
        var invalidParts = new List<string>();

        foreach (var part in candidates)
        {
            var candidate = NormalizeSource(CleanCandidate(part));
            if (IsValidSource(candidate, allowSpaces: unwrapped is not null || part.Any(char.IsWhiteSpace)))
            {
                if (media.Count < remainingReferences)
                    media.Add(CreateLegacyMedia(candidate));
            }
            else if (!part.Any(char.IsWhiteSpace) || !HasTraversalOrUnsupportedHomePrefix(candidate))
            {
                invalidParts.Add(part);
            }
        }

        var payloadValue = payload.Trim();
        var looksLocal = LooksLikeLocalPath(payloadValue)
            || rawPayload.TrimStart().StartsWith("file://", StringComparison.OrdinalIgnoreCase);

        if (media.Count == 0 && payloadValue.Any(char.IsWhiteSpace))
        {
            var fallback = NormalizeSource(CleanCandidate(payloadValue));
            if (IsValidSource(fallback, allowSpaces: true, allowBareFileName: true))
            {
                if (remainingReferences > 0)
                    media.Add(CreateLegacyMedia(fallback));
                invalidParts.Clear();
            }
        }

        if (media.Count == 0)
        {
            var fallback = NormalizeSource(CleanCandidate(payloadValue));
            if (IsValidSource(fallback, allowSpaces: true, allowBareFileName: true))
            {
                if (remainingReferences > 0)
                    media.Add(CreateLegacyMedia(fallback));
                invalidParts.Clear();
            }
        }

        if (media.Count > 0)
            return new(media, CleanLineText(string.Join(' ', invalidParts)), StripLine: true);

        if (looksLocal)
        {
            if (remainingReferences > 0)
            {
                media.Add(new ChatMediaContentInfo
                {
                    Kind = ChatMediaContentKind.Unknown,
                    Source = ChatMediaContentSource.Unavailable,
                });
            }
            return new(media, string.Empty, StripLine: true);
        }

        return new(media, string.Empty, StripLine: false);
    }

    private static IReadOnlyList<string> SplitUnquotedParts(string payload)
    {
        var matches = Regex.Matches(payload, @"\S+");
        var parts = new List<string>(matches.Count);
        var previousEnd = 0;
        foreach (Match match in matches)
        {
            var candidate = NormalizeSource(CleanCandidate(match.Value));
            var previous = parts.Count > 0 ? parts[^1] : null;
            var previousCandidate = previous is null
                ? string.Empty
                : NormalizeSource(CleanCandidate(previous));
            if (previous is not null
                && BeginsRootedSource(previousCandidate)
                && !BeginsIndependentSource(candidate)
                && (!s_hasFileExtension.IsMatch(previousCandidate) || !IsValidSource(candidate)))
            {
                parts[^1] = $"{previous}{payload[previousEnd..match.Index]}{match.Value}";
            }
            else
            {
                parts.Add(match.Value);
            }

            previousEnd = match.Index + match.Length;
        }

        return parts;
    }

    private static string? UnwrapQuoted(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != trimmed[^1] || trimmed[0] is not ('"' or '\'' or '`'))
            return null;
        return trimmed[1..^1].Trim();
    }

    private static string NormalizeSource(string source) =>
        source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? source["file://".Length..]
            : source;

    private static string CleanCandidate(string raw)
    {
        var stripped = raw.TrimStart('`', '"', '\'', '[', '{', '(')
            .TrimEnd('`', '"', '\'', '\\', '}', ')', ']', ',');
        var quoteAfterExtension = Regex.Match(
            stripped,
            @"^(.*\.\w{1,10})\\?""(?=[\]},:]|$).*",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return quoteAfterExtension.Success ? quoteAfterExtension.Groups[1].Value : stripped;
    }

    private static bool IsValidSource(
        string candidate,
        bool allowSpaces = false,
        bool allowBareFileName = false)
    {
        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > MaxSourceLength
            || (!allowSpaces && candidate.Any(char.IsWhiteSpace)))
        {
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(uri.UserInfo) && IsAllowedRemoteHost(uri.Host);
        }

        if (IsLikelyLocalPath(candidate))
            return true;

        return allowBareFileName
            && !s_scheme.IsMatch(candidate)
            && s_hasFileExtension.IsMatch(candidate)
            && !HasTraversalOrUnsupportedHomePrefix(candidate);
    }

    private static bool IsAllowedRemoteHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)
            || !normalized.Contains('.')
            || normalized is "localhost" or "localhost.localdomain" or "metadata.google.internal"
            || normalized.EndsWith(".localhost", StringComparison.Ordinal)
            || normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal))
        {
            return false;
        }

        if (!IPAddress.TryParse(normalized, out var address))
            return true;

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !(bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] >= 224);
    }

    private static bool IsLikelyLocalPath(string candidate) =>
        !HasTraversalOrUnsupportedHomePrefix(candidate)
        && (candidate.StartsWith('/')
            || candidate.StartsWith("./", StringComparison.Ordinal)
            || candidate.StartsWith("~/", StringComparison.Ordinal)
            || candidate.StartsWith("~\\", StringComparison.Ordinal)
            || s_windowsDrive.IsMatch(candidate)
            || candidate.StartsWith(@"\\", StringComparison.Ordinal)
            || (!s_scheme.IsMatch(candidate)
                && (candidate.Contains('/') || candidate.Contains('\\'))));

    private static bool LooksLikeLocalPath(string candidate) =>
        candidate.StartsWith('/')
        || candidate.StartsWith("./", StringComparison.Ordinal)
        || candidate.StartsWith("../", StringComparison.Ordinal)
        || candidate.StartsWith('~')
        || s_windowsDrive.IsMatch(candidate)
        || candidate.StartsWith(@"\\", StringComparison.Ordinal)
        || (!s_scheme.IsMatch(candidate)
            && (candidate.Contains('/') || candidate.Contains('\\')));

    private static bool HasTraversalOrUnsupportedHomePrefix(string candidate) =>
        candidate.StartsWith("../", StringComparison.Ordinal)
        || candidate == ".."
        || (candidate.StartsWith('~')
            && !candidate.StartsWith("~/", StringComparison.Ordinal)
            && !candidate.StartsWith("~\\", StringComparison.Ordinal))
        || s_traversalSegment.IsMatch(candidate);

    private static bool BeginsRootedSource(string candidate) =>
        candidate.StartsWith('/')
        || candidate.StartsWith('~')
        || candidate.StartsWith("./", StringComparison.Ordinal)
        || candidate.StartsWith("../", StringComparison.Ordinal)
        || s_windowsDrive.IsMatch(candidate)
        || candidate.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool BeginsIndependentSource(string candidate) =>
        BeginsRootedSource(candidate) || s_scheme.IsMatch(candidate);

    private static ChatMediaContentInfo CreateLegacyMedia(string source)
    {
        var fileName = SafeFileName(source);
        return new ChatMediaContentInfo
        {
            Kind = ClassifyByFileName(fileName),
            Source = ChatMediaContentSource.LegacyDirective,
            FileName = fileName,
            GatewaySource = source,
        };
    }

    internal static string? SafeFileName(string source)
    {
        var path = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        path = path.Replace('\\', '/');
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            leaf = Uri.UnescapeDataString(leaf);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(leaf.Length, MaxFileNameLength));
        foreach (var character in leaf)
        {
            if (!char.IsControl(character) && character is not '\r' and not '\n')
                builder.Append(character);
            if (builder.Length == MaxFileNameLength)
                break;
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static ChatMediaContentKind ClassifyByFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif"
                => ChatMediaContentKind.Image,
            ".mp3" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".flac"
                => ChatMediaContentKind.Audio,
            ".mp4" or ".mov" or ".m4v" or ".webm" or ".avi"
                => ChatMediaContentKind.Video,
            _ => ChatMediaContentKind.File,
        };
    }

    private static string CleanLineText(string text) =>
        Regex.Replace(text, @"[ \t]{2,}", " ").Trim();

    private sealed record PayloadProjection(
        IReadOnlyList<ChatMediaContentInfo> Media,
        string ResidualText,
        bool StripLine);
}
