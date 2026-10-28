using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Whether a Gateway record is a locally created, setup-managed Gateway whose blank owner profile may be offered
/// the current Windows account's name and photo. Evidence is the exact managed-record marker, never IsLocal,
/// loopback, an SSH tunnel address, or the credential type.
/// </summary>
internal static class ProfileCompletionEligibility
{
    public static bool IsEligible(GatewayRecord? record) =>
        record is { SshTunnel: null } &&
        (!string.IsNullOrWhiteSpace(record.SetupManagedDistroName) ||
         !string.IsNullOrWhiteSpace(record.NativePackageFamilyName));
}

internal enum ProfileOffer
{
    /// <summary>Not connected, read scope missing, or the profile could not be read.</summary>
    Unavailable,
    /// <summary>The owner already has a name, email, or photo; nothing is suggested.</summary>
    NotNeeded,
    Offer,
}

internal enum ProfileStep
{
    NotAttempted,
    Saved,
    /// <summary>The Gateway already had a value when re-read, so it was left alone.</summary>
    ExistingValue,
    /// <summary>Gateway refused: write permission or ownership is missing.</summary>
    Denied,
    Failed,
    /// <summary>The Gateway, profile, or user context changed while saving; nothing further was written.</summary>
    ContextChanged,
    /// <summary>The Gateway connection dropped before this change was sent; nothing was written for this field.</summary>
    ConnectionLost,
    /// <summary>The request was sent but no reply confirmed it (reply timeout, lost connection, or cancellation); it may or may not have been applied.</summary>
    Uncertain,
}

internal sealed record ProfilePreparation(
    ProfileOffer Offer,
    string? ProfileId,
    bool CanWrite,
    bool OfferName,
    bool OfferPhoto,
    AccountIdentityResult? Account,
    string? GatewayId = null,
    IOperatorGatewayClient? Client = null);

internal sealed record ProfileSaveResult(ProfileStep Name, ProfileStep Photo)
{
    public bool IsComplete =>
        Name is ProfileStep.NotAttempted or ProfileStep.Saved or ProfileStep.ExistingValue &&
        Photo is ProfileStep.NotAttempted or ProfileStep.Saved or ProfileStep.ExistingValue;
}

