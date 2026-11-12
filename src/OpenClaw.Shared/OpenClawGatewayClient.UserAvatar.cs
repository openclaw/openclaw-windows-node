using System.Net;

namespace OpenClaw.Shared;

/// <summary>An authenticated user's avatar image fetched from the Gateway-owned avatar route.</summary>
public sealed record UserAvatarImage(byte[] Bytes, string MimeType);

public enum UserAvatarStatus
{
    Ready,
    /// <summary>The Gateway has no image for this profile (404). Not an error.</summary>
    NotFound,
    /// <summary>The Gateway rejected the HTTP credential (401/403).</summary>
    Unauthorized,
    /// <summary>The client or Gateway cannot serve avatars.</summary>
    Unsupported,
    /// <summary>Transport, status, size, or content failure.</summary>
    Failed,
    /// <summary>The request was cancelled or the connection changed; callers must not surface this.</summary>
    Cancelled,
}

public sealed record UserAvatarResult(UserAvatarStatus Status, UserAvatarImage? Image = null)
{
    public static UserAvatarResult Of(UserAvatarStatus status) => new(status);
}

public partial class OpenClawGatewayClient
{
    internal const int MaximumUserAvatarBytes = 1_000_000;
    internal const int MaximumUserAvatarIdentifierLength = 512;
    internal const string UserAvatarTimeoutEnvironmentVariable = "OPENCLAW_USER_AVATAR_TIMEOUT_MS";
    internal const int DefaultUserAvatarTimeoutMs = 15_000;
    private const string UserAvatarPathPrefix = "/api/users/";
    private const string UserAvatarPathSuffix = "/avatar";

    private static readonly HashSet<string> s_userAvatarMimeTypes = new(StringComparer.Ordinal)
    {
        "image/gif", "image/jpeg", "image/png", "image/webp",
    };

    /// <summary>Deadline for the whole avatar request, response headers and body together.</summary>
    internal TimeSpan UserAvatarTimeout { get; set; } = ResolveUserAvatarTimeout();

    private static TimeSpan ResolveUserAvatarTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable(UserAvatarTimeoutEnvironmentVariable), out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(DefaultUserAvatarTimeoutMs);

    /// <summary>
    /// Uri.EscapeDataString leaves "." and ".." intact, which would collapse the users/{id}/avatar route.
    /// Identifiers are also capped far above any real profile id or revision so a malformed value cannot
    /// build an unbounded request path. Both are rejected before a request is built.
    /// </summary>
    private static bool IsRoutableAvatarProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId)
        && profileId.Length <= MaximumUserAvatarIdentifierLength
        && profileId is not ("." or "..");

    private static bool IsBoundedAvatarRevision(string? revision) =>
        revision is null || revision.Length <= MaximumUserAvatarIdentifierLength;

    /// <summary>
    /// Fetches the authenticated user's avatar from the Gateway's own route using the connection's
    /// existing HTTP credential. The Gateway resolves uploads and any email-backed or host-account
    /// image itself; the client never contacts a third-party image provider.
    /// </summary>
    public async Task<UserAvatarResult> ResolveUserAvatarAsync(
        string profileId,
        string? revision,
        CancellationToken cancellationToken = default)
    {
        if (!IsRoutableAvatarProfileId(profileId) || !IsBoundedAvatarRevision(revision))
            return UserAvatarResult.Of(UserAvatarStatus.Failed);
        if (!TryCaptureMediaLease(out var lease))
            return UserAvatarResult.Of(UserAvatarStatus.Cancelled);

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, CancellationToken);
            // A timeout cancels only this linked source, so it stays distinguishable from caller/connection cancellation.
            linkedCancellation.CancelAfter(UserAvatarTimeout);
            var basePath = lease.HttpBaseUri.AbsolutePath.TrimEnd('/');
            var builder = new UriBuilder(lease.HttpBaseUri)
            {
                Path = $"{basePath}{UserAvatarPathPrefix}{Uri.EscapeDataString(profileId)}{UserAvatarPathSuffix}",
                Query = string.IsNullOrWhiteSpace(revision) ? string.Empty : "v=" + Uri.EscapeDataString(revision),
            };
            using var request = CreateAuthenticatedMediaRequest(builder.Uri, "image/*");
            using var response = await _assistantMediaHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UserAvatarResult.Of(UserAvatarStatus.NotFound);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return UserAvatarResult.Of(UserAvatarStatus.Unauthorized);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > MaximumUserAvatarBytes)
                return UserAvatarResult.Of(UserAvatarStatus.Failed);

            var mimeType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mimeType is null || !s_userAvatarMimeTypes.Contains(mimeType))
                return UserAvatarResult.Of(UserAvatarStatus.Failed);

            var bytes = await ReadBoundedAsync(
                response.Content, MaximumUserAvatarBytes, linkedCancellation.Token).ConfigureAwait(false);
            if (!IsCurrentMediaLease(lease))
                return UserAvatarResult.Of(UserAvatarStatus.Cancelled);
            return bytes is { Length: > 0 }
                ? new UserAvatarResult(UserAvatarStatus.Ready, new UserAvatarImage(bytes, mimeType))
                : UserAvatarResult.Of(UserAvatarStatus.Failed);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || CancellationToken.IsCancellationRequested)
        {
            return UserAvatarResult.Of(UserAvatarStatus.Cancelled);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or ObjectDisposedException
                or OperationCanceledException or TimeoutException)
        {
            // An HttpClient timeout surfaces as TaskCanceledException without either token cancelled.
            _logger.Warn($"User avatar request failed ({ex.GetType().Name}).");
            return UserAvatarResult.Of(UserAvatarStatus.Failed);
        }
    }
}
