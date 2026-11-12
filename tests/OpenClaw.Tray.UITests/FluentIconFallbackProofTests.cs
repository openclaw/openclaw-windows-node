using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Windows;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Native behavior proof for the tray icon fallback (#1567): when the
/// <c>SymbolThemeFontFamily</c> theme resource is unavailable, icon
/// construction must fall back to Segoe MDL2 Assets and tray menu rows must
/// remain visible instead of the menu going blank. The degraded state is
/// forced deterministically by shadowing the theme value with a non-font
/// object, mirroring the runtime resource degradation seen after extended
/// uptime. Runs green on the fixed head and fails on the pre-fix base (the
/// icon lookup casts the corrupted value and throws).
/// </summary>
[Collection(UICollection.Name)]
public sealed class FluentIconFallbackProofTests
{
    private readonly UIThreadFixture _ui;
    public FluentIconFallbackProofTests(UIThreadFixture ui) => _ui = ui;

    // CheckMark glyph (FluentIconCatalog.StatusOk) passed as a raw PUA string
    // to avoid the type's duplicate compilation in OpenClaw.SetupEngine.UI.
    private const string MenuGlyph = "\uE73E";

    private static Panel MenuPanelOf(TrayMenuWindow menu) =>
        (Panel)typeof(TrayMenuWindow)
            .GetField("MenuPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(menu)!;

    [Fact]
    public async Task TrayMenuRows_RemainVisible_WithFallbackIconFont_WhenThemeFontUnavailable()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var app = Application.Current;
            var saved = app.Resources["SymbolThemeFontFamily"];
            app.Resources["SymbolThemeFontFamily"] = "degraded-resource-value";
            try
            {
                var menu = new TrayMenuWindow();
                menu.AddMenuItem("First row", MenuGlyph, "first");
                menu.AddMenuItem("Second row", MenuGlyph, "second");
                menu.AddToggleItem("Toggle row", MenuGlyph, description: "with icon", isOn: true, "toggle");

                var panel = MenuPanelOf(menu);
                Assert.Equal(3, panel.Children.Count);

                var rowGrid = (Grid)((Button)panel.Children[0]).Content;
                var iconSlot = (Border)rowGrid.Children[0];
                var icon = (FontIcon)iconSlot.Child!;
                Assert.Equal(MenuGlyph, icon.Glyph);
                Assert.Equal("Segoe MDL2 Assets", icon.FontFamily?.Source);
            }
            finally
            {
                app.Resources.Remove("SymbolThemeFontFamily");
                if (saved is not null)
                    app.Resources["SymbolThemeFontFamily"] = saved;
            }
        });
    }
}
