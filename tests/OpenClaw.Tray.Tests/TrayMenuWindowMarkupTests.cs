using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public class TrayMenuWindowMarkupTests
{
    [Fact]
    public void TrayMenuWindow_UsesVisibleVerticalScrollbar()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "TrayMenuWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Matches(
            new Regex(@"<ScrollViewer[^>]*VerticalScrollBarVisibility=""Visible""", RegexOptions.Singleline),
            xaml);
    }

    [Fact]
    public void SettingsWindow_HasCommandCenterEntryPoint()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SettingsWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsOpenCommandCenterButton""", xaml);
        Assert.Contains(@"Content=""Open Command Center""", xaml);
        Assert.Contains(@"Click=""OnOpenCommandCenter""", xaml);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "moltbot-windows-hub.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
