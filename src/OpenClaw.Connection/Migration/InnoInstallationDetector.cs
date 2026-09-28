using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Collects read-only evidence for the default, same-user production installation.
/// Payload presence establishes coherence, not uninstaller provenance or migration eligibility.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoInstallationDetector : IInnoInstallationDetector
{
    internal const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1";

    private readonly string _installDirectory;
    private readonly IOpenClawLogger _logger;
    private readonly IInnoInstallationReadSource _source;

    public InnoInstallationDetector(string localAppDataDirectory, IOpenClawLogger logger)
        : this(localAppDataDirectory, logger, new InnoInstallationReadSource())
    {
    }

    internal InnoInstallationDetector(
        string localAppDataDirectory, IOpenClawLogger logger, IInnoInstallationReadSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        if (!Path.IsPathFullyQualified(localAppDataDirectory))
            throw new ArgumentException("Local AppData must be an absolute path.", nameof(localAppDataDirectory));
        _installDirectory = Path.GetFullPath(Path.Combine(localAppDataDirectory, "OpenClawTray"));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public InnoInstallationDetection Detect()
    {
        try
        {
            // HKCU can alias the same registration in both views. Inspect HKLM even
            // when HKCU is valid: machine-wide or conflicting evidence must block.
            var user64 = _source.ReadRegistration(RegistryHive.CurrentUser, RegistryView.Registry64);
            var user32 = _source.ReadRegistration(RegistryHive.CurrentUser, RegistryView.Registry32);
            var machine64 = _source.ReadRegistration(RegistryHive.LocalMachine, RegistryView.Registry64);
            var machine32 = _source.ReadRegistration(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (machine64 is not null || machine32 is not null)
                return Unsupported("A machine-wide production Inno registration is present.");
            if (user64 is null && user32 is null)
                return new(InnoInstallationStatus.NotInstalled);
            if (user64 is not null && user32 is not null && user64 != user32)
                return Unsupported("Production Inno registrations disagree between registry views.");

            var registration = user64 ?? user32!;
            if (!MigrationVersionPolicy.TryParseReleaseVersion(registration.DisplayVersion, out var version))
                return Unsupported("The Inno DisplayVersion is not a stable numeric release version.");
            // Inno's default UninstallDisplayName includes the unnormalized AppVersion.
            if (registration.DisplayName != $"OpenClaw Companion version {registration.DisplayVersion}" ||
                registration.Publisher != "OpenClaw Foundation")
                return Unsupported("The Inno registration does not identify the production publisher and application.");
            if (!IsDefaultInstallPath(registration.InstallLocation))
                return Unsupported("The Inno installation is not at the canonical default per-user location.");

            var executable = Path.Combine(_installDirectory, "OpenClaw.Tray.WinUI.exe");
            var uninstaller = Path.Combine(_installDirectory, "unins000.exe");
            if (!string.Equals(registration.UninstallString, $"\"{uninstaller}\"", StringComparison.OrdinalIgnoreCase))
                return Unsupported("The Inno uninstall command must contain only the quoted canonical unins000.exe path.");

            foreach (var name in new[]
            {
                "OpenClaw.Tray.WinUI.exe", "unins000.exe", "app-identity.txt",
                "Test-InnoMigration.ps1", "MigrationRecordCodec.cs", "Uninstall-LocalGateway.ps1"
            })
            {
                if (!_source.IsOrdinaryFile(Path.Combine(_installDirectory, name)))
                    return Unsupported($"The required Inno payload {name} is missing or is not an ordinary file.");
            }

            if (_source.ReadIdentity(Path.Combine(_installDirectory, "app-identity.txt")) is not
                ("release" or "release\n" or "release\r\n"))
                return Unsupported("The installed application identity is not release.");
            var binary = _source.ReadExecutable(executable);
            var architecture = binary.Machine switch
            {
                Machine.Amd64 => "x64",
                Machine.Arm64 => "arm64",
                _ => null
            };
            if (architecture is null)
                return Unsupported("The installed executable has an unsupported PE architecture.");
            if (!MigrationVersionPolicy.TryParseReleaseVersion(binary.Version, out var binaryVersion) ||
                binaryVersion != version)
                return Unsupported("The executable file version does not match the registered release version.");

            return new(InnoInstallationStatus.Detected,
                new(_installDirectory, executable, uninstaller, architecture, version));
        }
        catch (InvalidDataException ex)
        {
            return Unsupported(ex.Message);
        }
        catch (BadImageFormatException)
        {
            return Unsupported("The installed executable is not a valid native PE executable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            const string reason = "The Inno installation could not be inspected.";
            _logger.Error(reason, ex);
            return new(InnoInstallationStatus.InspectionFailed, Reason: reason);
        }
    }

    private bool IsDefaultInstallPath(string? path)
    {
        // Inno writes InstallLocation with a trailing separator. Do not accept
        // relative paths, dot segments, expanded variables, or alternate spellings.
        return path is not null &&
            (string.Equals(path, _installDirectory, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(path, _installDirectory + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private InnoInstallationDetection Unsupported(string reason)
    {
        _logger.Warn(reason);
        return new(InnoInstallationStatus.Unsupported, Reason: reason);
    }
}

internal sealed record InnoInstallationRegistration(
    string? InstallLocation, string? DisplayVersion, string? Publisher,
    string? DisplayName, string? UninstallString);

internal sealed record InnoInstallationExecutable(Machine Machine, string? Version);

internal interface IInnoInstallationReadSource
{
    InnoInstallationRegistration? ReadRegistration(RegistryHive hive, RegistryView view);
    bool IsOrdinaryFile(string path);
    string ReadIdentity(string path);
    InnoInstallationExecutable ReadExecutable(string path);
}

[SupportedOSPlatform("windows")]
internal sealed class InnoInstallationReadSource : IInnoInstallationReadSource
{
    private readonly Func<RegistryHive, RegistryView, RegistryKey?> _openRegistration;

    internal InnoInstallationReadSource(Func<RegistryHive, RegistryView, RegistryKey?>? openRegistration = null)
    {
        _openRegistration = openRegistration ?? OpenRegistration;
    }

    public InnoInstallationRegistration? ReadRegistration(RegistryHive hive, RegistryView view)
    {
        using var key = _openRegistration(hive, view);
        if (key is null)
            return null;
        return new(ReadString(key, "InstallLocation"), ReadString(key, "DisplayVersion"),
            ReadString(key, "Publisher"), ReadString(key, "DisplayName"), ReadString(key, "UninstallString"));
    }

    public bool IsOrdinaryFile(string path)
    {
        MigrationRecordCodec.RejectReparsePoints(path);
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public string ReadIdentity(string path)
    {
        MigrationRecordCodec.RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32)
            throw new InvalidDataException("The installed application identity marker is malformed.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public InnoInstallationExecutable ReadExecutable(string path)
    {
        MigrationRecordCodec.RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        if (headers.PEHeader is null || headers.CorHeader is not null ||
            (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
            (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0)
            throw new InvalidDataException("The installed application is not a native PE executable.");
        if (headers.CoffHeader.Machine is Machine.Amd64 or Machine.Arm64 &&
            headers.PEHeader.Magic != PEMagic.PE32Plus)
            throw new InvalidDataException("The installed executable PE headers are inconsistent.");
        return new(headers.CoffHeader.Machine, FileVersionInfo.GetVersionInfo(path).FileVersion);
    }

    private static RegistryKey? OpenRegistration(RegistryHive hive, RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        return root.OpenSubKey(InnoInstallationDetector.UninstallKey, writable: false);
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;
        if (key.GetValueKind(name) != RegistryValueKind.String || value is not string text)
            throw new InvalidDataException($"The Inno registry value {name} must be a plain string.");
        return text;
    }
}
