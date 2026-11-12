using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Native behavior proof for the tray icon fallback (#1567): when the
/// <c>SymbolThemeFontFamily</c> theme resource is unavailable, icon
/// construction must fall back to Segoe MDL2 Assets and tray menu rows must
/// remain visible instead of the menu going blank. The degraded state is
/// forced deterministically by shadowing the theme value with a non-FontFamily
/// object, mirroring the runtime resource degradation seen after extended
/// uptime.
/// </summary>
[Collection(UICollection.Name)]
public sealed class FluentIconFallbackProofTests
{
    private readonly UIThreadFixture _ui;
    public FluentIconFallbackProofTests(UIThreadFixture ui) => _ui = ui;

    private static object? CorruptThemeFontResource(Application app)
    {
        var saved = app.Resources["SymbolThemeFontFamily"];
        app.Resources["SymbolThemeFontFamily"] = "degraded-resource-value";
        return saved;
    }

    private static void RestoreThemeFontResource(Application app, object? saved)
    {
        app.Resources.Remove("SymbolThemeFontFamily");
        if (saved is FontFamily)
            app.Resources["SymbolThemeFontFamily"] = saved;
    }

    [Fact]
    public async Task Icon_FallsBackToMarkdown2_WhenThemeFontResourceUnavailable()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var app = Application.Current;
            var saved = CorruptThemeFontResource(app);
            try
            {
                var icon = FluentIconCatalog.Build(FluentIconCatalog.StatusOk);

                Assert.NotNull(icon);
                Assert.Equal("Segoe MDL2 Assets", icon.FontFamily?.Source);
                Assert.Equal(FluentIconCatalog.StatusOk, icon.Glyph);
            }
            finally
            {
                RestoreThemeFontResource(app, saved);
            }
        });
    }

    [Fact]
    public async Task TrayMenuRows_RemainVisible_WhenThemeFontResourceUnavailable()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var app = Application.Current;
            var saved = CorruptThemeFontResource(app);
            try
            {
                var menu = new TrayMenuWindow();
                menu.AddMenuItem("First row", FluentIconCatalog.StatusOk, "first");
                menu.AddMenuItem("Second row", FluentIconCatalog.Sync, "second");
                menu.AddToggleItem("Toggle row", FluentIconCatalog.Lock, description: "with icon", isOn: true, "toggle");

                var rows = menu.MenuPanel.Children.OfType<Button>().Count();
                Assert.Equal(2, rows);
                Assert.Equal(3, menu.MenuPanel.Children.Count);
            }
            finally
            {
                RestoreThemeFontResource(app, saved);
            }
        });
    }
}
