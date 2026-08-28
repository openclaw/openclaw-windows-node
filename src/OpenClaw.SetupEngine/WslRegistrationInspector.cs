using Microsoft.Win32;
using System.Security;

namespace OpenClaw.SetupEngine;

internal interface IWslRegistrationInspector
{
    WslRegistrationInspection Inspect(string distroName);
}

internal interface IWslRegistrationSource
{
    WslRegistrationSnapshot ReadAll();
}

internal enum WslRegistrationInspectionStatus
{
    Found,
    NotFound,
    Unavailable,
    Duplicate,
    Malformed,
}

internal sealed record WslRegistrationInspection(
    WslRegistrationInspectionStatus Status,
    string? RegistrationId = null,
    string? BasePath = null,
    string? Detail = null);

internal sealed record RawWslRegistration(
    string RegistrationId,
    object? DistributionName,
    object? BasePath);

internal sealed record WslRegistrationSnapshot(
    bool IsComplete,
    IReadOnlyList<RawWslRegistration> Registrations,
    string? Failure = null);

internal sealed class WindowsWslRegistrationInspector : IWslRegistrationInspector
{
    private readonly IWslRegistrationSource _source;

    public WindowsWslRegistrationInspector()
        : this(new RegistryWslRegistrationSource())
    {
    }

    internal WindowsWslRegistrationInspector(IWslRegistrationSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public WslRegistrationInspection Inspect(string distroName)
    {
        if (string.IsNullOrWhiteSpace(distroName))
        {
            return new WslRegistrationInspection(
                WslRegistrationInspectionStatus.Malformed,
                Detail: "The requested WSL distro name is invalid.");
        }

        var snapshot = _source.ReadAll();
        if (!snapshot.IsComplete)
        {
            return new WslRegistrationInspection(
                WslRegistrationInspectionStatus.Unavailable,
                Detail: snapshot.Failure ?? "The current-user WSL registration inventory is unavailable.");
        }

        var matches = new List<WslRegistrationInspection>();
        foreach (var registration in snapshot.Registrations)
        {
            if (!Guid.TryParse(registration.RegistrationId, out _) ||
                registration.DistributionName is not string registeredName ||
                string.IsNullOrWhiteSpace(registeredName))
            {
                return new WslRegistrationInspection(
                    WslRegistrationInspectionStatus.Malformed,
                    Detail: "The current-user WSL registration inventory contains malformed identity metadata.");
            }

            if (!string.Equals(registeredName, distroName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (registration.BasePath is not string basePath ||
                !DistroInstallPathPolicy.TryCanonicalizeAbsolutePath(
                    basePath,
                    out var canonicalBasePath,
                    out _))
            {
                return new WslRegistrationInspection(
                    WslRegistrationInspectionStatus.Malformed,
                    Detail: "The matching WSL registration has a missing or malformed base path.");
            }

            matches.Add(new WslRegistrationInspection(
                WslRegistrationInspectionStatus.Found,
                registration.RegistrationId,
                canonicalBasePath));
        }

        return matches.Count switch
        {
            0 => new WslRegistrationInspection(
                WslRegistrationInspectionStatus.NotFound,
                Detail: "No exact current-user WSL registration was found."),
            1 => matches[0],
            _ => new WslRegistrationInspection(
                WslRegistrationInspectionStatus.Duplicate,
                Detail: "Multiple current-user WSL registrations use the requested distro name."),
        };
    }
}

internal sealed class RegistryWslRegistrationSource : IWslRegistrationSource
{
    private const string LxssRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public WslRegistrationSnapshot ReadAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WslRegistrationSnapshot(
                false,
                [],
                "The current-user WSL registration inventory is available only on Windows.");
        }

        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssRegistryPath, writable: false);
            if (lxss is null)
                return new WslRegistrationSnapshot(true, []);

            var registrations = new List<RawWslRegistration>();
            foreach (var registrationId in lxss.GetSubKeyNames())
            {
                using var registration = lxss.OpenSubKey(registrationId, writable: false);
                if (registration is null)
                {
                    return new WslRegistrationSnapshot(
                        false,
                        [],
                        "A current-user WSL registration could not be opened.");
                }

                registrations.Add(new RawWslRegistration(
                    registrationId,
                    registration.GetValue(
                        "DistributionName",
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames),
                    registration.GetValue(
                        "BasePath",
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames)));
            }

            return new WslRegistrationSnapshot(true, registrations);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or SecurityException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Current-user WSL registration inventory could not be read: {ex.Message}");
            return new WslRegistrationSnapshot(
                false,
                [],
                "The current-user WSL registration inventory could not be read.");
        }
    }
}
