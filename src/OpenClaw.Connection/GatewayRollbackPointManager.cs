using OpenClaw.Shared.Mcp;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Connection;

public enum GatewayRollbackPointPhase
{
    Creating,
    DistroStopped,
    Verified,
    UpdateInProgress,
    PostUpdateHealthy,
    RestoreStaged,
    UnregisterPending,
    DistroUnregistered,
    ImportPending,
    Imported,
    RestoreCancelled,
    RestoreHealthy,
    Failed
}

public enum GatewayRollbackPointVerificationStatus
{
    Pending,
    Verified,
    Failed
}

public sealed record GatewayRollbackPointManifest
{
    public int SchemaVersion { get; init; } = 3;
    public required string Id { get; init; }
    public required string DistroName { get; init; }
    public required string GatewayId { get; init; }
    public required string OpenClawVersion { get; init; }
    public required string TargetOpenClawVersion { get; init; }
    public required string InternalIdentitySha256 { get; init; }
    public required uint RegistrationVersion { get; init; }
    public required uint RegistrationDefaultUid { get; init; }
    public required uint RegistrationFlags { get; init; }
    public required string DefaultUserName { get; init; }
    public required uint DefaultUserUid { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required string VhdSha256 { get; init; }
    public required long VhdSizeBytes { get; init; }
    public required GatewayRollbackPointVerificationStatus VerificationStatus { get; init; }
    public required GatewayRollbackPointPhase Phase { get; init; }
    public required bool WasKnownGood { get; init; }
    public required bool RestoreEligible { get; init; }
    public string? NodeCommandAllowSnapshotJson { get; init; }
    public string? LastFailureCode { get; init; }
}

public sealed record GatewayRollbackPointInfo(
    string Id,
    string DistroName,
    string OpenClawVersion,
    DateTimeOffset CreatedAtUtc,
    GatewayRollbackPointVerificationStatus VerificationStatus,
    GatewayRollbackPointPhase Phase,
    long ApproximateSizeBytes,
    bool RestoreEligible,
    string? FailureCode);

public sealed record GatewayRollbackRetentionPolicy(int RetainPreviousVersions, TimeSpan? RetainYoungerThan)
{
    public static GatewayRollbackRetentionPolicy Default { get; } = new(1, null);

    public bool RetainIndefinitely => RetainPreviousVersions == -1;

    public void Validate()
    {
        if (RetainPreviousVersions is not (1 or 2 or -1))
            throw new ArgumentOutOfRangeException(nameof(RetainPreviousVersions), "Retention must be 1, 2, or -1 for indefinitely.");
        if (RetainYoungerThan is { } age && age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetainYoungerThan), "Age retention must be positive when enabled.");
    }
}

public enum GatewayRollbackOperationState
{
    Created,
    Restored,
    ConfirmationRequired,
    NotFound,
    VerificationFailed,
    OwnershipMismatch,
    SameNameCollision,
    InstallPathCollision,
    TerminationFailed,
    UnregisterFailed,
    ImportPending,
    Cancelled,
    ResumeRequired,
    AmbiguousRecovery,
    Failed
}

public sealed record GatewayRollbackOperationResult(
    GatewayRollbackOperationState State,
    GatewayRollbackPointManifest? Point = null,
    string? FailureCode = null,
    int? ExitCode = null)
{
    public bool Success => State is GatewayRollbackOperationState.Created
        or GatewayRollbackOperationState.Restored
        or GatewayRollbackOperationState.Cancelled;
}

/// <summary>
/// Owns immutable, offline VHD rollback points for one Companion-owned WSL distro.
/// Normal update never unregisters or imports. Those lifecycle operations are
/// reachable only through the explicit restore method and a matching point id.
/// </summary>
public sealed partial class GatewayRollbackPointManager
{
    private const string ManifestFileName = "manifest.json";
    private const string RollbackVhdFileName = "rollback.vhdx";
    private const string LiveVhdFileName = "ext4.vhdx";
    private const string VhdxSignature = "vhdxfile";
    private readonly IWslCommandRunner _commandRunner;
    private readonly string _ownedDistroName;
    private readonly string _localDataRoot;
    private readonly string _pointsRoot;
    private readonly string _stagingRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GatewayRollbackPointManager(
        IWslCommandRunner commandRunner,
        string localDataRoot,
        string ownedDistroName,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedDistroName);
        if (!DistroNameRegex().IsMatch(ownedDistroName))
            throw new ArgumentException("Owned distro name is not a safe WSL/path name.", nameof(ownedDistroName));

        _commandRunner = commandRunner;
        _ownedDistroName = ownedDistroName;
        _localDataRoot = NormalizePath(localDataRoot);
        _pointsRoot = NormalizePath(Path.Combine(_localDataRoot, "gateway-rollback-points", ownedDistroName));
        _stagingRoot = NormalizePath(Path.Combine(_localDataRoot, "gateway-rollback-staging", ownedDistroName));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string OwnedDistroName => _ownedDistroName;

