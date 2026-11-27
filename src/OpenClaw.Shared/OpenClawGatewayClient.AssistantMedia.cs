using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    internal const int MaximumAssistantImageBytes = 12 * 1024 * 1024;
    internal const int MaximumAssistantPlaybackBytes = 16 * 1024 * 1024;
    internal const int MaximumAssistantFilePreviewBytes = 1024 * 1024;
    private const int AssistantFilePreviewTimeoutMs = 20000;
    private const int MaximumAssistantMediaMetadataBytes = 64 * 1024;
    private const int Base64CancellationCheckInterval = 4096;
    private const string ManagedMediaPathPrefix = "/api/chat/media/outgoing/";
    private const string InlineDataUrlScheme = "data:";
    private const string DefaultInlineImageMimeType = "image/png";
    private const int MaximumInlineImageDataUrlHeaderChars = 256;
    private const int MaximumInlineImageDataUrlChars =
        MaximumInlineImageDataUrlHeaderChars + (MaximumAssistantImageBytes + 2) / 3 * 4;

    // Raster formats the Gateway embeds inline; SVG is excluded because it is active content.
    private static readonly HashSet<string> InlineImageMimeTypes = new(StringComparer.Ordinal)
    {
        "image/apng",
        "image/avif",
        "image/bmp",
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    private readonly Guid _mediaClientId = Guid.NewGuid();
    private readonly HttpClient _assistantMediaHttpClient;

    public async Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)
            || media.Kind == ChatMediaContentKind.Unknown
            || media.Kind == ChatMediaContentKind.File && media.Source != ChatMediaContentSource.Structured)
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        // Inline images already arrived with the message; decode them locally without a connection.
        var inlineDataUrl = media.InlineDataUrl;
        if (media.Kind == ChatMediaContentKind.Image && !string.IsNullOrEmpty(inlineDataUrl))
        {
            using var inlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                CancellationToken);
            try
            {
                return await Task.Run(
                    () => ResolveInlineImage(inlineDataUrl, inlineCancellation.Token),
                    inlineCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || CancellationToken.IsCancellationRequested)
            {
                return AssistantMediaResolutionResult.Unavailable;
            }
        }

        if (!TryCaptureMediaLease(out var lease))
            return AssistantMediaResolutionResult.Unavailable;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            CancellationToken);
        if (media.Kind == ChatMediaContentKind.File)
            linkedCancellation.CancelAfter(AssistantFilePreviewTimeoutMs);
        try
        {
            var result = media.Source switch
            {
                ChatMediaContentSource.Structured =>
                    await ResolveStructuredMediaAsync(
                        lease,
                        sessionKey,
                        media,
                        linkedCancellation.Token).ConfigureAwait(false),
                ChatMediaContentSource.LegacyDirective =>
                    await ResolveLegacyMediaAsync(
                        lease,
                        media,
                        linkedCancellation.Token).ConfigureAwait(false),
                _ => AssistantMediaResolutionResult.Unavailable,
            };
            return IsCurrentMediaLease(lease)
                ? result
                : AssistantMediaResolutionResult.Unavailable;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || CancellationToken.IsCancellationRequested)
        {
            return AssistantMediaResolutionResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Assistant media resolution failed ({ex.GetType().Name}).");
            return AssistantMediaResolutionResult.Unavailable;
        }
    }

    /// <summary>
    /// Resolves a structured file attachment (for example a zip the agent produced) to
    /// the Gateway's short-lived ticketed download URL. The ticket authorizes only this
    /// attachment. Only the connection-owned chat bridge may call this: it launches the
    /// browser itself and hands renderers an <see cref="AssistantFileDownloadStatus"/>,
    /// so the ticket never reaches presentation code. Returns null when unavailable.
    /// </summary>
    internal async Task<Uri?> ResolveAssistantFileDownloadUriAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)
            || media.Kind != ChatMediaContentKind.File
            || media.Source != ChatMediaContentSource.Structured
            || string.IsNullOrWhiteSpace(media.ArtifactId)
            || !TryCaptureMediaLease(out var lease))
        {
            return null;
        }

        try
        {
            var payload = await RequestArtifactDownloadAsync(sessionKey, media)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return IsCurrentMediaLease(lease)
                && TryReadArtifactMetadata(payload, media.Kind, out _, out _)
                && payload.TryGetProperty("url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                && TryResolveManagedMediaUri(lease.HttpBaseUri, urlElement.GetString(), out var downloadUri)
                    ? downloadUri
                    : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Assistant file download resolution failed ({ex.GetType().Name}).");
            return null;
        }
    }

    private Task<JsonElement> RequestArtifactDownloadAsync(
        string sessionKey,
        ChatMediaContentInfo media)
    {
        var parameters = string.IsNullOrWhiteSpace(media.AgentId)
            ? new { sessionKey, artifactId = media.ArtifactId }
            : (object)new { sessionKey, artifactId = media.ArtifactId, agentId = media.AgentId };
        return SendWizardRequestAsync(
            "artifacts.download",
            parameters,
            timeoutMs: 20000);
    }

    private async Task<AssistantMediaResolutionResult> ResolveStructuredMediaAsync(
        GatewayConnectionLease lease,
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.ArtifactId))
            return AssistantMediaResolutionResult.Unavailable;

        var payload = await RequestArtifactDownloadAsync(sessionKey, media)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!IsCurrentMediaLease(lease)
            || !TryReadArtifactMetadata(payload, media.Kind, out var mimeType, out var sizeBytes))
        {
            return AssistantMediaResolutionResult.Unavailable;
        }
        if (media.Kind == ChatMediaContentKind.File &&
            (!payload.GetProperty("artifact").TryGetProperty("id", out var artifactId) ||
             artifactId.ValueKind != JsonValueKind.String || artifactId.GetString() != media.ArtifactId))
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        var maximumBytes = MaximumBytes(media.Kind);
        if (sizeBytes is > 0 && sizeBytes > maximumBytes)
            return AssistantMediaResolutionResult.Unavailable;

        if (payload.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.String)
        {
            if (!payload.TryGetProperty("encoding", out var encoding)
                || encoding.ValueKind != JsonValueKind.String
                || !string.Equals(encoding.GetString(), "base64", StringComparison.Ordinal))
            {
                return AssistantMediaResolutionResult.Unavailable;
            }

            if (media.Kind == ChatMediaContentKind.File && dataElement.GetString() == string.Empty)
                return new(AssistantMediaResolutionStatus.Ready, [], mimeType);
            return TryDecodeBoundedBase64(dataElement.GetString(), maximumBytes, out var bytes)
                ? new AssistantMediaResolutionResult(
                    AssistantMediaResolutionStatus.Ready,
                    bytes,
                    mimeType)
                : AssistantMediaResolutionResult.Unavailable;
        }

        if (!payload.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || !TryResolveManagedMediaUri(lease.HttpBaseUri, urlElement.GetString(), out var mediaUri))
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        return await DownloadMediaBytesAsync(
            lease,
            mediaUri,
            media.Kind,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssistantMediaResolutionResult> ResolveLegacyMediaAsync(
        GatewayConnectionLease lease,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.GatewaySource)
            || !AssistantMediaDirectiveParser.IsSendableLegacySource(media.GatewaySource))
            return AssistantMediaResolutionResult.Unavailable;

        var metadataUri = BuildLegacyMediaUri(
            lease.HttpBaseUri,
            media.GatewaySource,
            mediaTicket: null,
            metadata: true,
            playback: false);
        using var metadataRequest = CreateAuthenticatedMediaRequest(metadataUri, "application/json");
        using var metadataResponse = await _assistantMediaHttpClient.SendAsync(
            metadataRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!metadataResponse.IsSuccessStatusCode)
            return AssistantMediaResolutionResult.Unavailable;

        var metadataBytes = await ReadBoundedAsync(
            metadataResponse.Content,
            MaximumAssistantMediaMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (metadataBytes is null || !IsCurrentMediaLease(lease))
            return AssistantMediaResolutionResult.Unavailable;

        using var metadataDocument = JsonDocument.Parse(metadataBytes);
        var root = metadataDocument.RootElement;
        if (!root.TryGetProperty("available", out var available)
            || available.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("mediaTicket", out var ticketElement)
            || ticketElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(ticketElement.GetString()))
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        var maximumBytes = MaximumBytes(media.Kind);
        if (root.TryGetProperty("sizeBytes", out var declaredSize)
            && declaredSize.TryGetInt64(out var sizeBytes)
            && sizeBytes > maximumBytes)
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        var playback = root.TryGetProperty("playback", out var playbackElement)
            && playbackElement.ValueKind == JsonValueKind.String
            && string.Equals(playbackElement.GetString(), "transcode", StringComparison.Ordinal);
        var bytesUri = BuildLegacyMediaUri(
            lease.HttpBaseUri,
            media.GatewaySource,
            ticketElement.GetString(),
            metadata: false,
            playback);
        return await DownloadMediaBytesAsync(
            lease,
            bytesUri,
            media.Kind,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssistantMediaResolutionResult> DownloadMediaBytesAsync(
        GatewayConnectionLease lease,
        Uri uri,
        ChatMediaContentKind kind,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedMediaRequest(uri, kind == ChatMediaContentKind.File ? "*/*" : $"{MimePrefix(kind)}/*");
        using var response = await _assistantMediaHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return AssistantMediaResolutionResult.Preparing;
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength > maximumBytes)
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (!MatchesKind(mimeType, kind))
            return AssistantMediaResolutionResult.Unavailable;

        var bytes = await ReadBoundedAsync(
            response.Content,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        return bytes is not null && IsCurrentMediaLease(lease)
            ? new AssistantMediaResolutionResult(
                AssistantMediaResolutionStatus.Ready,
                bytes,
                mimeType)
            : AssistantMediaResolutionResult.Unavailable;
    }

    private HttpRequestMessage CreateAuthenticatedMediaRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        var authToken = Volatile.Read(ref _assistantMediaAuthToken);
        if (!string.IsNullOrWhiteSpace(authToken))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                authToken);
        return request;
    }

    private bool TryCaptureMediaLease(out GatewayConnectionLease lease)
    {
        lease = default;
        if (!IsConnectedToGateway || !TryBuildMediaHttpBaseUri(_currentGatewayUrl, out var baseUri))
            return false;
        lease = new GatewayConnectionLease(_mediaClientId, ConnectionGeneration, baseUri);
        return true;
    }

    private bool IsCurrentMediaLease(GatewayConnectionLease lease) =>
        lease.ClientId == _mediaClientId
        && lease.Generation == ConnectionGeneration
        && IsConnectedToGateway;

    internal static bool TryBuildMediaHttpBaseUri(string gatewayUrl, out Uri baseUri)
    {
        baseUri = null!;
        var normalized = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var gatewayUri))
            return false;
        var builder = new UriBuilder(gatewayUri)
        {
            Scheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp,
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        baseUri = builder.Uri;
        return true;
    }

    internal static bool TryResolveManagedMediaUri(
        Uri baseUri,
        string? relativePath,
        out Uri mediaUri)
    {
        mediaUri = null!;
        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.StartsWith(ManagedMediaPathPrefix, StringComparison.Ordinal)
            || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains('\\')
            || HasTraversal(relativePath.Split('?', 2)[0]))
        {
            return false;
        }

        if (!Uri.TryCreate(relativePath, UriKind.Relative, out var relative)
            || !Uri.TryCreate(new Uri(baseUri.GetLeftPart(UriPartial.Authority)), relative, out var candidate)
            || !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != baseUri.Port
            || !string.IsNullOrEmpty(candidate.Fragment)
            || HasTraversal(candidate.AbsolutePath)
            || !HasNonEmptyQueryValue(candidate.Query, "mediaTicket"))
        {
            return false;
        }

        mediaUri = candidate;
        return true;
    }

    private static Uri BuildLegacyMediaUri(
        Uri baseUri,
        string source,
        string? mediaTicket,
        bool metadata,
        bool playback)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var route = $"{basePath}/__openclaw__/assistant-media";
        var query = new StringBuilder()
            .Append("source=")
            .Append(Uri.EscapeDataString(source));
        if (metadata)
            query.Append("&meta=1");
        if (!string.IsNullOrWhiteSpace(mediaTicket))
            query.Append("&mediaTicket=").Append(Uri.EscapeDataString(mediaTicket));
        if (playback)
            query.Append("&playback=1");
        return new UriBuilder(baseUri) { Path = route, Query = query.ToString() }.Uri;
    }

    private static bool TryReadArtifactMetadata(
        JsonElement payload,
        ChatMediaContentKind expectedKind,
        out string mimeType,
        out long? sizeBytes)
    {
        mimeType = string.Empty;
        sizeBytes = null;
        if (!payload.TryGetProperty("artifact", out var artifact)
            || artifact.ValueKind != JsonValueKind.Object
            || !artifact.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                typeElement.GetString(),
                expectedKind.ToString(),
                StringComparison.OrdinalIgnoreCase)
            || !artifact.TryGetProperty("mimeType", out var mimeElement)
            || mimeElement.ValueKind != JsonValueKind.String
            || !MatchesKind(mimeElement.GetString(), expectedKind))
        {
            return false;
        }

        mimeType = mimeElement.GetString()!.Trim().ToLowerInvariant();
        if (artifact.TryGetProperty("sizeBytes", out var sizeElement)
            && sizeElement.TryGetInt64(out var declaredSize))
        {
            sizeBytes = declaredSize;
        }
        return true;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                return null;
            output.Write(buffer, 0, read);
        }
    }

    internal static bool TryDecodeBoundedBase64(string? encoded, int maximumBytes, out byte[] bytes)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        return TryDecodeBoundedBase64(encoded.AsSpan(), maximumBytes, CancellationToken.None, out bytes);
    }

    private static bool TryDecodeBoundedBase64(
        ReadOnlySpan<char> encoded,
        int maximumBytes,
        CancellationToken cancellationToken,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (encoded.IsEmpty || maximumBytes <= 0)
            return false;

        var maximumEncodedLength = ((maximumBytes + 2L) / 3L) * 4L;
        var compactLength = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            if (cancellationToken.CanBeCanceled && index % Base64CancellationCheckInterval == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var character = encoded[index];
            if (char.IsWhiteSpace(character))
                continue;
            compactLength++;
            if (compactLength > maximumEncodedLength)
                return false;
        }

        if (compactLength == 0 || compactLength % 4 == 1)
            return false;

        var paddedLength = compactLength + (4 - compactLength % 4) % 4;
        if (paddedLength > maximumEncodedLength)
            return false;

        var normalized = GC.AllocateUninitializedArray<char>(paddedLength);
        var normalizedLength = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            if (cancellationToken.CanBeCanceled && index % Base64CancellationCheckInterval == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var character = encoded[index];
            if (char.IsWhiteSpace(character))
                continue;
            normalized[normalizedLength++] = character switch
            {
                '-' => '+',
                '_' => '/',
                _ => character,
            };
        }
        normalized.AsSpan(normalizedLength).Fill('=');

        var padding = normalized[^1] == '='
            ? normalized.Length > 1 && normalized[^2] == '=' ? 2 : 1
            : 0;
        var decodedLength = paddedLength / 4 * 3 - padding;
        if (decodedLength > maximumBytes)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = GC.AllocateUninitializedArray<byte>(decodedLength);
        if (!Convert.TryFromBase64Chars(normalized.AsSpan(), decoded, out var bytesWritten)
            || bytesWritten != decodedLength)
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    /// <summary>
    /// Validates the header of an inline <c>data:image/...;base64,</c> URL: an allowlisted
    /// raster MIME type, the base64 flag, and the overall size bound. The payload itself is
    /// validated when <see cref="ResolveInlineImage"/> decodes it.
    /// </summary>
    internal static bool TryParseInlineImageDataUrl(
        string? dataUrl,
        out string mimeType,
        out int payloadStart)
    {
        mimeType = string.Empty;
        payloadStart = 0;
        if (string.IsNullOrEmpty(dataUrl)
            || dataUrl.Length > MaximumInlineImageDataUrlChars
            || !dataUrl.StartsWith(InlineDataUrlScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var headerStart = InlineDataUrlScheme.Length;
        var comma = dataUrl.IndexOf(
            ',',
            headerStart,
            Math.Min(dataUrl.Length - headerStart, MaximumInlineImageDataUrlHeaderChars));
        if (comma < 0 || comma == dataUrl.Length - 1)
            return false;

        var header = dataUrl[headerStart..comma].Split(';', StringSplitOptions.TrimEntries);
        var declaredMimeType = header[0].ToLowerInvariant();
        if (!InlineImageMimeTypes.Contains(declaredMimeType)
            || !header.Skip(1).Any(static option => option.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        mimeType = declaredMimeType;
        payloadStart = comma + 1;
        return true;
    }

    private static AssistantMediaResolutionResult ResolveInlineImage(
        string dataUrl,
        CancellationToken cancellationToken)
    {
        if (!TryParseInlineImageDataUrl(dataUrl, out var mimeType, out var payloadStart))
            return AssistantMediaResolutionResult.Unavailable;

        cancellationToken.ThrowIfCancellationRequested();
        return TryDecodeBoundedBase64(
            dataUrl.AsSpan(payloadStart),
            MaximumAssistantImageBytes,
            cancellationToken,
            out var bytes)
                ? new AssistantMediaResolutionResult(AssistantMediaResolutionStatus.Ready, bytes, mimeType)
                : AssistantMediaResolutionResult.Unavailable;
    }

    private static int MaximumBytes(ChatMediaContentKind kind) => kind switch
    {
        ChatMediaContentKind.Image => MaximumAssistantImageBytes,
        ChatMediaContentKind.File => MaximumAssistantFilePreviewBytes,
        _ => MaximumAssistantPlaybackBytes,
    };

    private static string MimePrefix(ChatMediaContentKind kind) => kind switch
    {
        ChatMediaContentKind.Image => "image",
        ChatMediaContentKind.Audio => "audio",
        ChatMediaContentKind.Video => "video",
        _ => "application",
    };

    // Files are downloaded rather than rendered, so any declared MIME type is acceptable.
    private static bool MatchesKind(string? mimeType, ChatMediaContentKind kind) =>
        !string.IsNullOrWhiteSpace(mimeType)
        && (kind == ChatMediaContentKind.File
            || mimeType.StartsWith($"{MimePrefix(kind)}/", StringComparison.OrdinalIgnoreCase));

    private static bool HasTraversal(string absolutePath)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(absolutePath);
        }
        catch (UriFormatException)
        {
            return true;
        }
        return decoded.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }

    private static bool HasNonEmptyQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;
            if (string.Equals(
                    Uri.UnescapeDataString(pair[..separator]),
                    key,
                    StringComparison.Ordinal)
                && separator + 1 < pair.Length)
            {
                return true;
            }

        }
        return false;
    }

}
