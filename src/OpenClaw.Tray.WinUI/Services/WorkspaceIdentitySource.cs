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
    private bool _forceAvatar;
    private (string ProfileId, string? Revision)? _avatarKey;
    private bool _unavailable;

    private const string GatewayOwnerProfileId = "gateway-owner";
    private CancellationTokenSource _avatarCancellation = new();

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
        _avatarCancellation.Cancel();
        _avatarCancellation = new CancellationTokenSource();
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
                // Explicit invalidation supersedes a stalled avatar and any older read. An ordinary refresh only
                // coalesces so repeated presence events cannot restart a valid in-flight attempt forever.
                _invalidateAgain = true;
                _forceAvatar = true;
                CancelAvatarAttempt();
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
    /// Derived from the exception's origin and its fixed-list protocol codes, never from message text.
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

    private async Task LoadAsync(IOperatorGatewayClient client, int generation)
    {
        var token = _avatarCancellation.Token;
        do
        {
            _refreshAgain = false;
            _invalidateAgain = false;
            try
            {
                var response = await client.SendWizardRequestAsync("users.self", new { }, timeoutMs: 12000);
                if (!IsCurrent()) return;
                if (_invalidateAgain) continue;
                var profile = ReadProfile(response);
                DisplayName = profile.DisplayName;
                HasProfile = true;
                _loading = false;
                var key = profile.AvatarProfileId is { } keyId ? (keyId, profile.AvatarRevision) : ((string, string?)?)null;
                var refetch = key is not null && (_forceAvatar || Avatar is null || key != _avatarKey);
                if (key is null || key != _avatarKey)
                {
                    Avatar = null;
                    _avatarKey = null;
                }
                _forceAvatar = false;
                changed();
                if (refetch)
                {
                    var avatar = await LoadAvatarAttemptAsync(client, key!.Value.Item1, key.Value.Item2, token);
                    if (!IsCurrent()) return;
                    if (_invalidateAgain) continue;
                    Avatar = avatar;
                    _avatarKey = avatar is null ? null : key;
                    changed();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                OperationCanceledException or JsonException)
            {
                if (!IsCurrent()) return;
                if (_refreshAgain) continue;
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
        } while (IsCurrent() && _refreshAgain);

        bool IsCurrent() => generation == _generation &&
            ReferenceEquals(client, _client) && client.IsConnectedToGateway;
    }

    private CancellationTokenSource? _avatarAttempt;

    private void CancelAvatarAttempt()
    {
        try { _avatarAttempt?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<UserAvatarImage?> LoadAvatarAttemptAsync(
        IOperatorGatewayClient client, string profileId, string? revision, CancellationToken connectionToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _avatarAttempt = attempt;
        try
        {
            return await LoadAvatarAsync(client, profileId, revision, attempt.Token);
        }
        finally
        {
            if (ReferenceEquals(_avatarAttempt, attempt)) _avatarAttempt = null;
        }
    }

    /// <summary>
    /// The picture is optional: any failure keeps the already-loaded name and leaves native initials.
    /// Cancellation and superseded connections are silent; real failures report only a category.
    /// </summary>
    private async Task<UserAvatarImage?> LoadAvatarAsync(
        IOperatorGatewayClient client, string profileId, string? revision, CancellationToken token)
    {
        try
        {
            var result = await client.ResolveUserAvatarAsync(profileId, revision, token);
            switch (result.Status)
            {
                case UserAvatarStatus.Ready:
                    return result.Image;
                case UserAvatarStatus.Unauthorized or UserAvatarStatus.Failed:
                    reportUnavailable("Avatar" + result.Status);
                    return null;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException
            or ObjectDisposedException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                reportUnavailable("Avatar" + ex.GetType().Name);
            return null;
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
