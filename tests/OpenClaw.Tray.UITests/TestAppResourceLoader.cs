using System.Xml.Linq;

namespace OpenClaw.Tray.UITests;

internal static class TestAppResourceLoader
{
    internal const string ProfileVariable = "OPENCLAW_UI_TEST_RESOURCE_PROFILE";

    internal static bool IsProductionProfile(string? profile) => profile?.Trim().ToLowerInvariant() switch
    {
        null or "" or "standard" => false,
        "production" => true,
        _ => throw new InvalidOperationException($"{ProfileVariable} must be Standard or Production."),
    };

    internal static XElement LoadProductionMarkup(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for production resource proof.");

        var appDirectory = Path.Combine(repositoryRoot, "src", "OpenClaw.Tray.WinUI");
        var app = XElement.Load(Path.Combine(appDirectory, "App.xaml"));
        var resources = new XElement(app.Elements().Single().Elements().Single());
        foreach (var dictionary in resources.Descendants().Where(element => element.Attribute("Source") is not null).ToArray())
        {
            var path = Path.Combine(appDirectory, ((string)dictionary.Attribute("Source")!).Replace('/', Path.DirectorySeparatorChar));
            dictionary.ReplaceWith(XElement.Load(path));
        }
        return resources;
    }
}
