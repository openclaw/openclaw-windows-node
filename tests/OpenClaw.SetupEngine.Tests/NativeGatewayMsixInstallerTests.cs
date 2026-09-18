using System.IO.Compression;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class NativeGatewayMsixInstallerTests
{
    [Fact]
    public async Task DefaultSource_UsesConfiguredLocalPackage()
    {
        using var temp = new TempDirectory();
        var path = CreatePackage(temp);
        var previous = Environment.GetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, path);
            var installer = new NativeGatewayMsixInstaller();
            Assert.Equal(path, installer.PackagePath);
            var calls = 0;
            await installer.OpenAsync((received, _) =>
            {
                Assert.Equal(path, received);
                calls++;
                return Task.FromResult(true);
            }, CancellationToken.None);
            Assert.Equal(1, calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UnconfiguredSource_ExplainsConfigurationWithoutOpeningInstaller(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, value);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new NativeGatewayMsixInstaller().OpenAsync(MustNotLaunch, CancellationToken.None));
            Assert.Contains(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, error.Message);
            Assert.Contains("restart Companion", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void ExplicitSource_TakesPrecedenceOverEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, "environment.msix");
            Assert.Equal("explicit.msix", new NativeGatewayMsixInstaller("explicit.msix").PackagePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayMsixInstaller.PackagePathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Open_HandsTheLocalPackageToTheInstallerExactlyOnce()
    {
        using var temp = new TempDirectory();
        var path = CreatePackage(temp);
        var calls = 0;
        await new NativeGatewayMsixInstaller(path).OpenAsync((received, ct) =>
        {
            Assert.Equal(path, received);
            Assert.False(ct.IsCancellationRequested);
            calls++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingFile_DoesNotOpenAnInstallerOrDownloadAnything()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new NativeGatewayMsixInstaller(temp.Combine("missing.msix")).OpenAsync(
                MustNotLaunch, CancellationToken.None));
    }

    [Theory]
    [InlineData("Another.Package", NativeGatewayMsixInstaller.Publisher, "arm64")]
    [InlineData(NativeGatewayMsixInstaller.PackageName, "CN=Unrelated", "arm64")]
    [InlineData(NativeGatewayMsixInstaller.PackageName, NativeGatewayMsixInstaller.Publisher, "x64")]
    public async Task WrongPackage_RejectsBeforeOpeningInstaller(string name, string publisher, string architecture)
    {
        using var temp = new TempDirectory();
        var path = CreatePackage(temp, name, publisher, architecture);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NativeGatewayMsixInstaller(path).OpenAsync(MustNotLaunch, CancellationToken.None));
    }

    [Fact]
    public async Task ShellDeclinesLaunch_IsNotReportedAsInstallationSuccess()
    {
        using var temp = new TempDirectory();
        var path = CreatePackage(temp);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller(path).OpenAsync((_, _) => Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_DoesNotOpenInstaller()
    {
        using var temp = new TempDirectory();
        var path = CreatePackage(temp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativeGatewayMsixInstaller(path).OpenAsync(MustNotLaunch, cts.Token));
    }

    private static Task<bool> MustNotLaunch(string path, CancellationToken ct) =>
        throw new Xunit.Sdk.XunitException("The installer must not be invoked.");

    private static string CreatePackage(
        TempDirectory temp,
        string name = NativeGatewayMsixInstaller.PackageName,
        string publisher = NativeGatewayMsixInstaller.Publisher,
        string architecture = "arm64")
    {
        var path = temp.Combine("synthetic.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry("AppxManifest.xml").Open();
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        new XDocument(new XElement(ns + "Package",
            new XElement(ns + "Identity",
                new XAttribute("Name", name),
                new XAttribute("Publisher", publisher),
                new XAttribute("ProcessorArchitecture", architecture)))).Save(stream);
        return path;
    }
}
