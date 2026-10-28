namespace OpenClaw.Shared.Systems;

public enum BackupHealth
{
    Ok,
    Failed,
    PushFailed,
}

/// <summary>One backup target projected for display. A null <see cref="Error"/> on a failed
/// target means the UI shows the localized hint for its <see cref="Health"/>.</summary>
public sealed record BackupTargetView(
    string Name,
    BackupKind Kind,
    BackupHealth Health,
    DateTimeOffset? LastSuccessAt,
    long? SizeBytes,
    DateTimeOffset? NextRunAt,
    string Destination,
    string? Namespace,
    string? Error);

public sealed record BackupsView(
    IReadOnlyList<BackupTargetView> Targets,
    IReadOnlyList<BackupSchedule> UnmatchedSchedules,
    IReadOnlyList<StorageLocation> Locations)
{
    public bool IsEmpty => Targets.Count == 0;
}

public static class BackupsPresentation
{
    public static BackupsView Project(BackupStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var targets = status.Targets.Select(target => ProjectTarget(target, status)).ToArray();
        var unmatched = status.Schedules
            .Where(schedule => schedule.Enabled && !status.Targets.Any(target => Matches(target, schedule)))
            .ToArray();
        return new BackupsView(targets, unmatched, status.Locations);
    }

    public static bool Matches(BackupTarget target, BackupSchedule schedule) =>
        schedule.Enabled &&
        schedule.Target == target.Target &&
        (schedule.Mode == "offsite"
            ? target.Kind == BackupKind.Archive && schedule.Namespace == target.Namespace
            : target.Kind == BackupKind.Git);

    private static BackupTargetView ProjectTarget(BackupTarget target, BackupStatus status)
    {
        var latest = target.Latest;
        var pushFailed = target.Kind == BackupKind.Git && latest.Ok && latest.PushFailed;
        var failed = !latest.Ok || pushFailed;
        var storedLocation = latest.Location ?? target.LatestOk?.Location;
        var locationName = storedLocation?.Name ?? (target.Kind == BackupKind.Archive ? target.Target : null);
        var location = status.Locations.FirstOrDefault(entry => entry.Name == locationName);
        var nextRun = status.Schedules
            .Where(schedule => Matches(target, schedule) && schedule.NextRunAt is not null)
            .Select(schedule => schedule.NextRunAt!.Value)
            .DefaultIfEmpty()
            .Min();
        return new BackupTargetView(
            storedLocation?.Name ?? target.Target,
            target.Kind,
            pushFailed ? BackupHealth.PushFailed : failed ? BackupHealth.Failed : BackupHealth.Ok,
            target.LatestOk?.CreatedAt,
            target.LatestOk?.Location?.StoredBytes ?? target.LatestOk?.Bytes,
            nextRun == default ? null : nextRun,
            location?.DisplayTarget ?? target.Target,
            target.Namespace,
            failed ? latest.Error : null);
    }
}
