using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal static class ManagedDistroOwnership
{
    private const string MarkerFileName = "setup-managed-distro.json";

    internal static bool HasEvidence(
        string dataDir,
        string localDataDir,
        string distroName)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        if (registry.GetAll().Any(record =>
                record.IsLocal &&
                record.SshTunnel is null &&
                string.Equals(
                    GatewayRecordEditing.ResolveManagedDistroName(record),
                    distroName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HasPathBoundMarkerEvidence(localDataDir, distroName))
            return true;

        var setupStatePath = Path.Combine(localDataDir, "setup-state.json");
        if (!File.Exists(setupStatePath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(setupStatePath));
            return document.RootElement.TryGetProperty("DistroName", out var distroElement) &&
                string.Equals(
                    distroElement.GetString(),
                    distroName,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Setup ownership evidence could not be read: {ex.Message}");
            return false;
        }
    }

    internal static bool HasRegisteredDistroEvidence(
        string dataDir,
        string localDataDir,
        string distroName,
        string expectedInstallPath,
        IWslRegistrationInspector registrationInspector,
        [NotNullWhen(false)] out string? failure)
    {
        if (!HasEvidence(dataDir, localDataDir, distroName))
        {
            failure = "No durable OpenClaw ownership evidence matches the distro name.";
            return false;
        }

        var registration = registrationInspector.Inspect(distroName);
        if (registration.Status != WslRegistrationInspectionStatus.Found ||
            string.IsNullOrWhiteSpace(registration.BasePath))
        {
            failure = registration.Detail ??
                "The current WSL registration could not be bound to OpenClaw's managed install path.";
            return false;
        }

        if (!DistroInstallPathPolicy.PathsReferToSameLocation(
                registration.BasePath,
                expectedInstallPath))
        {
            failure = "The current WSL registration is bound to a different base path.";
            return false;
        }

        failure = null;
        return true;
    }

    internal static async Task WriteMarkerAsync(
        string localDataDir,
        string distroName,
        string installPath,
        CancellationToken cancellationToken)
    {
        var marker = new ManagedDistroMarker(
            SchemaVersion: 1,
            DistroName: distroName,
            InstallPath: Path.GetFullPath(installPath),
            CreatedAtUtc: DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(marker, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(
            MarkerPath(localDataDir),
            json,
            cancellationToken);
    }

    internal static bool DeleteMarker(
        string localDataDir,
        string distroName,
        string expectedInstallPath)
    {
        var markerPath = MarkerPath(localDataDir);
        if (!TryReadMarker(localDataDir, out var marker) ||
            !MarkerMatches(
                localDataDir,
                distroName,
                expectedInstallPath,
                marker))
        {
            return false;
        }

        try
        {
            File.Delete(markerPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Managed distro marker could not be deleted: {ex.Message}");
            return false;
        }
    }

    internal static bool HasPathBoundMarkerEvidence(
        string localDataDir,
        string distroName)
    {
        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                localDataDir,
                distroName,
                out var expectedInstallPath,
                out _) ||
            !TryReadMarker(localDataDir, out var marker))
        {
            return false;
        }

        return MarkerMatches(
            localDataDir,
            distroName,
            expectedInstallPath,
            marker);
    }

    private static bool MarkerMatches(
        string localDataDir,
        string distroName,
        string expectedInstallPath,
        ManagedDistroMarker marker)
    {
        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                localDataDir,
                distroName,
                out var policyInstallPath,
                out _))
        {
            return false;
        }

        try
        {
            return string.Equals(
                    marker.DistroName,
                    distroName,
                    StringComparison.OrdinalIgnoreCase) &&
                DistroInstallPathPolicy.PathsReferToSameLocation(
                    expectedInstallPath,
                    policyInstallPath) &&
                DistroInstallPathPolicy.PathsReferToSameLocation(
                    marker.InstallPath,
                    policyInstallPath);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Managed distro marker could not be validated: {ex.Message}");
            return false;
        }
    }

    private static bool TryReadMarker(
        string localDataDir,
        [NotNullWhen(true)] out ManagedDistroMarker? marker)
    {
        var markerPath = MarkerPath(localDataDir);
        if (!File.Exists(markerPath))
        {
            marker = null;
            return false;
        }

        try
        {
            marker = JsonSerializer.Deserialize<ManagedDistroMarker>(
                File.ReadAllText(markerPath),
                SetupConfig.JsonOptions);
            return marker is { SchemaVersion: 1 };
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Managed distro marker could not be read: {ex.Message}");
            marker = null;
            return false;
        }
    }

    private static string MarkerPath(string localDataDir) =>
        Path.Combine(localDataDir, MarkerFileName);

    private sealed record ManagedDistroMarker(
        int SchemaVersion,
        string DistroName,
        string InstallPath,
        DateTimeOffset CreatedAtUtc);
}
