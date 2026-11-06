using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Whether a Gateway record is a locally created, setup-managed Gateway whose blank owner profile may receive
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

/// <summary>
/// A connected operator and the matching selected, managed Gateway. Registry selection can change before the
/// old operator is retired, so selection alone is not evidence of the RPC destination.
/// </summary>
internal sealed record ProfileCompletionContext(string GatewayId, IOperatorGatewayClient Client)
{
    public bool Matches(ProfileCompletionContext? other) =>
        other is not null && ReferenceEquals(Client, other.Client) &&
        string.Equals(GatewayId, other.GatewayId, StringComparison.Ordinal);

    public static ProfileCompletionContext? Capture(IGatewayConnectionManager? manager, GatewayRegistry? registry)
    {
        var snapshot = manager?.CurrentSnapshot;
        var client = manager?.OperatorClient;
        var record = registry?.GetActive();
        if (snapshot is not { OperatorState: RoleConnectionState.Connected, GatewayId: { } gatewayId } ||
            client is not { IsConnectedToGateway: true } ||
            !string.Equals(record?.Id, gatewayId, StringComparison.Ordinal) ||
            !ProfileCompletionEligibility.IsEligible(record) ||
            !string.Equals(registry?.ActiveGatewayId, gatewayId, StringComparison.Ordinal) ||
            !ReferenceEquals(snapshot, manager?.CurrentSnapshot) ||
            !ReferenceEquals(client, manager?.OperatorClient))
            return null;

        return new ProfileCompletionContext(gatewayId, client);
    }
}

internal sealed record ProfileAutoFillResult(ProfileOffer Offer, ProfileSaveResult? Save)
{
    /// <summary>
    /// Nothing was read (not connected, read failed) or the context moved before a field was written, so a later
    /// identity update may try again. Uncertain, denied, and failed writes do not request an automatic retry.
    /// </summary>
    public bool RetryLater =>
        Offer == ProfileOffer.Unavailable ||
        Save is { } save && (save.Name is ProfileStep.ConnectionLost or ProfileStep.ContextChanged ||
                             save.Photo is ProfileStep.ConnectionLost or ProfileStep.ContextChanged);

    /// <summary>A write was sent, so the shown identity should be re-read from the Gateway.</summary>
    public bool MayHaveChanged =>
        Save is { } save && (save.Name is ProfileStep.Saved or ProfileStep.Uncertain ||
                             save.Photo is ProfileStep.Saved or ProfileStep.Uncertain);
}

internal enum ProfileOffer
{
    /// <summary>Not connected, read scope missing, or the profile could not be read.</summary>
    Unavailable,
    /// <summary>No missing field can be filled for this profile and connection.</summary>
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
/// Automatically fills missing Gateway owner fields from the current Windows account.
/// The Gateway has no conditional write, so each field is re-read immediately before it is written and an
/// existing value is kept; this narrows but cannot eliminate a concurrent edit.
/// </summary>
internal sealed class ProfileCompletionService(
    Func<ProfileCompletionContext?> getContext,
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
        getContext() is { } current && ReferenceEquals(client, current.Client) &&
        client.IsConnectedToGateway && string.Equals(gatewayId, current.GatewayId, StringComparison.Ordinal);

    /// <summary>
    /// Mac parity. A macOS Gateway fills a blank owner name from its host account on every owner connection and
    /// serves the host account photo while no avatar is uploaded, without asking. A WSL or native Windows Gateway
    /// cannot read the Windows account, so the app fills missing fields instead. Unlike Mac's dynamic photo
    /// fallback, this persists the photo on the Gateway. Writes recheck fields but are not atomic compare-and-set.
    /// </summary>
    public async Task<ProfileAutoFillResult> AutoFillAsync(
        CancellationToken cancellationToken, ProfileCompletionContext? expectedContext = null)
    {
        var preparation = await PrepareAsync(cancellationToken, expectedContext);
        if (preparation is not { Offer: ProfileOffer.Offer, Account: { } account })
            return new ProfileAutoFillResult(preparation.Offer, null);

        var name = preparation.OfferName && account.NameStatus == AccountFieldStatus.Available &&
            ProfileNamePolicy.TryNormalize(account.DisplayName, out var normalized) ? normalized : null;
        var photo = preparation.OfferPhoto && account.PhotoStatus == AccountFieldStatus.Available ? account.Photo : null;
        if (name is null && photo is null)
            return new ProfileAutoFillResult(ProfileOffer.Offer, null);

        return new ProfileAutoFillResult(ProfileOffer.Offer,
            await SaveAsync(preparation, name, photo, cancellationToken));
    }

    internal async Task<ProfilePreparation> PrepareAsync(
        CancellationToken cancellationToken, ProfileCompletionContext? expectedContext = null)
    {
        var none = new ProfilePreparation(ProfileOffer.Unavailable, null, false, false, null);
        var context = getContext();
        if (context is null || (expectedContext is not null && !expectedContext.Matches(context)))
            return none;
        var client = context.Client;
        var gatewayId = context.GatewayId;
        if (!client.IsConnectedToGateway)
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

        // A name or an email already identifies the owner. Read nothing from Windows unless a field can be filled.
        var offerName = !self.HasName && !self.HasEmail;
        var offerPhoto = !self.HasAvatar;
        var canWrite = CanWrite(client);
        var needed = self.Id == OwnerProfileId && canWrite && (offerName || offerPhoto);
        if (!needed)
            return new ProfilePreparation(ProfileOffer.NotNeeded, self.Id, false, false, null, gatewayId, client);

        var account = await provider.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSame(client, gatewayId))
            return none;

        return new ProfilePreparation(ProfileOffer.Offer, self.Id, offerName, offerPhoto, account, gatewayId, client);
    }

    /// <summary>
    /// Writes the selected account fields. Neither value is sent unless supplied.
    /// Each step reports its own outcome; a failed photo does not undo a saved name, and no rollback is attempted.
    /// </summary>
    internal async Task<ProfileSaveResult> SaveAsync(
        ProfilePreparation preparation, string? confirmedName, AccountPhotoDraft? photo,
        CancellationToken cancellationToken)
    {
        if (_saving)
            throw new InvalidOperationException("A profile save is already in progress.");
        _saving = true;
        try
        {
            // Account data is bound to the exact Gateway and client it was prepared against; never retarget it.
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

            if (confirmedName is not null)
            {
                if (!ProfileNamePolicy.TryNormalize(confirmedName, out var name))
                    throw new ArgumentException("The display name is not valid.", nameof(confirmedName));
                nameStep = await WriteFieldAsync(client, gatewayId, preparation.ProfileId,
                    state => state.HasName || state.HasEmail, "users.setDisplayName",
                    new { profileId = preparation.ProfileId, displayName = name }, cancellationToken);
            }

            if (photo is not null)
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
            catch (InvalidOperationException ex) when (
                GatewayRequestErrors.TryGetCodes(ex, out var writeCode, out _) && writeCode is "UNAVAILABLE" or "UNKNOWN")
            {
                // Newer upstream Gateways can raise UNAVAILABLE after the store write when a post-write currency
                // check fails, and UNKNOWN is an unrecognized code. Neither proves nothing was written.
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
