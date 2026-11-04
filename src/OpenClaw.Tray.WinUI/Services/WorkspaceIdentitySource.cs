using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// UI-thread-owned display identity for one operator connection. Never an authorization source.
/// </summary>
internal sealed class WorkspaceIdentitySource(Action changed, Action<string> reportUnavailable)
{
    private IOperatorGatewayClient? _client;
    private int _generation;
    private Task? _pending;
    private bool _refreshAgain;
    private bool _invalidateAgain;

    /// <summary>An explicit invalidation owes one avatar refetch even for an unchanged key; it stays owed until an attempt resolves.</summary>
    private bool _forceAvatar;

    /// <summary>
    /// Profile id and revision whose avatar outcome (an image, or settled "no image") is known for this
    /// connection. An ordinary refresh with the same key never refetches; an explicit invalidation, a
    /// changed key or a new connection does. A cancelled attempt is never recorded.
    /// </summary>
    private (string ProfileId, string? Revision)? _avatarKey;
    private bool _unavailable;

    private const string GatewayOwnerProfileId = "gateway-owner";
    internal const string UserSelfTimeoutEnvironmentVariable = "OPENCLAW_USER_SELF_TIMEOUT_MS";
    internal const int DefaultUserSelfTimeoutMs = 12_000;

    /// <summary>Lifetime of the current connection; every read and avatar attempt is linked to it.</summary>
    private CancellationTokenSource _connectionCancellation = new();

    /// <summary>The newest read-and-avatar attempt; an explicit invalidation cancels it so a stalled one cannot delay a refresh.</summary>
    private CancellationTokenSource? _attempt;

    public string? DisplayName { get; private set; }

    /// <summary>Gateway-served avatar for the authenticated user; scoped to the current connection only and never persisted.</summary>
    public UserAvatarImage? Avatar { get; private set; }

    public bool IsLoading => _loading;

    /// <summary>True only while the current connection's last users.self read returned a valid profile.</summary>
    public bool HasProfile { get; private set; }

    /// <summary>A valid profile with no display name or email: the owner has not set a name yet.</summary>
    public bool IsUnnamedProfile => HasProfile && DisplayName is null && !_loading;

    /// <summary>
    /// True from the moment a connected context starts reading its profile until the name
    /// outcome (name, blank profile, failure) is known; cleared before the terminal change
    /// notification. The avatar loads afterwards and never changes this: the name stays visible.
    /// </summary>
    private bool _loading;
    private bool _scopeReported;

    private static bool HasReadScope(IOperatorGatewayClient client) =>
        client.GrantedOperatorScopes.Any(scope => scope is
            "operator.admin" or "operator.read" or "operator.write" or
            "operator.sessions.read" or "operator.sessions.write");

    public bool SetConnection(IOperatorGatewayClient? client, ConnectionStatus status)
    {
        var connected = status == ConnectionStatus.Connected && client?.IsConnectedToGateway == true;
        var next = connected ? client : null;
        if (ReferenceEquals(next, _client))
            return false;

        _generation++;
        _connectionCancellation.Cancel();
        _connectionCancellation = new CancellationTokenSource();
        Avatar = null;
        _avatarKey = null;
        _forceAvatar = false;
        _invalidateAgain = false;
        _client = next;
        _pending = null;
        _refreshAgain = false;
        _unavailable = false;
        _scopeReported = false;
        DisplayName = null;
        HasProfile = false;
        _loading = next is not null;
        changed();
        return true;
    }

    public Task RefreshAsync(bool invalidate = false)
    {
        if (invalidate) _unavailable = false;
        if (_unavailable || _client is not { IsConnectedToGateway: true } client ||
            !HasReadScope(client))
        {
            if (!_unavailable && !_scopeReported && _client is { IsConnectedToGateway: true } gated &&
                !HasReadScope(gated))
            {
                _scopeReported = true;
                reportUnavailable(IdentityFailure.LocalScopeGate.ToString());
            }
            var hadProfile = HasProfile;
            HasProfile = false;
            if (_loading || hadProfile)
            {
                _loading = false;
                changed();
            }
            return Task.CompletedTask;
        }

        if (_pending is { IsCompleted: false })
        {
            _refreshAgain = true;
            if (invalidate)
            {
                // Explicit invalidation supersedes a stalled read or avatar. An ordinary refresh only
                // coalesces so repeated presence events cannot restart a valid in-flight attempt forever.
                _invalidateAgain = true;
                _forceAvatar = true;
                CancelAttempt();
            }
            return _pending;
        }

        if (invalidate) _forceAvatar = true;
        if (!_loading && DisplayName is null && !HasProfile)
        {
            _loading = true;
            HasProfile = false;
            changed();
        }
        return _pending = LoadAsync(client, _generation);
    }

