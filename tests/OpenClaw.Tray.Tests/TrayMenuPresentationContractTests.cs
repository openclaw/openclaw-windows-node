namespace OpenClaw.Tray.Tests;

public sealed class TrayMenuPresentationContractTests
{
    [Fact]
    public void PresentationFiles_AreWinUiAppAndConcreteSettingsFree()
    {
        var presentationDirectory = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation");
        var files = new[]
        {
            "TrayMenuPresentation.cs",
            "TrayMenuPresenter.cs",
            "ConnectionTogglePresenter.cs",
        };
        var forbidden = new[]
        {
            "Microsoft.UI",
            "Application.Current",
            "UIElement",
            "Brush",
            "SettingsManager",
            "App.",
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(presentationDirectory, file));
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\bWindow\b", source);
        }
    }

    [Fact]
    public void Renderer_DoesNotInterpretSnapshot_AndWindowRetainsPopupMechanics()
    {
        var renderer = Read("src", "OpenClaw.Tray.WinUI", "Services", "TrayMenuRenderer.cs");
        var window = Read("src", "OpenClaw.Tray.WinUI", "Windows", "TrayMenuWindow.xaml.cs");

        Assert.DoesNotContain("TrayMenuSnapshot", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStatusPresenter", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsManager", renderer, StringComparison.Ordinal);

        Assert.Contains("GetCursorPos", window);
        Assert.Contains("SetWindowPos", window);
        Assert.Contains("ShowAdjacentTo", window);
        Assert.Contains("SetForegroundWindow", window);
        Assert.Contains("HideCascade", window);
        Assert.Contains("SizeToContent", window);
        Assert.Contains("OnActivated", window);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
