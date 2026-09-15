using System.Diagnostics;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class SetupAssetPublishTests
{
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
            "msbuild", harnessPath, "-t:CopySetupAssetsLooseForPublish", "-nologo", "-v:minimal",
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
        var images = Directory.GetFiles(sourceDirectory, "*.png", SearchOption.AllDirectories);
        Assert.NotEmpty(images);
        foreach (var source in images)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, source);
            foreach (var assetRoot in new[]
            {
                Path.Combine(publishDirectory, "Assets", "Setup"),
                Path.Combine(publishDirectory, "OpenClaw.SetupEngine.UI", "Assets", "Setup"),
            })
            {
                var published = Path.Combine(assetRoot, relativePath);
                Assert.True(File.Exists(published), $"Published setup asset is missing: {published}");
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(published));
            }
        }
    }
}
