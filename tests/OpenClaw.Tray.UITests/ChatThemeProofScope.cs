using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OpenClaw.Tray.UITests;

/// <summary>Loads actual production resource markup without changing Windows theme settings.</summary>
internal sealed class ChatThemeProofScope : IDisposable
{
    private static readonly Lazy<ResourceDictionary> s_lightDarkResources =
        new(() => LoadResources(highContrast: false));
    private static readonly Lazy<ResourceDictionary> s_highContrastResources =
        new(() => LoadResources(highContrast: true));
    private readonly ResourceDictionary _previous = Application.Current.Resources;
    internal ElementTheme ElementTheme { get; }

    internal ChatThemeProofScope(string theme)
    {
        ElementTheme = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        Application.Current.Resources = theme == "HighContrast"
            ? s_highContrastResources.Value
            : s_lightDarkResources.Value;
    }

    public void Dispose() => Application.Current.Resources = _previous;

    private static ResourceDictionary LoadResources(bool highContrast)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for production resource proof.");
        var appDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var app = XElement.Load(Path.Combine(appDirectory, "App.xaml"));
        var resources = new XElement(app.Elements().Single().Elements().Single());
        foreach (var dictionary in resources.Descendants().Where(element => element.Attribute("Source") is not null).ToArray())
        {
            var path = Path.Combine(appDirectory, ((string)dictionary.Attribute("Source")!).Replace('/', Path.DirectorySeparatorChar));
            dictionary.ReplaceWith(XElement.Load(path));
        }
        if (highContrast)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var dictionaries in resources.Descendants().Where(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            {
                var highContrastDictionary = dictionaries.Elements().Single(
                    element => (string?)element.Attribute(x + "Key") == "HighContrast");
                foreach (var active in dictionaries.Elements().Where(element => (string?)element.Attribute(x + "Key") is "Default" or "Light"))
                    active.ReplaceNodes(highContrastDictionary.Elements().Select(element => new XElement(element)));
            }
        }
        return (ResourceDictionary)XamlReader.Load(resources.ToString());
    }
}
