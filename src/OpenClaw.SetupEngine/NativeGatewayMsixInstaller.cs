using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Temporary local-package handoff until the Gateway Store listing is available.
/// Windows App Installer owns signature validation, installation consent and deployment.
/// </summary>
public sealed class NativeGatewayMsixInstaller(string? packagePath = null)
{
    public const string PackagePathEnvironmentVariable = "OPENCLAW_GATEWAY_MSIX_PATH";
    public const string PackageName = "OpenClaw.Gateway";
    public const string Publisher =
        "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US";

    public string? PackagePath { get; } =
        packagePath ?? Environment.GetEnvironmentVariable(PackagePathEnvironmentVariable);

    public async Task OpenAsync(
        Func<string, CancellationToken, Task<bool>> openInstaller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackagePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"Set {PackagePathEnvironmentVariable} to the local Gateway MSIX, " +
                "then restart Companion and retry setup. A Store installation link is not available yet.");
        if (!File.Exists(path))
            throw new FileNotFoundException("The local Gateway MSIX was not found.", path);

        using (var archive = ZipFile.OpenRead(path))
        {
            var entry = archive.GetEntry("AppxManifest.xml")
                ?? throw new InvalidDataException("The local MSIX has no package manifest.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_048_576,
            });
            var document = XDocument.Load(reader);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var identity = document.Root?.Element(ns + "Identity");
            if ((string?)identity?.Attribute("Name") != PackageName ||
                (string?)identity?.Attribute("Publisher") != Publisher ||
                (string?)identity?.Attribute("ProcessorArchitecture") != "arm64")
            {
                throw new InvalidDataException(
                    "The local MSIX is not the expected OpenClaw Foundation ARM64 Gateway package.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await openInstaller(path, cancellationToken))
            throw new InvalidOperationException(
                "Windows App Installer could not be opened for the local Gateway MSIX.");
    }
}