    /// <summary>Sanitized log line for a reported category; names the failing source truthfully.</summary>
    internal static string DescribeUnavailable(string category) =>
        category.StartsWith("Avatar", StringComparison.Ordinal)
            ? $"[Workspace] owner avatar unavailable ({category}); the profile name is kept."
            : $"[Workspace] users.self unavailable ({category}); showing unavailable text.";

    /// <summary>
    /// Why the Gateway user profile could not be read. Derived from the exception's origin and its
    /// fixed-list protocol codes, never from message text.
    /// </summary>
    internal enum IdentityFailure
    {
        /// <summary>Local pre-send refusal or lost connection; retried on the next lifecycle event.</summary>
        NotReady,
        Timeout,
        /// <summary>Server reported a retryable UNAVAILABLE (profile not resolved yet).</summary>
        Unavailable,
        /// <summary>FORBIDDEN reply. Cause is not proven by the code alone (profile binding, scope, or policy).</summary>
        Forbidden,
        /// <summary>INVALID_REQUEST reply. Cause is not proven by the code alone (unknown method or validation).</summary>
        InvalidRequest,
        /// <summary>Another server error code, or a code outside the fixed protocol list.</summary>
        Unknown,
        MalformedReply,
        /// <summary>Local gate: the connection reports no operator read-capable scope, so users.self was not sent.</summary>
        LocalScopeGate,
        /// <summary>FORBIDDEN carrying the MISSING_SCOPE detail: the server's method scope gate denied the request. Scope names are never captured.</summary>
        ServerMissingScope,
    }

    internal static IdentityFailure Classify(Exception ex)
    {
        switch (ex)
        {
            case JsonException: return IdentityFailure.MalformedReply;
            case TimeoutException: return IdentityFailure.Timeout;
            case OperationCanceledException: return IdentityFailure.NotReady;
        }
        if (!GatewayRequestErrors.TryGetCodes(ex, out var code, out var detail))
            return IdentityFailure.NotReady;
        if (detail == "AUTHENTICATED_PROFILE_UNAVAILABLE") return IdentityFailure.Unavailable;
        if (code == "FORBIDDEN" && detail == "MISSING_SCOPE") return IdentityFailure.ServerMissingScope;
        return code switch
        {
            "UNAVAILABLE" => IdentityFailure.Unavailable,
            "FORBIDDEN" => IdentityFailure.Forbidden,
            "INVALID_REQUEST" => IdentityFailure.InvalidRequest,
            _ => IdentityFailure.Unknown,
        };
    }

    /// <summary>Terminal outcomes stay suppressed until a supported invalidation; transient ones retry on the next event.</summary>
    internal static bool IsTerminal(IdentityFailure failure) => failure is
        IdentityFailure.Forbidden or IdentityFailure.InvalidRequest or
        IdentityFailure.Unknown or IdentityFailure.MalformedReply or IdentityFailure.ServerMissingScope;

    /// <summary>Nonfatal failures while writing or decoding the avatar bytes into a bitmap.</summary>
    internal static bool IsAvatarDecodeFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException
            or ObjectDisposedException or InvalidOperationException or NotSupportedException;

    /// <summary>Deadline for one users.self read; a positive OPENCLAW_USER_SELF_TIMEOUT_MS overrides the default.</summary>
    internal static int UserSelfTimeoutMs() =>
        int.TryParse(Environment.GetEnvironmentVariable(UserSelfTimeoutEnvironmentVariable), out var ms) && ms > 0
            ? ms
            : DefaultUserSelfTimeoutMs;

