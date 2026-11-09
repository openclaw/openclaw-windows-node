using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OpenClaw.Tray.UITests;

/// <summary>Loads actual production resource markup without changing Windows theme settings.</summary>
internal sealed class ChatThemeProofScope : IDisposable
{
    // Cached native templates can retain converters from an earlier scope. Keep one
    // complete production graph per theme alive for the shared UI application's lifetime.
    private static readonly Dictionary<string, ResourceDictionary> ResourcesByTheme = new(StringComparer.Ordinal);
    private readonly ResourceDictionary _previous = Application.Current.Resources;
    internal ElementTheme ElementTheme { get; }

    internal ChatThemeProofScope(string theme)
    {
        if (theme is not ("Light" or "Dark" or "HighContrast"))
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "Expected Light, Dark, or HighContrast.");
        ElementTheme = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        if (ResourcesByTheme.TryGetValue(theme, out var cached))
        {
            Application.Current.Resources = cached;
            return;
        }
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
        var loaded = (ResourceDictionary)XamlReader.Load(resources.ToString());
        ResourcesByTheme.Add(theme, loaded);
        Application.Current.Resources = loaded;
    }

    public void Dispose() => Application.Current.Resources = _previous;
}