    public bool HasUnreadableReceipt()
    {
        if (!Directory.Exists(_pointsRoot))
            return false;
        if (!HasSafePathBoundary(_localDataRoot, _pointsRoot))
            return true;

        try
        {
            foreach (var pointDirectory in Directory.EnumerateDirectories(_pointsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var pointId = Path.GetFileName(pointDirectory);
                if (!PointIdRegex().IsMatch(pointId))
                    continue;
                if (!HasSafePathBoundary(_pointsRoot, pointDirectory))
                    return true;

                var manifestPath = Path.Combine(pointDirectory, ManifestFileName);
                if (!File.Exists(manifestPath) ||
                    IsReparsePoint(manifestPath) ||
                    !TryReadManifest(manifestPath, out var manifest) ||
                    manifest is null ||
                    !string.Equals(manifest.Id, pointId, StringComparison.Ordinal) ||
                    !IsManifestOwned(manifest))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    public IReadOnlyList<GatewayRollbackPointInfo> List()
    {
        if (!Directory.Exists(_pointsRoot) ||
            !HasSafePathBoundary(_localDataRoot, _pointsRoot))
            return [];

        var points = new List<GatewayRollbackPointInfo>();
        foreach (var pointDirectory in Directory.EnumerateDirectories(_pointsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!PointIdRegex().IsMatch(Path.GetFileName(pointDirectory)) ||
                !HasSafePathBoundary(_pointsRoot, pointDirectory))
            {
                continue;
            }
            var manifestPath = Path.Combine(pointDirectory, ManifestFileName);
            if (!File.Exists(manifestPath) || IsReparsePoint(manifestPath))
                continue;
            if (!TryReadManifest(manifestPath, out var manifest) || manifest is null)
                continue;
            points.Add(ToInfo(manifest));
        }

        return points.OrderByDescending(point => point.CreatedAtUtc).ThenByDescending(point => point.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<GatewayRollbackOperationResult> CreateVerifiedAsync(
        string distroName,
        string gatewayId,
        string openClawVersion,
        string targetOpenClawVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwnedDistro(distroName) || string.IsNullOrWhiteSpace(gatewayId))
            return new(GatewayRollbackOperationState.OwnershipMismatch, FailureCode: "ownership_mismatch");
        if (!ExactVersionRegex().IsMatch(openClawVersion) || !ExactVersionRegex().IsMatch(targetOpenClawVersion))
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "version_attestation_invalid");
        if (!HasSafePathBoundary(_localDataRoot, _pointsRoot))
            return new(GatewayRollbackOperationState.InstallPathCollision, FailureCode: "rollback_points_reparse_boundary");
        if (HasUnreadableReceipt())
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "rollback_receipt_unreadable");

        var liveRegistration = await ValidateOwnedLiveRegistrationAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!liveRegistration.Valid)
            return new(liveRegistration.State, FailureCode: liveRegistration.FailureCode);

        var registration = await _commandRunner.GetDistroConfigurationAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!registration.Success || registration.Configuration is null || registration.Configuration.Version != 2)
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "registration_configuration_probe_failed");
        var defaultUser = await ProbeDefaultUserAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (defaultUser is null)
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "default_user_probe_failed");
        var identity = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (identity is null)
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "identity_probe_failed");
        var versionBeforeStop = await ProbeExactVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(versionBeforeStop, openClawVersion, StringComparison.Ordinal))
            return new(GatewayRollbackOperationState.VerificationFailed, FailureCode: "version_attestation_changed");

        if (!HasSafePathBoundary(_localDataRoot, _pointsRoot))
            return new(GatewayRollbackOperationState.InstallPathCollision, FailureCode: "rollback_points_reparse_boundary");
        EnsurePrivateDirectory(_pointsRoot);
        if (!HasSafePathBoundary(_localDataRoot, _pointsRoot))
            return new(GatewayRollbackOperationState.InstallPathCollision, FailureCode: "rollback_points_reparse_boundary");
        var now = _utcNow();
        var pointId = $"{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var pointDirectory = GetPointDirectory(pointId);
        EnsurePrivateDirectory(pointDirectory);
        var vhdPath = Path.Combine(pointDirectory, RollbackVhdFileName);
        var partialPath = vhdPath + ".partial";
        if (!HasSafePathBoundary(_pointsRoot, pointDirectory) ||
            !HasSafePathBoundary(pointDirectory, vhdPath) ||
            !HasSafePathBoundary(pointDirectory, partialPath))
        {
            return new(GatewayRollbackOperationState.InstallPathCollision, FailureCode: "rollback_point_reparse_boundary");
        }
        var manifest = new GatewayRollbackPointManifest
        {
            Id = pointId,
            DistroName = distroName,
            GatewayId = gatewayId,
            OpenClawVersion = openClawVersion,
            TargetOpenClawVersion = targetOpenClawVersion,
            InternalIdentitySha256 = identity,
            RegistrationVersion = registration.Configuration.Version,
            RegistrationDefaultUid = registration.Configuration.DefaultUid,
            RegistrationFlags = registration.Configuration.Flags,
            DefaultUserName = defaultUser.Name,
            DefaultUserUid = defaultUser.Uid,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            VhdSha256 = string.Empty,
            VhdSizeBytes = 0,
            VerificationStatus = GatewayRollbackPointVerificationStatus.Pending,
            Phase = GatewayRollbackPointPhase.Creating,
            WasKnownGood = true,
            RestoreEligible = false
        };
        WriteManifest(manifest);
        var verifiedManifestCommitted = false;

        try
        {
            var terminate = await _commandRunner.TerminateDistroAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (!terminate.Success)
                return Fail(manifest, GatewayRollbackOperationState.TerminationFailed, "terminate_failed", terminate.ExitCode);

            manifest = UpdateManifest(manifest, GatewayRollbackPointPhase.DistroStopped);
            var export = await _commandRunner.RunAsync(
                ["--export", distroName, partialPath, "--vhd"],
                cancellationToken).ConfigureAwait(false);
            if (!export.Success)
                return Fail(manifest, GatewayRollbackOperationState.Failed, "vhd_export_failed", export.ExitCode);

            if (!File.Exists(partialPath))
                return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "vhd_export_missing");

            File.Move(partialPath, vhdPath, overwrite: false);
            var verification = await VerifyVhdAsync(vhdPath, expectedSha256: null, expectedSize: null, cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Verified)
                return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, verification.FailureCode ?? "vhd_verify_failed");

            // The retained VHD is opaque to the host. Attest the package version and
            // distro identity immediately on both sides of the stop/export boundary;
            // any concurrent mutation makes the point ineligible before update.
            var versionAfterExport = await ProbeExactVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
            var identityAfterExport = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
            var registrationAfterExport = await _commandRunner.GetDistroConfigurationAsync(distroName, cancellationToken).ConfigureAwait(false);
            var defaultUserAfterExport = await ProbeDefaultUserAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(versionAfterExport, openClawVersion, StringComparison.Ordinal) ||
                !string.Equals(identityAfterExport, identity, StringComparison.Ordinal) ||
                !registrationAfterExport.Success ||
                registrationAfterExport.Configuration != registration.Configuration ||
                defaultUserAfterExport != defaultUser)
            {
                return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "exported_state_attestation_changed");
            }

            manifest = manifest with
            {
                UpdatedAtUtc = _utcNow(),
                VhdSha256 = verification.Sha256!,
                VhdSizeBytes = verification.SizeBytes,
                VerificationStatus = GatewayRollbackPointVerificationStatus.Verified,
                Phase = GatewayRollbackPointPhase.Verified,
                RestoreEligible = true,
                LastFailureCode = null
            };
            WriteManifest(manifest);
            verifiedManifestCommitted = true;
            return new(GatewayRollbackOperationState.Created, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return Fail(manifest, GatewayRollbackOperationState.Failed, $"{ex.GetType().Name.ToLowerInvariant()}");
        }
        finally
        {
            TryDeleteGeneratedFile(partialPath);
            if (!verifiedManifestCommitted)
                TryDeleteGeneratedFile(vhdPath);
        }
    }

    public IReadOnlyList<GatewayRollbackPointManifest> FindPendingUpdates(string? targetOpenClawVersion = null) =>
        LoadOwnedManifests()
            .Where(point =>
                point.Phase == GatewayRollbackPointPhase.UpdateInProgress &&
                (targetOpenClawVersion is null ||
                 string.Equals(point.TargetOpenClawVersion, targetOpenClawVersion, StringComparison.Ordinal)))
            .OrderByDescending(point => point.CreatedAtUtc)
            .ThenByDescending(point => point.Id, StringComparer.Ordinal)
            .ToArray();

    public GatewayRollbackPointManifest? FindPendingUpdate(string gatewayId, string? targetOpenClawVersion = null) =>
        FindPendingUpdates(targetOpenClawVersion).FirstOrDefault();

    public IReadOnlyList<GatewayRollbackPointManifest> FindUnresolvedRestores() =>
        LoadOwnedManifests()
            .Where(point =>
                IsUnresolvedRestorePhase(point.Phase))
            .OrderBy(point => point.CreatedAtUtc)
            .ThenBy(point => point.Id, StringComparer.Ordinal)
            .ToArray();

    public async Task<bool> VerifyAsync(string pointId, CancellationToken cancellationToken = default)
    {
        if (!TryLoadPoint(pointId, out var manifest) || manifest is null || !IsManifestOwned(manifest))
            return false;
        if (manifest.VerificationStatus != GatewayRollbackPointVerificationStatus.Verified ||
            !manifest.WasKnownGood || !manifest.RestoreEligible)
            return false;

        var result = await VerifyVhdAsync(
            GetRollbackVhdPath(pointId), manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
        if (!result.Verified)
        {
            WriteManifest(manifest with
            {
                UpdatedAtUtc = _utcNow(),
                VerificationStatus = GatewayRollbackPointVerificationStatus.Failed,
                RestoreEligible = false,
                LastFailureCode = result.FailureCode ?? "point_verify_failed"
            });
        }
        return result.Verified;
    }

    public async Task<bool> AttestLiveDistroAsync(
        string pointId,
        string distroName,
        string expectedExactVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedExpectedVersion = expectedExactVersion?.Trim();
        if (!IsOwnedDistro(distroName) ||
            normalizedExpectedVersion is null ||
            !ExactVersionRegex().IsMatch(normalizedExpectedVersion) ||
            !TryLoadPoint(pointId, out var manifest) ||
            manifest is null ||
            !IsManifestOwned(manifest))
        {
            return false;
        }

        var registrations = await _commandRunner.ListRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        var matching = registrations
            .Where(registration => string.Equals(registration.Name, distroName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length != 1)
            return false;

        string registeredBasePath;
        try { registeredBasePath = NormalizePath(matching[0].BasePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!string.Equals(registeredBasePath, GetInstallDirectory(), StringComparison.OrdinalIgnoreCase))
            return false;

        var registration = await _commandRunner.GetDistroConfigurationAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!registration.Success ||
            registration.Configuration is null ||
            registration.Configuration.Version != manifest.RegistrationVersion ||
            registration.Configuration.DefaultUid != manifest.RegistrationDefaultUid ||
            registration.Configuration.Flags != manifest.RegistrationFlags)
        {
            return false;
        }

        var identity = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(identity, manifest.InternalIdentitySha256, StringComparison.Ordinal))
            return false;

        var defaultUser = await ProbeDefaultUserAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (defaultUser is null ||
            defaultUser.Name != manifest.DefaultUserName ||
            defaultUser.Uid != manifest.DefaultUserUid)
        {
            return false;
        }

        var exactVersion = await ProbeExactVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
        return string.Equals(exactVersion, normalizedExpectedVersion, StringComparison.Ordinal);
    }

    public void MarkUpdateInProgress(string pointId) => UpdatePhase(pointId, GatewayRollbackPointPhase.UpdateInProgress);

    public void MarkPostUpdateHealthy(string pointId) => UpdatePhase(pointId, GatewayRollbackPointPhase.PostUpdateHealthy);

    public void MarkRestoreHealthy(string pointId) => UpdatePhase(pointId, GatewayRollbackPointPhase.RestoreHealthy);

    public void MarkImported(string pointId) => UpdatePhase(pointId, GatewayRollbackPointPhase.Imported);

    public GatewayRollbackPointManifest RecordNodeCommandAllowSnapshot(string pointId, string normalizedArrayJson)
    {
        if (!TryLoadPoint(pointId, out var manifest) || manifest is null || !IsManifestOwned(manifest))
            throw new InvalidOperationException("Rollback point is missing or is not owned by this Companion distro.");
        if (manifest.Phase != GatewayRollbackPointPhase.Verified ||
            !IsCompleteCommandArrayJson(normalizedArrayJson))
        {
            throw new InvalidOperationException("The node command policy snapshot is invalid for this rollback point.");
        }

        var updated = manifest with
        {
            NodeCommandAllowSnapshotJson = normalizedArrayJson,
            UpdatedAtUtc = _utcNow(),
            LastFailureCode = null
        };
        WriteManifest(updated);
        return updated;
    }

    public GatewayRollbackOperationResult CancelRestore(
        string distroName,
        string gatewayId,
        string pointId)
    {
        if (!IsOwnedDistro(distroName) || string.IsNullOrWhiteSpace(gatewayId))
            return new(GatewayRollbackOperationState.OwnershipMismatch, FailureCode: "ownership_mismatch");
        if (HasUnreadableReceipt())
            return new(GatewayRollbackOperationState.AmbiguousRecovery, FailureCode: "rollback_receipt_unreadable");

        var unresolved = FindUnresolvedRestores();
        if (unresolved.Count > 1)
            return new(GatewayRollbackOperationState.AmbiguousRecovery, FailureCode: "multiple_unresolved_restores");
        if (unresolved.Count == 1 && !string.Equals(unresolved[0].Id, pointId, StringComparison.Ordinal))
            return new(GatewayRollbackOperationState.ResumeRequired, unresolved[0], "restore_resume_required");
        if (!TryLoadPoint(pointId, out var manifest) || manifest is null)
            return new(GatewayRollbackOperationState.NotFound, FailureCode: "point_not_found");
        if (!IsManifestOwned(manifest) ||
            (!IsRecoveryReceiptPhase(manifest.Phase) &&
             !string.Equals(manifest.GatewayId, gatewayId, StringComparison.Ordinal)))
        {
            return new(GatewayRollbackOperationState.OwnershipMismatch, manifest, "point_ownership_mismatch");
        }
        if (manifest.Phase != GatewayRollbackPointPhase.RestoreStaged)
            return new(GatewayRollbackOperationState.ResumeRequired, manifest, "restore_resume_required");

        var stagePath = GetStageVhdPath(pointId);
        if (!DeleteGeneratedFile(stagePath, _stagingRoot))
            return new(GatewayRollbackOperationState.VerificationFailed, manifest, "restore_stage_cancel_cleanup_failed");

        var cancelled = UpdateManifest(manifest, GatewayRollbackPointPhase.RestoreCancelled);
        return new(GatewayRollbackOperationState.Cancelled, cancelled);
    }

    public async Task<GatewayRollbackOperationResult> RestoreExplicitAsync(
        string distroName,
        string gatewayId,
        string pointId,
        string confirmedPointId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(pointId, confirmedPointId, StringComparison.Ordinal))
            return new(GatewayRollbackOperationState.ConfirmationRequired, FailureCode: "confirmation_required");
        if (!IsOwnedDistro(distroName) || string.IsNullOrWhiteSpace(gatewayId))
            return new(GatewayRollbackOperationState.OwnershipMismatch, FailureCode: "ownership_mismatch");
        if (HasUnreadableReceipt())
            return new(GatewayRollbackOperationState.AmbiguousRecovery, FailureCode: "rollback_receipt_unreadable");
        if (!TryLoadPoint(pointId, out var manifest) || manifest is null)
            return new(GatewayRollbackOperationState.NotFound, FailureCode: "point_not_found");
        if (!IsManifestOwned(manifest) ||
            (!IsRecoveryReceiptPhase(manifest.Phase) &&
             !string.Equals(manifest.GatewayId, gatewayId, StringComparison.Ordinal)))
            return new(GatewayRollbackOperationState.OwnershipMismatch, manifest, "point_ownership_mismatch");

        var pendingUpdates = FindPendingUpdates();
        if (pendingUpdates.Count > 1)
            return new(GatewayRollbackOperationState.AmbiguousRecovery, manifest, "multiple_pending_updates");
        if (pendingUpdates.Count == 1 &&
            !string.Equals(pendingUpdates[0].Id, pointId, StringComparison.Ordinal))
        {
            return new(GatewayRollbackOperationState.ResumeRequired, pendingUpdates[0], "update_recovery_resume_required");
        }

        var unresolved = FindUnresolvedRestores();
        if (unresolved.Count > 1)
            return new(GatewayRollbackOperationState.AmbiguousRecovery, manifest, "multiple_unresolved_restores");
        if (unresolved.Count == 1 && !string.Equals(unresolved[0].Id, pointId, StringComparison.Ordinal))
            return new(GatewayRollbackOperationState.ResumeRequired, unresolved[0], "restore_resume_required");

        if (!await VerifyAsync(pointId, cancellationToken).ConfigureAwait(false))
        {
            if (TryLoadPoint(pointId, out var invalidated) && invalidated is not null)
                manifest = invalidated;
            return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "point_verify_failed");
        }

        var stagePath = GetStageVhdPath(pointId);
        var installDirectory = GetInstallDirectory();
        var liveVhdPath = Path.Combine(installDirectory, LiveVhdFileName);

        try
        {
            var distroList = await ListDistroNamesAsync(cancellationToken).ConfigureAwait(false);
            if (!distroList.Success)
                return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "distro_list_failed", distroList.ExitCode);
            var sameNameCount = distroList.Names.Count(name => string.Equals(name, distroName, StringComparison.OrdinalIgnoreCase));
            if (sameNameCount > 1)
                return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "duplicate_same_name_registration");
            var sameNameExists = sameNameCount == 1;
            var initialPreflight = await ValidateRestorePreflightAsync(
                distroName, installDirectory, liveVhdPath, sameNameExists, cancellationToken).ConfigureAwait(false);
            if (!initialPreflight.Valid)
                return Fail(manifest, initialPreflight.State, initialPreflight.FailureCode);
            if (sameNameExists && manifest.Phase is GatewayRollbackPointPhase.ImportPending or GatewayRollbackPointPhase.Imported)
            {
                var existingIdentity = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existingIdentity, manifest.InternalIdentitySha256, StringComparison.Ordinal))
                    return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "same_name_identity_collision");
                return await FinalizeImportedRegistrationAsync(distroName, manifest, cancellationToken).ConfigureAwait(false);
            }
            if (!sameNameExists && manifest.Phase == GatewayRollbackPointPhase.Imported)
                return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "restored_registration_missing");
            if (sameNameExists && manifest.Phase == GatewayRollbackPointPhase.DistroUnregistered)
                return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "registration_reappeared_after_unregister");

            if (PathExists(stagePath) && !HasSafePathBoundary(_stagingRoot, stagePath))
                return Fail(manifest, GatewayRollbackOperationState.InstallPathCollision, "restore_stage_reparse_boundary");

            if (File.Exists(stagePath))
            {
                var staged = await VerifyVhdAsync(stagePath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (!staged.Verified)
                {
                    if (!DeleteGeneratedFile(stagePath, _stagingRoot))
                        return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "restore_stage_invalid_delete_failed");
                    var recreated = await CreateVerifiedStageAsync(stagePath, manifest, cancellationToken).ConfigureAwait(false);
                    if (!recreated)
                        return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "restore_stage_recreate_failed");
                }
            }
            else if (!sameNameExists && File.Exists(liveVhdPath))
            {
                var movedStage = await VerifyVhdAsync(
                    liveVhdPath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
                if (!movedStage.Verified)
                    return Fail(manifest, GatewayRollbackOperationState.InstallPathCollision, "recovery_import_vhd_mismatch");
            }
            else
            {
                if (!await CreateVerifiedStageAsync(stagePath, manifest, cancellationToken).ConfigureAwait(false))
                    return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "restore_stage_recreate_failed");
            }

            if (!IsDestructiveRecoveryPhase(manifest.Phase))
                manifest = UpdateManifest(manifest, GatewayRollbackPointPhase.RestoreStaged);

            if (sameNameExists)
            {
                var existingIdentity = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existingIdentity, manifest.InternalIdentitySha256, StringComparison.Ordinal))
                    return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "same_name_identity_collision");

                var terminate = await _commandRunner.TerminateDistroAsync(distroName, cancellationToken).ConfigureAwait(false);
                if (!terminate.Success)
                    return Fail(manifest, GatewayRollbackOperationState.TerminationFailed, "restore_terminate_failed", terminate.ExitCode);

                var destructivePreflight = await ValidateRestorePreflightAsync(
                    distroName, installDirectory, liveVhdPath, sameNameExists: true, cancellationToken).ConfigureAwait(false);
                if (!destructivePreflight.Valid)
                    return Fail(manifest, destructivePreflight.State, destructivePreflight.FailureCode);

                if (!HasSafePathBoundary(_stagingRoot, stagePath))
                    return Fail(manifest, GatewayRollbackOperationState.InstallPathCollision, "restore_stage_reparse_boundary");
                var destructiveStage = await VerifyVhdAsync(
                    stagePath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
                if (!destructiveStage.Verified)
                    return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "restore_stage_changed_before_unregister");

                manifest = UpdateManifest(manifest, GatewayRollbackPointPhase.UnregisterPending);
                var unregister = await _commandRunner.UnregisterDistroAsync(distroName, cancellationToken).ConfigureAwait(false);
                if (!unregister.Success)
                    return Fail(manifest, GatewayRollbackOperationState.UnregisterFailed, "restore_unregister_failed", unregister.ExitCode);
            }

            if (manifest.Phase is not GatewayRollbackPointPhase.DistroUnregistered
                and not GatewayRollbackPointPhase.ImportPending)
            {
                manifest = UpdateManifest(manifest, GatewayRollbackPointPhase.DistroUnregistered);
            }
            var importPath = await PrepareCanonicalImportVhdAsync(
                stagePath, liveVhdPath, manifest, cancellationToken).ConfigureAwait(false);
            if (!importPath.Prepared)
                return Fail(manifest, GatewayRollbackOperationState.InstallPathCollision, importPath.FailureCode ?? "install_path_collision");

            var importPreflight = await ValidateRestorePreflightAsync(
                distroName, installDirectory, liveVhdPath, sameNameExists: false, cancellationToken).ConfigureAwait(false);
            if (!importPreflight.Valid)
                return Fail(manifest, importPreflight.State, importPreflight.FailureCode);

            if (manifest.Phase == GatewayRollbackPointPhase.DistroUnregistered)
                manifest = UpdateManifest(manifest, GatewayRollbackPointPhase.ImportPending);
            var promotedVhd = await VerifyVhdAsync(
                liveVhdPath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
            if (!promotedVhd.Verified)
                return Fail(manifest, GatewayRollbackOperationState.VerificationFailed, "restore_import_vhd_mismatch", preservePhase: true);
            var import = await _commandRunner.RunAsync(
                ["--import-in-place", distroName, liveVhdPath], cancellationToken).ConfigureAwait(false);
            if (!import.Success)
                return Fail(manifest, GatewayRollbackOperationState.ImportPending, "restore_import_failed", import.ExitCode, preservePhase: true);

            return await FinalizeImportedRegistrationAsync(distroName, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecurityException)
        {
            return Fail(manifest, GatewayRollbackOperationState.Failed, ex.GetType().Name.ToLowerInvariant());
        }
    }

    public async Task<int> CleanupAsync(
        GatewayRollbackRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        policy.Validate();
        var ownedManifests = LoadOwnedManifests();
        foreach (var unverified in ownedManifests.Where(point =>
                     point.VerificationStatus != GatewayRollbackPointVerificationStatus.Verified ||
                     !point.RestoreEligible))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pointDirectory = GetPointDirectory(unverified.Id);
            var rollbackVhdPath = GetRollbackVhdPath(unverified.Id);
            DeleteGeneratedFile(rollbackVhdPath, pointDirectory);
            DeleteGeneratedFile(rollbackVhdPath + ".partial", pointDirectory);
        }

        var manifests = ownedManifests
            .Where(point => point.VerificationStatus == GatewayRollbackPointVerificationStatus.Verified && point.RestoreEligible)
            .OrderByDescending(point => point.CreatedAtUtc)
            .ThenByDescending(point => point.Id, StringComparer.Ordinal)
            .ToArray();
        if (manifests.Length == 0 || policy.RetainIndefinitely)
            return 0;

        var keep = new HashSet<string>(StringComparer.Ordinal) { manifests[0].Id };
        foreach (var point in manifests.Take(policy.RetainPreviousVersions))
            keep.Add(point.Id);
        if (policy.RetainYoungerThan is { } age)
        {
            var cutoff = _utcNow() - age;
            foreach (var point in manifests.Where(point => point.CreatedAtUtc >= cutoff))
                keep.Add(point.Id);
        }

        var removed = 0;
        foreach (var point in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keep.Contains(point.Id) || point.Phase is not (GatewayRollbackPointPhase.PostUpdateHealthy or GatewayRollbackPointPhase.RestoreHealthy))
                continue;
            if (DeletePointFilesOnly(point.Id))
                removed++;
            await Task.Yield();
        }
        return removed;
    }

    private async Task<(bool Prepared, string? FailureCode)> PrepareCanonicalImportVhdAsync(
        string stagePath,
        string liveVhdPath,
        GatewayRollbackPointManifest manifest,
        CancellationToken cancellationToken)
    {
        var installDirectory = GetInstallDirectory();
        if (!HasSafePathBoundary(_localDataRoot, installDirectory) ||
            !HasSafePathBoundary(_localDataRoot, liveVhdPath) ||
            !HasSafePathBoundary(_localDataRoot, stagePath))
        {
            return (false, "restore_path_reparse_boundary");
        }
        if (Directory.Exists(installDirectory))
        {
            var entries = Directory.GetFileSystemEntries(installDirectory);
            if (entries.Length > 0)
            {
                if (entries.Length == 1 &&
                    string.Equals(NormalizePath(entries[0]), NormalizePath(liveVhdPath), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(liveVhdPath) &&
                    (await VerifyVhdAsync(
                        liveVhdPath,
                        manifest.VhdSha256,
                        manifest.VhdSizeBytes,
                        cancellationToken).ConfigureAwait(false)).Verified)
                {
                    return (true, null);
                }
                return (false, "install_path_not_empty");
            }
        }
        else
        {
            Directory.CreateDirectory(installDirectory);
            if (!HasSafePathBoundary(_localDataRoot, installDirectory))
                return (false, "restore_path_reparse_boundary");
        }

        if (!File.Exists(stagePath))
            return (false, "restore_stage_missing");
        File.Move(stagePath, liveVhdPath, overwrite: false);
        return (true, null);
    }

    private async Task<bool> CreateVerifiedStageAsync(
        string stagePath,
        GatewayRollbackPointManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory(_stagingRoot);
        if (!HasSafePathBoundary(_stagingRoot, stagePath))
            return false;

        var partialPath = stagePath + ".partial";
        if (File.Exists(partialPath) && !DeleteGeneratedFile(partialPath, _stagingRoot))
            return false;

        try
        {
            await CopyAndFlushAsync(GetRollbackVhdPath(manifest.Id), partialPath, cancellationToken).ConfigureAwait(false);
            var partial = await VerifyVhdAsync(
                partialPath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
            if (!partial.Verified)
                return false;

            File.Move(partialPath, stagePath, overwrite: false);
            var staged = await VerifyVhdAsync(
                stagePath, manifest.VhdSha256, manifest.VhdSizeBytes, cancellationToken).ConfigureAwait(false);
            return staged.Verified;
        }
        finally
        {
            DeleteGeneratedFile(partialPath, _stagingRoot);
        }
    }

    private async Task<(bool Valid, GatewayRollbackOperationState State, string FailureCode)> ValidateRestorePreflightAsync(
        string distroName,
        string installDirectory,
        string liveVhdPath,
        bool sameNameExists,
        CancellationToken cancellationToken)
    {
        if (!HasSafePathBoundary(_localDataRoot, installDirectory) ||
            !HasSafePathBoundary(_localDataRoot, liveVhdPath) ||
            !HasSafePathBoundary(_localDataRoot, _stagingRoot) ||
            !HasSafePathBoundary(_localDataRoot, _pointsRoot))
        {
            return (false, GatewayRollbackOperationState.InstallPathCollision, "restore_path_reparse_boundary");
        }

        var registrations = await _commandRunner.ListRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        var matching = registrations
            .Where(registration => string.Equals(registration.Name, distroName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!sameNameExists)
        {
            return matching.Length == 0
                ? (true, GatewayRollbackOperationState.Restored, string.Empty)
                : (false, GatewayRollbackOperationState.SameNameCollision, "registration_list_disagrees");
        }

        if (matching.Length != 1)
            return (false, GatewayRollbackOperationState.SameNameCollision, "registration_missing_or_duplicate");

        string registeredBasePath;
        try { registeredBasePath = NormalizePath(matching[0].BasePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, GatewayRollbackOperationState.InstallPathCollision, "registration_base_path_invalid");
        }

        if (!string.Equals(registeredBasePath, installDirectory, StringComparison.OrdinalIgnoreCase))
            return (false, GatewayRollbackOperationState.InstallPathCollision, "registration_base_path_mismatch");
        if (!Directory.Exists(installDirectory) || !File.Exists(liveVhdPath))
            return (false, GatewayRollbackOperationState.InstallPathCollision, "registered_install_path_missing");

        var entries = Directory.GetFileSystemEntries(installDirectory).Select(NormalizePath).ToArray();
        if (entries.Length != 1 || !string.Equals(entries[0], NormalizePath(liveVhdPath), StringComparison.OrdinalIgnoreCase))
            return (false, GatewayRollbackOperationState.InstallPathCollision, "install_path_not_exclusive");

        return (true, GatewayRollbackOperationState.Restored, string.Empty);
    }

    private async Task<(bool Valid, GatewayRollbackOperationState State, string FailureCode)> ValidateOwnedLiveRegistrationAsync(
        string distroName,
        CancellationToken cancellationToken)
    {
        var installDirectory = GetInstallDirectory();
        if (!HasSafePathBoundary(_localDataRoot, installDirectory))
            return (false, GatewayRollbackOperationState.InstallPathCollision, "registration_base_path_reparse_boundary");

        var matching = (await _commandRunner.ListRegistrationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(registration => string.Equals(registration.Name, distroName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length != 1)
            return (false, GatewayRollbackOperationState.OwnershipMismatch, "registration_missing_or_duplicate");

        string registeredBasePath;
        try { registeredBasePath = NormalizePath(matching[0].BasePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, GatewayRollbackOperationState.InstallPathCollision, "registration_base_path_invalid");
        }

        return string.Equals(registeredBasePath, installDirectory, StringComparison.OrdinalIgnoreCase)
            ? (true, GatewayRollbackOperationState.Created, string.Empty)
            : (false, GatewayRollbackOperationState.InstallPathCollision, "registration_base_path_mismatch");
    }

    private GatewayRollbackOperationResult Fail(
        GatewayRollbackPointManifest manifest,
        GatewayRollbackOperationState state,
        string failureCode,
        int? exitCode = null,
        bool preservePhase = false)
    {
        var keepRecoveryPhase = preservePhase || IsRecoveryReceiptPhase(manifest.Phase);
        var failed = manifest with
        {
            UpdatedAtUtc = _utcNow(),
            VerificationStatus = manifest.VerificationStatus == GatewayRollbackPointVerificationStatus.Verified
                ? manifest.VerificationStatus
                : GatewayRollbackPointVerificationStatus.Failed,
            Phase = keepRecoveryPhase ? manifest.Phase : GatewayRollbackPointPhase.Failed,
            RestoreEligible = manifest.VerificationStatus == GatewayRollbackPointVerificationStatus.Verified && manifest.WasKnownGood,
            LastFailureCode = failureCode
        };
        WriteManifest(failed);
        return new(state, failed, failureCode, exitCode);
    }

    private static bool IsDestructiveRecoveryPhase(GatewayRollbackPointPhase phase) =>
        phase is GatewayRollbackPointPhase.UnregisterPending
            or GatewayRollbackPointPhase.DistroUnregistered
            or GatewayRollbackPointPhase.ImportPending
            or GatewayRollbackPointPhase.Imported;

    private static bool IsUnresolvedRestorePhase(GatewayRollbackPointPhase phase) =>
        phase is GatewayRollbackPointPhase.RestoreStaged
            or GatewayRollbackPointPhase.UnregisterPending
            or GatewayRollbackPointPhase.DistroUnregistered
            or GatewayRollbackPointPhase.ImportPending
            or GatewayRollbackPointPhase.Imported;

    private static bool IsRecoveryReceiptPhase(GatewayRollbackPointPhase phase) =>
        phase == GatewayRollbackPointPhase.UpdateInProgress || IsUnresolvedRestorePhase(phase);

    private GatewayRollbackPointManifest UpdateManifest(
        GatewayRollbackPointManifest manifest,
        GatewayRollbackPointPhase phase)
    {
        var updated = manifest with { Phase = phase, UpdatedAtUtc = _utcNow(), LastFailureCode = null };
        WriteManifest(updated);
        return updated;
    }

    private void UpdatePhase(string pointId, GatewayRollbackPointPhase phase)
    {
        if (!TryLoadPoint(pointId, out var manifest) || manifest is null || !IsManifestOwned(manifest))
            throw new InvalidOperationException("Rollback point is missing or is not owned by this Companion distro.");
        UpdateManifest(manifest, phase);
    }

    private async Task<string?> ProbeInternalIdentityHashAsync(string distroName, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunInDistroAsync(
            distroName, ["sh", "-lc", "cat /etc/machine-id"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return null;
        var identity = result.StandardOutput.Trim();
        if (!MachineIdRegex().IsMatch(identity))
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private async Task<DefaultUserIdentity?> ProbeDefaultUserAsync(string distroName, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunInDistroAsync(
            distroName,
            ["sh", "-lc", "printf '%s\\n%s\\n' \"$(id -un)\" \"$(id -u)\""],
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return null;
        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 ||
            !DefaultUserNameRegex().IsMatch(lines[0]) ||
            !uint.TryParse(lines[1], out var uid))
        {
            return null;
        }
        return new(lines[0], uid);
    }

    private async Task<GatewayRollbackOperationResult> FinalizeImportedRegistrationAsync(
        string distroName,
        GatewayRollbackPointManifest manifest,
        CancellationToken cancellationToken)
    {
        var configure = await _commandRunner.ConfigureDistroRegistrationAsync(
            distroName,
            manifest.RegistrationDefaultUid,
            manifest.RegistrationFlags,
            cancellationToken).ConfigureAwait(false);
        if (!configure.Success)
            return Fail(manifest, GatewayRollbackOperationState.ImportPending, "restore_registration_configure_failed", configure.ExitCode, preservePhase: true);

        var registration = await _commandRunner.GetDistroConfigurationAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!registration.Success ||
            registration.Configuration is null ||
            registration.Configuration.Version != manifest.RegistrationVersion ||
            registration.Configuration.DefaultUid != manifest.RegistrationDefaultUid ||
            registration.Configuration.Flags != manifest.RegistrationFlags)
        {
            return Fail(manifest, GatewayRollbackOperationState.ImportPending, "restored_registration_mismatch", preservePhase: true);
        }

        var importedIdentity = await ProbeInternalIdentityHashAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(importedIdentity, manifest.InternalIdentitySha256, StringComparison.Ordinal))
            return Fail(manifest, GatewayRollbackOperationState.SameNameCollision, "restored_identity_mismatch", preservePhase: true);

        var defaultUser = await ProbeDefaultUserAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (defaultUser is null ||
            defaultUser.Name != manifest.DefaultUserName ||
            defaultUser.Uid != manifest.DefaultUserUid)
        {
            return Fail(manifest, GatewayRollbackOperationState.ImportPending, "restored_default_user_mismatch", preservePhase: true);
        }

        var imported = UpdateManifest(manifest, GatewayRollbackPointPhase.Imported);
        return new(GatewayRollbackOperationState.Restored, imported);
    }

    private async Task<string?> ProbeExactVersionAsync(string distroName, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunInDistroAsync(
            distroName, GatewayVersionAlignmentCommandBuilder.BuildProbe(), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return null;
        var match = VersionOutputRegex().Match(result.StandardOutput ?? string.Empty);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private async Task<(bool Success, IReadOnlyList<string> Names, int ExitCode)> ListDistroNamesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(["--list", "--quiet"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return (false, Array.Empty<string>(), result.ExitCode);
        var names = result.StandardOutput
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0)
            .ToArray();
        return (true, names, result.ExitCode);
    }

    private async Task<VhdVerification> VerifyVhdAsync(
        string path,
        string? expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new(false, null, 0, "vhd_missing");

        var header = new byte[8];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = stream.Length;
        if (size < header.Length || (expectedSize.HasValue && size != expectedSize.Value))
            return new(false, null, size, "vhd_size_mismatch");

        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read != header.Length || !string.Equals(Encoding.ASCII.GetString(header), VhdxSignature, StringComparison.Ordinal))
            return new(false, null, size, "vhdx_signature_invalid");

        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (stream.Length != size)
            return new(false, hash, stream.Length, "vhd_size_changed");
        if (expectedSha256 is not null && !string.Equals(hash, expectedSha256, StringComparison.Ordinal))
            return new(false, hash, size, "vhd_hash_mismatch");
        return new(true, hash, size, null);
    }

    private static async Task CopyAndFlushAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private void WriteManifest(GatewayRollbackPointManifest manifest)
    {
        var directory = GetPointDirectory(manifest.Id);
        EnsurePrivateDirectory(directory);
        var path = Path.Combine(directory, ManifestFileName);
        var temp = path + ".tmp";
        DeleteGeneratedFile(temp, directory);
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest, _jsonOptions));
        using (var stream = new FileStream(
                   temp,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temp, path, overwrite: false);

        using var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        committed.Flush(flushToDisk: true);
    }

    private bool TryReadManifest(string path, out GatewayRollbackPointManifest? manifest)
    {
        manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<GatewayRollbackPointManifest>(File.ReadAllText(path), _jsonOptions);
            return manifest is not null && PointIdRegex().IsMatch(manifest.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private bool TryLoadPoint(string pointId, out GatewayRollbackPointManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(pointId) || !PointIdRegex().IsMatch(pointId))
            return false;
        return TryReadManifest(Path.Combine(GetPointDirectory(pointId), ManifestFileName), out manifest);
    }

    private GatewayRollbackPointManifest[] LoadOwnedManifests() =>
        List().Select(info => TryLoadPoint(info.Id, out var manifest) ? manifest : null)
            .Where(manifest => manifest is not null && IsManifestOwned(manifest))
            .Cast<GatewayRollbackPointManifest>()
            .ToArray();

    private bool DeletePointFilesOnly(string pointId)
    {
        var directory = GetPointDirectory(pointId);
        if (!Directory.Exists(directory) ||
            !HasSafePathBoundary(_pointsRoot, directory))
            return false;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(Path.Combine(directory, ManifestFileName)),
            NormalizePath(Path.Combine(directory, RollbackVhdFileName))
        };
        var entries = Directory.GetFileSystemEntries(directory).Select(NormalizePath).ToArray();
        if (entries.Any(entry => !allowed.Contains(entry) || IsReparsePoint(entry)))
            return false;
        foreach (var entry in entries)
            File.Delete(entry);
        Directory.Delete(directory, recursive: false);
        return true;
    }

    private bool IsManifestOwned(GatewayRollbackPointManifest manifest) =>
        manifest.SchemaVersion == 3 &&
        IsOwnedDistro(manifest.DistroName) &&
        PointIdRegex().IsMatch(manifest.Id) &&
        ExactVersionRegex().IsMatch(manifest.OpenClawVersion) &&
        ExactVersionRegex().IsMatch(manifest.TargetOpenClawVersion) &&
        Sha256Regex().IsMatch(manifest.InternalIdentitySha256) &&
        manifest.RegistrationVersion == 2 &&
        DefaultUserNameRegex().IsMatch(manifest.DefaultUserName) &&
        (manifest.NodeCommandAllowSnapshotJson is null ||
         IsCompleteCommandArrayJson(manifest.NodeCommandAllowSnapshotJson)) &&
        (string.IsNullOrEmpty(manifest.VhdSha256) || Sha256Regex().IsMatch(manifest.VhdSha256));

    private static bool IsCompleteCommandArrayJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().All(item =>
                       item.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(item.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsOwnedDistro(string? distroName) =>
        string.Equals(distroName?.Trim(), _ownedDistroName, StringComparison.Ordinal);

    private string GetPointDirectory(string pointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointId);
        if (!PointIdRegex().IsMatch(pointId))
            throw new ArgumentException("Invalid rollback point id.", nameof(pointId));
        var path = NormalizePath(Path.Combine(_pointsRoot, pointId));
        if (!string.Equals(Path.GetDirectoryName(path), _pointsRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Rollback point escaped its owned root.");
        return path;
    }

    private string GetRollbackVhdPath(string pointId) => Path.Combine(GetPointDirectory(pointId), RollbackVhdFileName);
    private string GetStageVhdPath(string pointId) => NormalizePath(Path.Combine(_stagingRoot, $"{pointId}.vhdx"));
    private string GetInstallDirectory() => NormalizePath(Path.Combine(_localDataRoot, "wsl", _ownedDistroName));

    private static GatewayRollbackPointInfo ToInfo(GatewayRollbackPointManifest manifest) => new(
        manifest.Id,
        manifest.DistroName,
        manifest.OpenClawVersion,
        manifest.CreatedAtUtc,
        manifest.VerificationStatus,
        manifest.Phase,
        manifest.VhdSizeBytes,
        manifest.RestoreEligible && manifest.VerificationStatus == GatewayRollbackPointVerificationStatus.Verified,
        manifest.LastFailureCode);

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        McpAuthToken.TryRestrictDataDirectoryAcl(path);
    }

    private static void TryDeleteGeneratedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool DeleteGeneratedFile(string path, string ownedRoot)
    {
        if (!File.Exists(path))
            return true;
        if (!HasSafePathBoundary(ownedRoot, path) || IsReparsePoint(path))
            return false;
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasSafePathBoundary(string root, string target)
    {
        var normalizedRoot = NormalizePath(root);
        var normalizedTarget = NormalizePath(target);
        if (!string.Equals(normalizedTarget, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var current = normalizedRoot;
        if (PathExists(current) && IsReparsePoint(current))
            return false;

        var relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (PathExists(current) && IsReparsePoint(current))
                return false;
        }
        return true;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            fullPath = @"\\" + fullPath[8..];
        else if (fullPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            fullPath = fullPath[4..];
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private sealed record VhdVerification(bool Verified, string? Sha256, long SizeBytes, string? FailureCode);
    private sealed record DefaultUserIdentity(string Name, uint Uid);

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DistroNameRegex();

    [GeneratedRegex(@"\A\d{8}T\d{9}Z-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PointIdRegex();

    [GeneratedRegex(@"\A[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MachineIdRegex();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"\A[a-z_][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultUserNameRegex();

    [GeneratedRegex(@"\A(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersionRegex();

    [GeneratedRegex(
        @"\A\s*(?:OpenClaw\s+)?(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)(?:\s+\([^\r\n()]+\))?\s*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionOutputRegex();
}
