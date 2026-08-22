using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    internal const int MaximumAssistantImageBytes = 12 * 1024 * 1024;
    internal const int MaximumAssistantPlaybackBytes = 16 * 1024 * 1024;
    private const int MaximumAssistantMediaMetadataBytes = 64 * 1024;
    private const string ManagedMediaPathPrefix = "/api/chat/media/outgoing/";
    private readonly Guid _mediaClientId = Guid.NewGuid();
    private readonly HttpClient _assistantMediaHttpClient;

    public async Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)
            || media.Kind is ChatMediaContentKind.File or ChatMediaContentKind.Unknown
            || !TryCaptureMediaLease(out var lease))
        {
            return AssistantMediaResolutionResult.Unavailable;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            CancellationToken);
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

    private async Task<AssistantMediaResolutionResult> ResolveStructuredMediaAsync(
        GatewayConnectionLease lease,
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.ArtifactId))
            return AssistantMediaResolutionResult.Unavailable;

        var parameters = string.IsNullOrWhiteSpace(media.AgentId)
            ? new { sessionKey, artifactId = media.ArtifactId }
            : (object)new { sessionKey, artifactId = media.ArtifactId, agentId = media.AgentId };
        var payload = await SendWizardRequestAsync(
                "artifacts.download",
                parameters,
                timeoutMs: 20000)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!IsCurrentMediaLease(lease)
            || !TryReadArtifactMetadata(payload, media.Kind, out var mimeType, out var sizeBytes))
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
        if (string.IsNullOrWhiteSpace(media.GatewaySource))
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
        using var request = CreateAuthenticatedMediaRequest(uri, $"{MimePrefix(kind)}/*");
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
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(encoded) || maximumBytes <= 0)
            return false;

        var maximumEncodedLength = ((maximumBytes + 2L) / 3L) * 4L;
        var compactLength = 0;
        foreach (var character in encoded)
        {
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

        var normalized = string.Create(paddedLength, encoded, static (destination, source) =>
        {
            var index = 0;
            foreach (var character in source)
            {
                if (char.IsWhiteSpace(character))
                    continue;
                destination[index++] = character switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => character,
                };
            }
            destination[index..].Fill('=');
        });

        var padding = normalized[^1] == '='
            ? normalized.Length > 1 && normalized[^2] == '=' ? 2 : 1
            : 0;
        var decodedLength = paddedLength / 4 * 3 - padding;
        if (decodedLength > maximumBytes)
            return false;

        var decoded = GC.AllocateUninitializedArray<byte>(decodedLength);
        if (!Convert.TryFromBase64Chars(normalized, decoded, out var bytesWritten)
            || bytesWritten != decodedLength)
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    private static int MaximumBytes(ChatMediaContentKind kind) =>
        kind == ChatMediaContentKind.Image
            ? MaximumAssistantImageBytes
            : MaximumAssistantPlaybackBytes;

    private static string MimePrefix(ChatMediaContentKind kind) => kind switch
    {
        ChatMediaContentKind.Image => "image",
        ChatMediaContentKind.Audio => "audio",
        ChatMediaContentKind.Video => "video",
        _ => "application",
    };

    private static bool MatchesKind(string? mimeType, ChatMediaContentKind kind) =>
        !string.IsNullOrWhiteSpace(mimeType)
        && mimeType.StartsWith($"{MimePrefix(kind)}/", StringComparison.OrdinalIgnoreCase);

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