    private async Task LoadAsync(IOperatorGatewayClient client, int generation)
    {
        var connectionToken = _connectionCancellation.Token;
        do
        {
            _refreshAgain = false;
            _invalidateAgain = false;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            _attempt = attempt;
            try
            {
                var response = await ReadSelfAsync(client, attempt.Token);
                if (!IsCurrent()) return;
                if (_invalidateAgain) continue;
                var profile = ReadProfile(response);
                DisplayName = profile.DisplayName;
                HasProfile = true;
                _loading = false;
                var key = profile.AvatarProfileId is { } keyId ? (keyId, profile.AvatarRevision) : ((string, string?)?)null;
                var refetch = key is not null && (_forceAvatar || key != _avatarKey);
                if (key is null || key != _avatarKey)
                {
                    Avatar = null;
                    _avatarKey = null;
                }
                if (!refetch) _forceAvatar = false;
                changed();
                if (refetch)
                {
                    var avatar = await LoadAvatarAsync(client, key!.Value.Item1, key.Value.Item2, attempt.Token);
                    if (!IsCurrent()) return;
                    if (_invalidateAgain) continue;
                    if (avatar.Resolved)
                    {
                        _forceAvatar = false;
                        Avatar = avatar.Image;
                        _avatarKey = key;
                        changed();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                OperationCanceledException or JsonException)
            {
                if (!IsCurrent()) return;
                // Only an explicit invalidation retries here. A queued ordinary refresh must not postpone
                // the outcome, or repeated presence events would hold the profile in Loading indefinitely.
                if (_invalidateAgain) continue;
                DisplayName = null;
                HasProfile = false;
                Avatar = null;
                _avatarKey = null;
                var failure = Classify(ex);
                _unavailable = IsTerminal(failure);
                // Bounded category only: never the Gateway message, profile, name, email or token.
                reportUnavailable(failure.ToString());
                _loading = false;
                changed();
                return;
            }
            finally
            {
                if (ReferenceEquals(_attempt, attempt)) _attempt = null;
            }
        } while (IsCurrent() && _refreshAgain);

        bool IsCurrent() => generation == _generation &&
            ReferenceEquals(client, _client) && client.IsConnectedToGateway;
    }

    private void CancelAttempt()
    {
        try { _attempt?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The request API takes no token, so a superseded read is abandoned rather than cancelled and left to
    /// the client's own request handling. Its eventual fault is observed so it cannot surface later.
    /// </summary>
    private static async Task<JsonElement> ReadSelfAsync(IOperatorGatewayClient client, CancellationToken token)
    {
        var read = client.SendWizardRequestAsync("users.self", new { }, timeoutMs: UserSelfTimeoutMs());
        try
        {
            return await read.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            _ = read.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Resolved is false when the attempt was cancelled or could not run, so a later refresh tries again.</summary>
    private readonly record struct AvatarOutcome(bool Resolved, UserAvatarImage? Image);

    /// <summary>
    /// The picture is optional: any failure keeps the already-loaded name and leaves native initials.
    /// Cancellation and superseded connections are silent; real failures report only a category.
    /// </summary>
    private async Task<AvatarOutcome> LoadAvatarAsync(
        IOperatorGatewayClient client, string profileId, string? revision, CancellationToken token)
    {
        try
        {
            var result = await client.ResolveUserAvatarAsync(profileId, revision, token);
            switch (result.Status)
            {
                case UserAvatarStatus.Ready:
                    return new AvatarOutcome(true, result.Image);
                case UserAvatarStatus.Unauthorized or UserAvatarStatus.Failed:
                    reportUnavailable("Avatar" + result.Status);
                    return new AvatarOutcome(true, null);
                case UserAvatarStatus.NotFound or UserAvatarStatus.Unsupported:
                    return new AvatarOutcome(true, null);
                default:
                    return new AvatarOutcome(false, null);
            }
        }
        // Optional enrichment behind an injectable client: no exception from it may fault the profile refresh.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (token.IsCancellationRequested)
                return new AvatarOutcome(false, null);
            reportUnavailable("Avatar" + ex.GetType().Name);
            return new AvatarOutcome(true, null);
        }
    }

    internal readonly record struct SelfProfile(string? DisplayName, string? AvatarProfileId, string? AvatarRevision);

    internal static string? ReadDisplayName(JsonElement response) => ReadProfile(response).DisplayName;

    /// <summary>Name is the trimmed non-blank displayName, else the first non-blank email. Avatar is requested only when the profile has an upload or an email the Gateway can resolve.</summary>
    internal static SelfProfile ReadProfile(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("profile", out var profile) ||
            profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("users.self did not return a profile.");

        string? displayName = null;
        if (profile.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(name.GetString()))
            displayName = name.GetString()!.Trim();

        string? firstEmail = null;
        var hasEmail = false;
        if (profile.TryGetProperty("emails", out var emails) && emails.ValueKind == JsonValueKind.Array)
        {
            foreach (var email in emails.EnumerateArray())
            {
                if (email.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(email.GetString()))
                    continue;
                hasEmail = true;
                firstEmail = email.GetString()!.Trim();
                break;
            }
        }

        var hasAvatar = profile.TryGetProperty("hasAvatar", out var avatar) && avatar.ValueKind == JsonValueKind.True;
        string? revision = null;
        if (profile.TryGetProperty("updatedAt", out var updated))
            revision = updated.ValueKind switch
            {
                JsonValueKind.String => updated.GetString(),
                JsonValueKind.Number => updated.GetRawText(),
                _ => null,
            };

        return new SelfProfile(
            displayName ?? firstEmail,
            hasAvatar || hasEmail || id.GetString() == GatewayOwnerProfileId ? id.GetString() : null,
            revision);
    }
}
