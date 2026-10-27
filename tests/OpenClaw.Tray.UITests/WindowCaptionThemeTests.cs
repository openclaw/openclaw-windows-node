using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WindowCaptionThemeTests(UIThreadFixture ui)
{
    [Fact]
    public async Task ApplyTheme_KeepsNativeCaptionsAlignedWithContentAcrossPreferenceChanges()
    {
        await ui.RunOnUIAsync(() =>
        {
            var root = new Grid();
            var window = new Window { Content = root, ExtendsContentIntoTitleBar = true };
            try
            {
                // Reuse one native window to cover live changes and returning to System.
                (string? Preference, ElementTheme ContentTheme, TitleBarTheme CaptionTheme)[] changes =
                [
                    ("Light", ElementTheme.Light, TitleBarTheme.Light),
                    ("Dark", ElementTheme.Dark, TitleBarTheme.Dark),
                    ("System", ElementTheme.Default, TitleBarTheme.UseDefaultAppMode),
                    ("lIgHt", ElementTheme.Light, TitleBarTheme.Light),
                    (null, ElementTheme.Default, TitleBarTheme.UseDefaultAppMode),
                    ("dArK", ElementTheme.Dark, TitleBarTheme.Dark),
                    ("invalid", ElementTheme.Default, TitleBarTheme.UseDefaultAppMode)
                ];

                foreach (var change in changes)
                {
                    ThemeHelper.ApplyTheme(window, change.Preference);

                    Assert.Equal(change.ContentTheme, root.RequestedTheme);
                    Assert.Equal(change.CaptionTheme, window.AppWindow.TitleBar.PreferredTheme);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