/// <summary>
/// Optional completion of a blank Gateway owner profile from the current Windows account.
/// The Gateway has no conditional write, so each field is re-read immediately before it is written and an
/// existing value is kept; this narrows but cannot eliminate a concurrent edit. Nothing is written before Save.
/// </summary>
internal sealed class ProfileCompletionService(
    Func<IOperatorGatewayClient?> getClient,
    Func<string?> getGatewayId,
    Func<bool> isEligible,
    IWindowsAccountIdentityProvider provider)
{
    internal const string OwnerProfileId = "gateway-owner";
    private const int ReadTimeoutMs = 12000;
    private const int WriteTimeoutMs = 20000;
    private bool _saving;

    internal readonly record struct SelfState(string Id, bool HasName, bool HasEmail, bool HasAvatar);

    internal static SelfState ReadSelf(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("users.self did not return a profile.");

        var hasName = profile.TryGetProperty("displayName", out var name) &&
            name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString());
        var hasEmail = profile.TryGetProperty("emails", out var emails) && emails.ValueKind == JsonValueKind.Array &&
            emails.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()));
        var hasAvatar = profile.TryGetProperty("hasAvatar", out var avatar) && avatar.ValueKind == JsonValueKind.True;
        return new SelfState(id.GetString()!, hasName, hasEmail, hasAvatar);
    }

    internal static bool CanWrite(IOperatorGatewayClient client) =>
        client.GrantedOperatorScopes.Any(scope => scope is "operator.write" or "operator.admin");

    private bool IsSame(IOperatorGatewayClient client, string? gatewayId) =>
        ReferenceEquals(client, getClient()) && client.IsConnectedToGateway &&
        string.Equals(gatewayId, getGatewayId(), StringComparison.Ordinal) && isEligible();

    /// <summary>Reads the profile (a network read, no write) and then the Windows account, only when a suggestion is useful.</summary>
    public async Task<ProfilePreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        var none = new ProfilePreparation(ProfileOffer.Unavailable, null, false, false, false, null);
        var client = getClient();
        var gatewayId = getGatewayId();
        if (client is not { IsConnectedToGateway: true } || gatewayId is null || !isEligible())
            return none;

        SelfState self;
        try
        {
            self = ReadSelf(await client.SendWizardRequestAsync("users.self", new { }, ReadTimeoutMs));
        }
        catch (Exception ex) when (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Connection lifetime or drain cancellation, including the parameterless Drain's plain cancellation.
            return none;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException)
        {
            return none;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSame(client, gatewayId))
            return none;

        // Only the canonical, entirely blank owner is offered a suggestion; a named, email-backed, or photo profile is left alone.
        if (self.Id != OwnerProfileId || self.HasName || self.HasEmail || self.HasAvatar)
            return new ProfilePreparation(ProfileOffer.NotNeeded, self.Id,             CanWrite(client), false, false, null, gatewayId, client);

        var account = await provider.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSame(client, gatewayId))
            return none;

        return new ProfilePreparation(ProfileOffer.Offer, self.Id, CanWrite(client), true, true, account, gatewayId, client);
    }

    /// <summary>
    /// Writes the user-confirmed name and optionally the photo. Neither value is sent unless supplied.
    /// Each step reports its own outcome; a failed photo does not undo a saved name, and no rollback is attempted.
    /// </summary>
    public async Task<ProfileSaveResult> SaveAsync(
        ProfilePreparation preparation, string? confirmedName, AccountPhotoDraft? photo,
        bool retryNameOnly, bool retryPhotoOnly, CancellationToken cancellationToken)
    {
        if (_saving)
            throw new InvalidOperationException("A profile save is already in progress.");
        _saving = true;
        try
        {
            // Consent is bound to the exact Gateway and client it was prepared against; never retarget it.
            var client = preparation.Client;
            var gatewayId = preparation.GatewayId;
            if (client is null || gatewayId is null || preparation.ProfileId is null || !IsSame(client, gatewayId))
                return new ProfileSaveResult(ProfileStep.ContextChanged, ProfileStep.ContextChanged);
            if (!CanWrite(client))
                return new ProfileSaveResult(
                    confirmedName is null ? ProfileStep.NotAttempted : ProfileStep.Denied,
                    photo is null ? ProfileStep.NotAttempted : ProfileStep.Denied);

            var nameStep = ProfileStep.NotAttempted;
            var photoStep = ProfileStep.NotAttempted;

            if (confirmedName is not null && !retryPhotoOnly)
            {
                if (!ProfileNamePolicy.TryNormalize(confirmedName, out var name))
                    throw new ArgumentException("The display name is not valid.", nameof(confirmedName));
                nameStep = await WriteFieldAsync(client, gatewayId, preparation.ProfileId,
                    state => state.HasName, "users.setDisplayName",
                    new { profileId = preparation.ProfileId, displayName = name }, cancellationToken);
            }

            if (photo is not null && !retryNameOnly)
            {
                if (nameStep is ProfileStep.ContextChanged or ProfileStep.ConnectionLost)
                    photoStep = nameStep;
                else if (nameStep == ProfileStep.Uncertain)
                    photoStep = ProfileStep.NotAttempted;
                else
                {
                    try
                    {
                        photoStep = await WriteFieldAsync(client, gatewayId, preparation.ProfileId,
                            state => state.HasAvatar, "users.setAvatar",
                            new { profileId = preparation.ProfileId, mime = photo.Mime, avatarBase64 = Convert.ToBase64String(photo.Bytes) },
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && nameStep != ProfileStep.NotAttempted)
                    {
                        // Caller cancellation during the photo recheck: keep the name outcome and never report the
                        // requested photo as complete.
                        photoStep = ProfileStep.Uncertain;
                    }
                }
            }

            return new ProfileSaveResult(nameStep, photoStep);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// A transport cancellation (connection drained or disposed, or the connection-lifetime token firing) is a
    /// plain OperationCanceledException/TaskCanceledException as well as GatewayConnectionLostException. It is
    /// only treated as caller cancellation when the caller's own token is cancelled.
    /// </summary>
    private static bool IsTransportLoss(Exception ex, CancellationToken callerToken) =>
        ex is OperationCanceledException && !callerToken.IsCancellationRequested;

    private async Task<ProfileStep> WriteFieldAsync(
        IOperatorGatewayClient client, string gatewayId, string profileId,
        Func<SelfState, bool> alreadySet, string method, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            SelfState self;
            try
            {
                self = ReadSelf(await client.SendWizardRequestAsync("users.self", new { }, ReadTimeoutMs));
            }
            catch (Exception ex) when (IsTransportLoss(ex, cancellationToken))
            {
                return ProfileStep.ConnectionLost;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSame(client, gatewayId) || self.Id != profileId)
                return ProfileStep.ContextChanged;
            if (alreadySet(self))
                return ProfileStep.ExistingValue;

            try
            {
                await client.SendWizardRequestAsync(method, parameters, WriteTimeoutMs);
                return ProfileStep.Saved;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The request was sent. A lost connection, caller cancellation, or reply timeout does not
                // cancel the Gateway operation, so the outcome is unknown until the profile is read again.
                return ProfileStep.Uncertain;
            }
            catch (InvalidOperationException ex) when (!GatewayRequestErrors.TryGetCodes(ex, out _, out _))
            {
                // Uncoded means raised locally, not a Gateway reply. The send path also reports false after the
                // frame was written when the connection generation changed, so it may have been transmitted.
                return ProfileStep.Uncertain;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException)
        {
            return GatewayRequestErrors.TryGetCodes(ex, out var code, out _) && code == "FORBIDDEN"
                ? ProfileStep.Denied
                : ProfileStep.Failed;
        }
    }
}
/// <summary>
/// Remembers, per Gateway record id, that the user declined the optional profile offer. Holds no name, photo, or
/// account data; entries for records that no longer exist are pruned lazily the next time a decline is recorded.
/// </summary>
internal sealed class ProfileCompletionDeclineStore(string filePath)
{
    private HashSet<string> Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool IsDeclined(string gatewayId) => Load().Contains(gatewayId);

    public void Decline(string gatewayId, IEnumerable<string> liveGatewayIds)
    {
        var live = liveGatewayIds.ToHashSet(StringComparer.Ordinal);
        var next = Load();
        next.RemoveWhere(id => !live.Contains(id));
        next.Add(gatewayId);
        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(next));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a missing flag only means the optional offer may be shown again.
        }
    }
}
