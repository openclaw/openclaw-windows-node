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
    private readonly ResourceDictionary _previous;
    internal ElementTheme ElementTheme { get; }

    internal ChatThemeProofScope(string theme)
    {
        if (Application.Current is TestApp { UsesProductionResources: true })
            throw new InvalidOperationException(
                "Per-case resource replacement requires the Standard test resource profile, not Production.");

        _previous = Application.Current.Resources;
        ElementTheme = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        Application.Current.Resources = theme == "HighContrast"
            ? s_highContrastResources.Value
            : s_lightDarkResources.Value;
    }

    public void Dispose() => Application.Current.Resources = _previous;

    private static ResourceDictionary LoadResources(bool highContrast)
    {
        var resources = TestAppResourceLoader.LoadProductionMarkup(
            Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT"));
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
