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

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
