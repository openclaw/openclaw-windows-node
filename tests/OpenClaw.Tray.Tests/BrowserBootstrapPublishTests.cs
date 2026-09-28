using System.Diagnostics;
using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public class BrowserBootstrapPublishTests
{
    [Fact]
    public void RequiredPublishProfileBundlesTheStandaloneExecutable()
    {
        var root=TestRepositoryPaths.GetRepositoryRoot();
        var profile=XDocument.Load(Path.Combine(root,"src","OpenClaw.BrowserBootstrap","Properties","PublishProfiles","Windows.pubxml"));
        foreach(var name in new[]{"SelfContained","PublishSingleFile","IncludeNativeLibrariesForSelfExtract"})
            Assert.Equal("true",profile.Descendants(name).Single().Value);
        var target=XDocument.Load(Path.Combine(root,"src","OpenClaw.Tray.WinUI","BrowserBootstrap.targets"));
        var build=target.Root!.Elements("Target").Single(x=>(string?)x.Attribute("Name")=="BuildBrowserBootstrapPayload");
        Assert.Equal("Error",build.Elements().First().Name.LocalName);
        Assert.Contains("Windows.pubxml",(string?)build.Elements().First().Attribute("Condition"));
        Assert.Contains("PublishProfile=Windows",(string?)build.Element("MSBuild")!.Attribute("Properties"));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProductionPublishTarget_CopiesOnlySingleExeOrFails(bool missing)
    {
        var root=TestRepositoryPaths.GetRepositoryRoot();
        var source=XDocument.Load(Path.Combine(root,"src","OpenClaw.Tray.WinUI","BrowserBootstrap.targets"));
        var add=new XElement(source.Root!.Elements("Target").Single(x=>(string?)x.Attribute("Name")=="AddBrowserBootstrapToPublishItems"));
        var verify=new XElement(source.Root.Elements("Target").Single(x=>(string?)x.Attribute("Name")=="VerifyBrowserBootstrapPublished"));
        Assert.Equal("ComputeResolvedFilesToPublishList",(string?)add.Attribute("AfterTargets"));
        using var temp=new TempDirectory();
        var payload=temp.Combine("payload")+Path.DirectorySeparatorChar;Directory.CreateDirectory(payload);
        var output=temp.Combine("published")+Path.DirectorySeparatorChar;
        if(!missing)File.WriteAllText(Path.Combine(payload,"OpenClaw.BrowserBootstrap.exe"),"synthetic executable artifact");
        var harness=temp.Combine("publish.proj");
        new XDocument(new XElement("Project",new XElement("PropertyGroup",new XElement("PublishDir",output),new XElement("BrowserBootstrapPayload",payload)),
            new XElement("Target",new XAttribute("Name","BuildBrowserBootstrapPayload")),
            new XElement("Target",new XAttribute("Name","ComputeResolvedFilesToPublishList")),add,
            new XElement("Target",new XAttribute("Name","Publish"),new XAttribute("DependsOnTargets","ComputeResolvedFilesToPublishList"),
                new XElement("Copy",new XAttribute("SourceFiles","@(ResolvedFileToPublish)"),new XAttribute("DestinationFiles","@(ResolvedFileToPublish->'$(PublishDir)%(RelativePath)')"))),verify)).Save(harness);
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=root,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"msbuild",harness,"-t:Publish","-nologo","-v:minimal"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill(true);throw;}
        var log=await stdout+await stderr;
        if(missing){Assert.NotEqual(0,process.ExitCode);return;}
        Assert.True(process.ExitCode==0,log);
        Assert.Equal("synthetic executable artifact",File.ReadAllText(Path.Combine(output,"tools","browser-bootstrap","OpenClaw.BrowserBootstrap.exe")));
        Assert.Single(Directory.GetFiles(output,"*",SearchOption.AllDirectories));
    }
}
