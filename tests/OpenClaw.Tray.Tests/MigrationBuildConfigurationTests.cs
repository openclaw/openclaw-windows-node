using System.Diagnostics;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class MigrationBuildConfigurationTests
{
    [Theory]
    [InlineData("win-x64", false)]
    [InlineData("win-x64", true)]
    [InlineData("win-arm64", false)]
    [InlineData("win-arm64", true)]
    public async Task Production_InnoAndStoreShareThePinnedReleasePolicy(string runtime, bool packaged)
    {
        var result = await EvaluateAsync(
            ("MigrationProductionEnabled", "true"),
            ("Version", "2027.1.1"),
            ("RuntimeIdentifier", runtime),
            ("PackageMsix", packaged.ToString()));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PRODUCTION_MIGRATION", result.Output);
        Assert.Contains("MigrationStoreProductId=9NFPR3BGDRR5", result.Output);
        Assert.Contains("MigrationMinimumSourceVersion=2026.9.5.0", result.Output);
        Assert.DoesNotContain("MigrationMinimumSourceVersion=2027", result.Output);
        Assert.DoesNotContain("MIGRATION_PREVIEW", result.Output);
    }

    [Fact]
    public async Task PinnedRelease_DefaultsEnableTheShippingPolicy()
    {
        var result = await EvaluateAsync(("Version", "2027.1.1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PRODUCTION_MIGRATION", result.Output);
        Assert.Contains("MigrationStoreProductId=9NFPR3BGDRR5", result.Output);
        Assert.Contains("MigrationMinimumSourceVersion=2026.9.5.0", result.Output);
        Assert.DoesNotContain("MigrationMinimumSourceVersion=2027", result.Output);
        Assert.DoesNotContain("MIGRATION_PREVIEW", result.Output);
    }

    [Theory]
    [InlineData("MigrationMinimumSourceVersion", "", "MigrationMinimumSourceVersion")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9", "MigrationMinimumSourceVersion")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9.5-alpha.1", "MigrationMinimumSourceVersion")]
    [InlineData("MigrationMinimumSourceVersion", "0.0.0", "zero version")]
    [InlineData("MigrationMinimumSourceVersion", "0.0.0.0", "zero version")]
    [InlineData("MigrationMinimumSourceVersion", "2147483648.1.1", "Version")]
    [InlineData("MigrationStoreProductId", "not-a-product", "MigrationStoreProductId")]
    [InlineData("RuntimeIdentifier", "win-x86", "win-x64 or win-arm64")]
    [InlineData("MigrationProductionEnabled", "yes", "true or false")]
    public async Task Production_RejectsIncompleteOrInvalidPolicy(string key, string value, string error)
    {
        var properties = new Dictionary<string, string>
        {
            ["MigrationProductionEnabled"] = "true",
            ["MigrationMinimumSourceVersion"] = "2026.9.5.0",
            [key] = value
        };
        var result = await EvaluateAsync(properties.Select(pair => (pair.Key, pair.Value)).ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output);
    }

    [Theory]
    [InlineData("Debug", "false", "true")]
    [InlineData("Debug", "true", "true")]
    [InlineData("Release", "true", "true")]
    [InlineData("Release", "false", "false")]
    public async Task Production_ExcludesDebugDevAndDisabledBuilds(
        string configuration, string devBuild, string enabled)
    {
        var result = await EvaluateAsync(
            ("Configuration", configuration), ("DevBuild", devBuild),
            ("MigrationProductionEnabled", enabled));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("PRODUCTION_MIGRATION", result.Output);
        Assert.DoesNotContain("MigrationStoreProductId=", result.Output);
        Assert.DoesNotContain("MigrationMinimumSourceVersion=", result.Output);
    }

    [Theory]
    [InlineData(false, "InnoMigrationPreview", "INNO_MIGRATION_PREVIEW")]
    [InlineData(true, "StoreMigrationPreview", "STORE_MIGRATION_PREVIEW")]
    public async Task ExplicitDebugPreviewsRemainAvailable(bool packaged, string flag, string symbol)
    {
        var result = await EvaluateAsync(
            ("Configuration", "Debug"), ("PackageMsix", packaged.ToString()),
            ("MigrationProductionEnabled", "false"), (flag, "true"),
            ("MigrationPreviewStoreProductId", "9NFPR3BGDRR5"),
            ("StoreMigrationPreviewMinimumSourceVersion", "2026.9.5.0"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(symbol, result.Output);
        Assert.DoesNotContain("PRODUCTION_MIGRATION", result.Output);
    }

    [Theory]
    [InlineData(false, "InnoMigrationPreview")]
    [InlineData(true, "StoreMigrationPreview")]
    public async Task PreviewFlagsCannotShipInRelease(bool packaged, string flag)
    {
        var result = await EvaluateAsync(
            ("MigrationProductionEnabled", "false"), ("PackageMsix", packaged.ToString()),
            (flag, "true"), ("StoreMigrationPreviewMinimumSourceVersion", "2026.9.5.0"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("restricted to Debug", result.Output);
    }

    [Fact]
    public void TrayImportsOneCanonicalMigrationBuildPolicy()
    {
        var directory = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var project = XDocument.Load(Path.Combine(directory, "OpenClaw.Tray.WinUI.csproj"));
        Assert.Single(project.Descendants("Import"), element =>
            (string?)element.Attribute("Project") == "Migration.Build.props");
        var policy = XDocument.Load(Path.Combine(directory, "Migration.Build.props"));
        Assert.Equal("9NFPR3BGDRR5", policy.Descendants("MigrationStoreProductId").Single().Value);
        var minimum = policy.Descendants("MigrationMinimumSourceVersion").Single().Value;
        Assert.Equal("2026.9.5.0", minimum);
        Assert.DoesNotContain("$(Version)", minimum);
        Assert.DoesNotContain("GitVersion", minimum);
        Assert.Equal("true", policy.Descendants("MigrationProductionEnabled").Single().Value);
        Assert.Equal("CoreCompile", policy.Descendants("Target")
            .Single(element => (string?)element.Attribute("Name") == "ValidateMigrationBuild")
            .Attribute("BeforeTargets")!.Value);
    }

    private static async Task<(int ExitCode, string Output)> EvaluateAsync(params (string Key, string Value)[] overrides)
    {
        using var temp = new TempDirectory();
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var properties = new Dictionary<string, string>
        {
            ["Configuration"] = "Release",
            ["RuntimeIdentifier"] = "win-x64",
            ["DevBuild"] = "false",
            ["PackageMsix"] = "false"
        };
        var path = temp.Combine("migration-build.proj");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", properties.Select(pair => new XElement(pair.Key, pair.Value))),
            new XElement("Import", new XAttribute("Project",
                Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Migration.Build.props"))),
            new XElement("Target", new XAttribute("Name", "Probe"),
                new XAttribute("DependsOnTargets", "ValidateMigrationBuild"),
                new XElement("Message", new XAttribute("Importance", "high"),
                    new XAttribute("Text", "MIGRATION-DEFINES=$(DefineConstants)")),
                new XElement("Message", new XAttribute("Importance", "high"),
                    new XAttribute("Text", "@(AssemblyAttribute->'%(_Parameter1)=%(_Parameter2)', '|')")))))
            .Save(path);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "msbuild", path, "-t:Probe", "-nologo", "-v:minimal" })
            start.ArgumentList.Add(argument);
        foreach (var (key, value) in overrides)
            start.ArgumentList.Add($"-p:{key}={value}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start migration build validation.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Migration build validation timed out.");
        }
        return (process.ExitCode, $"{await stdout}\n{await stderr}");
    }
}
