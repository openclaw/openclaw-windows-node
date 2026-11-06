using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OpenClaw.Tray.UITests;

/// <summary>Loads actual production resource markup without changing Windows theme settings.</summary>
internal sealed class ChatThemeProofScope : IDisposable
{
    private readonly ResourceDictionary _previous;
    internal ElementTheme ElementTheme { get; }

    internal ChatThemeProofScope(string theme)
    {
        if (Application.Current is TestApp { UsesProductionResources: true })
            throw new InvalidOperationException(
                "Per-case resource replacement requires the Standard test resource profile, not Production.");

        _previous = Application.Current.Resources;
        ElementTheme = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        var resources = TestAppResourceLoader.LoadProductionMarkup(
            Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT"));
        if (theme == "HighContrast")
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var dictionaries in resources.Descendants().Where(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            {
                var highContrast = dictionaries.Elements().Single(element => (string?)element.Attribute(x + "Key") == "HighContrast");
                foreach (var active in dictionaries.Elements().Where(element => (string?)element.Attribute(x + "Key") is "Default" or "Light"))
                    active.ReplaceNodes(highContrast.Elements().Select(element => new XElement(element)));
            }
        }
        Application.Current.Resources = (ResourceDictionary)XamlReader.Load(resources.ToString());
    }

    public void Dispose() => Application.Current.Resources = _previous;
}
