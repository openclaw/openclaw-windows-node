using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection;

/// <summary>Stages profile deletion so a rejected registry write can restore the original identity.</summary>
public static class GatewayIdentityRemoval
{
    /// <summary>
    /// True when an earlier removal left this gateway's profile in a staging folder. Reconnecting then would
    /// create a fresh profile and could pick weaker credentials, so callers must not restore the connection.
    /// </summary>
    public static bool HasPendingProfileStage(GatewayRegistry registry, string gatewayId)
    {
        NativeGatewayPaths.ValidateGatewayId(gatewayId);
        var dataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(registry.GetIdentityDirectory(gatewayId))!)!;
        var stagingPrefix = $"gateway-removal-{gatewayId}-";
        return Directory.Exists(dataDirectory) &&
            Directory.EnumerateDirectories(dataDirectory, stagingPrefix + "*").Any(stage =>
                Guid.TryParseExact(Path.GetFileName(stage)[stagingPrefix.Length..], "N", out _));
    }

    public static void Remove(GatewayRegistry registry, GatewayRecord expected)
    {
        NativeGatewayPaths.ValidateGatewayId(expected.Id);
        var snapshot = registry.CapturePersistedSnapshot();
        if (!LocalGatewaySettings.IsSameTarget(expected, snapshot.Records.SingleOrDefault(record => record.Id == expected.Id)))
            throw new InvalidOperationException("The Gateway connection changed. Its profile was not deleted.");
        var directory = registry.GetIdentityDirectory(expected.Id);
        var dataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!;
        var stagingPrefix = $"gateway-removal-{expected.Id}-";
        foreach (var previousStage in Directory.EnumerateDirectories(dataDirectory, stagingPrefix + "*"))
        {
            var suffix = Path.GetFileName(previousStage)[stagingPrefix.Length..];
            if (Guid.TryParseExact(suffix, "N", out _))
                throw new IOException(
                    $"A previous Gateway removal left profile data at '{previousStage}'. " +
                    "Inspect and resolve that folder before retrying. The saved connection was retained.");
        }
        // Keep incomplete cleanup outside gateways/* so startup cannot adopt its old token.
        var staged = Path.Combine(dataDirectory, $"{stagingPrefix}{Guid.NewGuid():N}");
        var moved = false;
        if (Directory.Exists(directory))
        {
            // The profile root must be real. Child links are removed as links, never followed.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Gateway identity directory is redirected. Its profile was not deleted.");
            Directory.Move(directory, staged);
            moved = true;
        }
        try
        {
            registry.ReplaceSnapshotAndSave(snapshot, new(
                snapshot.Records.Where(record => record.Id != expected.Id).ToArray(),
                snapshot.ActiveId == expected.Id ? null : snapshot.ActiveId),
                suppressLegacyGatewayUrl: expected.Url);
        }
        catch (Exception failure)
        {
            if (moved)
            {
                try { Directory.Move(staged, directory); }
                catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"The connection was retained, but its profile could not be restored from '{staged}'. Do not reconnect until the profile is restored.",
                        new AggregateException(failure, restoreFailure));
                }
            }
            throw;
        }
        if (!moved) return;
        try { DeleteProfileTree(staged); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The Gateway connection was removed, but its old profile remains at '{staged}'. Close programs using those files, then remove that folder.", exception);
        }
    }

    private static void DeleteProfileTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            if ((attributes & FileAttributes.Directory) == 0)
                File.Delete(entry);
            else if ((attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(entry);
            else
                DeleteProfileTree(entry);
        }
        var directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(directory);
    }
}
