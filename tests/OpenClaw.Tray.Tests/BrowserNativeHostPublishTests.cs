using System.Diagnostics;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public class BrowserNativeHostPublishTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("hostpolicy.dll")]
    [InlineData("hostfxr.dll")]
    [InlineData("System.Private.CoreLib.dll")]
    public async Task Publish_KeepsIndependentRuntimeAndRejectsMissingFiles(string? missing)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "BrowserNativeHost.targets"));
        var add = new XElement(project.Root!.Elements("Target").Single(x => (string?)x.Attribute("Name") == "AddBrowserNativeHostToPublishItems"));
        var verify = new XElement(project.Root.Elements("Target").Single(x => (string?)x.Attribute("Name") == "VerifyBrowserNativeHostPublished"));
        Assert.Equal("ComputeResolvedFilesToPublishList", (string?)add.Attribute("AfterTargets"));
        var build = project.Root.Elements("Target").Single(x => (string?)x.Attribute("Name") == "BuildBrowserNativeHostPayload");
        Assert.Empty(build.Descendants("ResolvedFileToPublish"));
        Assert.Equal("Never", build.Descendants("ContentWithTargetPath").Single().Element("CopyToPublishDirectory")!.Value);

        using var temp = new TempDirectory();
        var payload = temp.Combine("payload");
        var published = temp.Combine("publish") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(payload);
        var required = verify.Descendants("_RequiredBrowserNativeFile").Single().Attribute("Include")!.Value.Split(';');
        foreach (var file in required.Where(file => file != missing))
            File.WriteAllText(Path.Combine(payload, file), "synthetic artifact: " + file);
        var parentRuntime = temp.Combine("hostpolicy.dll");
        File.WriteAllText(parentRuntime, "parent runtime");
        var harness = temp.Combine("publish-native.proj");
        // Exercise the production late item insertion, copy paths and completeness gate.
        // The stand-in resolution target retains a same-named runtime at the parent root.
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", new XElement("PublishDir", published)),
            new XElement("Target", new XAttribute("Name", "BuildBrowserNativeHostPayload"),
                new XElement("ItemGroup", new XElement("_BrowserNativeHostFile", new XAttribute("Include", Path.Combine(payload, "**", "*"))))),
            new XElement("Target", new XAttribute("Name", "ComputeResolvedFilesToPublishList"),
                new XElement("ItemGroup", new XElement("ResolvedFileToPublish", new XAttribute("Include", parentRuntime),
                    new XElement("RelativePath", "hostpolicy.dll")))),
            add,
            new XElement("Target", new XAttribute("Name", "Publish"), new XAttribute("DependsOnTargets", "ComputeResolvedFilesToPublishList"),
                new XElement("Copy", new XAttribute("SourceFiles", "@(ResolvedFileToPublish)"),
                    new XAttribute("DestinationFiles", "@(ResolvedFileToPublish->'$(PublishDir)%(RelativePath)')"))),
            verify)).Save(harness);

        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "msbuild", harness, "-t:Publish", "-nologo", "-v:minimal" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var output = await stdout + await stderr;
        if (missing is not null)
        {
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("missing " + missing, output);
            return;
        }
        Assert.True(process.ExitCode == 0, output);
        Assert.Equal("parent runtime", File.ReadAllText(Path.Combine(published, "hostpolicy.dll")));
        foreach (var file in required)
            Assert.Equal("synthetic artifact: " + file, File.ReadAllText(Path.Combine(published, "tools", "browser-bootstrap", file)));
    }
}
