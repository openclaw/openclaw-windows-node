using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OpenClaw.Tray.UITests;

/// <summary>Installs actual Usage resource markup in place before Reactor compiles styles against the test application.</summary>
internal static class UsageThemeProofResources
{
    private static ResourceDictionary? s_installed;

    public static void Ensure()
    {
        var target = Application.Current.Resources;
        if (ReferenceEquals(s_installed, target)) return;
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for Usage resource proof.");
        var app = XElement.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml"));
        var source = app.Elements().Single().Elements().Single();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var themes in source.Elements().Where(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
        {
            foreach (var theme in themes.Elements())
            {
                var name = (string)theme.Attribute(x + "Key")!;
                if (!target.ThemeDictionaries.TryGetValue(name, out var existing) || existing is not ResourceDictionary)
                    target.ThemeDictionaries[name] = new ResourceDictionary();
                Copy(theme, (ResourceDictionary)target.ThemeDictionaries[name]);
            }
        }
        Copy(source, target);
        s_installed = target;

        static void Copy(XElement source, ResourceDictionary target)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var element in source.Elements())
            {
                var key = (string?)element.Attribute(x + "Key");
                if (key is null || (!key.StartsWith("Usage", StringComparison.Ordinal)
                    && !key.StartsWith("ConnectionCapabilityPill", StringComparison.Ordinal))) continue;
                var markup = new XElement(element);
                markup.Attribute(x + "Key")!.Remove();
                target[key] = XamlReader.Load(markup.ToString());
            }
        }
    }
}
