using System.Diagnostics;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class SetupAssetPublishTests
{
    [Fact]
    public async Task Build_CopiesSetupImagesBeforeProjectReferenceConsumersRun()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var trayDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var sourceDirectory = Path.Combine(trayDirectory, "Assets", "Setup");
        var trayProject = XDocument.Load(Path.Combine(trayDirectory, "OpenClaw.Tray.WinUI.csproj"));
        var target = new XElement(Assert.Single(trayProject.Root!.Elements("Target"),
            element => (string?)element.Attribute("Name") == "UseSourceSetupAssetsForPriConsumerCopy"));
        Assert.Equal("AddPriPayloadFilesToCopyToOutputDirectoryItems", (string?)target.Attribute("AfterTargets"));

        using var temp = new TempDirectory();
        var outputDirectory = temp.Combine("output");
        var harnessPath = temp.Combine("build-assets.proj");
        var missingGeneratedAsset = temp.Combine("missing-generated-provider-icon.svg");
        target.Descendants("_SetupPriAssetSource").Single()
            .SetAttributeValue("Include", Path.Combine(sourceDirectory, "**", "*"));
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("OutputDirectory", outputDirectory + Path.DirectorySeparatorChar)),
            new XElement("ItemGroup",
                new XElement("_AllChildProjectItemsWithTargetPath",
                    new XAttribute("Include", missingGeneratedAsset),
                    new XElement("TargetPath",
                        @"OpenClaw.SetupEngine.UI\Assets\Setup\ProviderIcons\ProviderIcon-claude.svg"),
                    new XElement("CopyToOutputDirectory", "PreserveNewest"))),
            target,
            new XElement("Target",
                new XAttribute("Name", "CopyResolvedSetupAssets"),
                new XAttribute("DependsOnTargets", "UseSourceSetupAssetsForPriConsumerCopy"),
                new XElement("Copy",
                    new XAttribute("SourceFiles", "@(_AllChildProjectItemsWithTargetPath)"),
                    new XAttribute("DestinationFiles",
                        "@(_AllChildProjectItemsWithTargetPath->'$(OutputDirectory)%(TargetPath)')")))))
            .Save(harnessPath);

        await RunTargetAsync(root, harnessPath, "CopyResolvedSetupAssets");
        AssertSetupAssetsCopied(sourceDirectory,
            Path.Combine(outputDirectory, "OpenClaw.SetupEngine.UI", "Assets", "Setup"));
    }

    [Fact]
    public async Task Publish_CopiesSetupImagesToTheirLibraryQualifiedPaths()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var trayDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var sourceDirectory = Path.Combine(trayDirectory, "Assets", "Setup");
        var project = XDocument.Load(Path.Combine(trayDirectory, "OpenClaw.Tray.WinUI.csproj"));
        var target = new XElement(Assert.Single(project.Root!.Elements("Target"),
            element => (string?)element.Attribute("Name") == "CopySetupAssetsLooseForPublish"));
        Assert.Equal("Publish", (string?)target.Attribute("AfterTargets"));

        using var temp = new TempDirectory();
        var publishDirectory = temp.Combine("publish");
        var harnessPath = temp.Combine("publish-assets.proj");
        // Execute the production copy target without rebuilding WinUI or relying
        // on stale bin files, which masked the missing paths on developer machines.
        target.Descendants("_SetupAssetPublishSource").Single()
            .SetAttributeValue("Include", Path.Combine(sourceDirectory, "**", "*"));
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("PublishDir", publishDirectory + Path.DirectorySeparatorChar)),
            target)).Save(harnessPath);

        await RunTargetAsync(root, harnessPath, "CopySetupAssetsLooseForPublish");
        AssertSetupAssetsCopied(sourceDirectory,
            Path.Combine(publishDirectory, "Assets", "Setup"),
            Path.Combine(publishDirectory, "OpenClaw.SetupEngine.UI", "Assets", "Setup"));
    }

    private static async Task RunTargetAsync(string root, string harnessPath, string targetName)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "msbuild", harnessPath, $"-t:{targetName}", "-nologo", "-v:minimal",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the setup asset publish test.");
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
            throw new TimeoutException("The setup asset publish target did not finish within one minute.");
        }

        Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
    }

    private static void AssertSetupAssetsCopied(string sourceDirectory, params string[] assetRoots)
    {
        var images = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(images);
        Assert.Contains(images, path => path.EndsWith("ProviderIcon-claude.svg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(images, path => path.EndsWith("ProviderIcons" + Path.DirectorySeparatorChar + "NOTICE.md", StringComparison.OrdinalIgnoreCase));
        foreach (var source in images)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, source);
            foreach (var assetRoot in assetRoots)
            {
                var published = Path.Combine(assetRoot, relativePath);
                Assert.True(File.Exists(published), $"Published setup asset is missing: {published}");
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(published));
            }
        }
    }
}
