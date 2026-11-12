// Measures real native input controls using the shipping capped-field layout attributes.
// Run with FullyQualifiedName~SettingsFieldAlignmentTests on the Standard resource profile.
using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Width caps must retain left alignment without overflowing a narrow form.</summary>
[Collection(UICollection.Name)]
public sealed class SettingsFieldAlignmentTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("DefaultModelPage", false)]
    [InlineData("DefaultModelPage", true)]
    [InlineData("LocalAiPage", false)]
    [InlineData("LocalAiPage", true)]
    [InlineData("VoiceSettingsPage", false)]
    [InlineData("VoiceSettingsPage", true)]
    public async Task ShippingFieldsStayLeftAndFitWideAndNarrowSlots(string page, bool populated)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT.");
        var source = XDocument.Load(Path.Combine(repo, "src", "OpenClaw.Tray.WinUI", "Pages", page + ".xaml"));
        var definitions = source.Descendants().Where(element =>
            element.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox"
            && element.Attribute("MaxWidth") is not null).ToArray();
        Assert.NotEmpty(definitions);
        await ui.ResetContainerAsync();
        foreach (var definition in definitions)
        {
            Grid? slot = null;
            FrameworkElement? field = null;
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    // Copy only layout/content attributes; page handlers and app services are not involved.
                    var header = definition.Attribute("Header")?.Value ?? "Field";
                    const string value = "A deliberately long synthetic field value for narrow layout";
                    field = definition.Name.LocalName switch
                    {
                        "TextBox" => new TextBox { Header = header, Text = populated ? value : "" },
                        "PasswordBox" => new PasswordBox { Header = header, Password = populated ? value : "" },
                        "ComboBox" => new ComboBox { Header = header, Items = { value }, SelectedIndex = populated ? 0 : -1 },
                        _ => throw new InvalidOperationException("Unexpected field kind."),
                    };
                    field.MaxWidth = double.Parse(definition.Attribute("MaxWidth")!.Value, CultureInfo.InvariantCulture);
                    field.HorizontalAlignment = Enum.Parse<HorizontalAlignment>(definition.Attribute("HorizontalAlignment")!.Value);
                    slot = new Grid { Width = 800, Children = { field } };
                    ui.Container.Children.Add(slot);
                });
                foreach (var width in new[] { 800d, 160d, 800d })
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        slot!.Width = width;
                        ui.Container.UpdateLayout();
                    });
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                        () => slot!.IsLoaded && Math.Abs(slot.ActualWidth - width) < 1 && field!.ActualWidth > 0,
                        "capped native field arranged"));
                    await ui.RunOnUIAsync(() =>
                    {
                        var origin = field!.TransformToVisual(slot).TransformPoint(new Windows.Foundation.Point());
                        Assert.InRange(origin.X, -0.5, 0.5);
                        Assert.InRange(field.ActualWidth, 24, Math.Min(width, field.MaxWidth) + 0.5);
                        Assert.True(origin.X + field.ActualWidth <= width + 0.5);
                    });
                }
            }
            finally
            {
                await ui.RunOnUIAsync(() => { if (slot is not null) ui.Container.Children.Remove(slot); });
            }
        }
    }
}
