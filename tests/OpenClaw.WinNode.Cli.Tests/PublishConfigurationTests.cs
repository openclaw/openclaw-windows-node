using System.Xml.Linq;

namespace OpenClaw.WinNode.Cli.Tests;

public class PublishConfigurationTests
{
    [Fact]
    public void WinNode_publish_uses_conservative_partial_trimming()
    {
        var project = XDocument.Load(FindProjectFile());
        var properties = project.Root!
            .Elements("PropertyGroup")
            .SelectMany(group => group.Elements())
            .ToDictionary(element => element.Name.LocalName, element => element.Value);

        Assert.Equal("true", properties["PublishTrimmed"]);
        Assert.Equal("partial", properties["TrimMode"]);

        var preserveTarget = Assert.Single(
            project.Root!.Elements("Target"),
            target => (string?)target.Attribute("Name") == "PreserveOpenClawSharedDuringTrim");
        Assert.Equal("_ComputeManagedAssemblyToLink", (string?)preserveTarget.Attribute("BeforeTargets"));

        var sharedUpdate = Assert.Single(
            preserveTarget.Descendants("ResolvedFileToPublish"),
            item => ((string?)item.Attribute("Condition"))?.Contains(
                "OpenClaw.Shared",
                StringComparison.Ordinal) == true);
        Assert.Equal("false", sharedUpdate.Element("PostprocessAssembly")?.Value);
    }

    private static string FindProjectFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "OpenClaw.WinNode.Cli",
                "OpenClaw.WinNode.Cli.csproj");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate OpenClaw.WinNode.Cli.csproj.");
    }
}
