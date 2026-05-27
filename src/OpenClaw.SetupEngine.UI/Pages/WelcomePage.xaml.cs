using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using System.Diagnostics;
using System.Numerics;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;

    public WelcomePage()
    {
        Program.WriteStartupBreadcrumb("WelcomePage.ctor.begin");
        BuildPageShell();
        Program.WriteStartupBreadcrumb("WelcomePage.ctor.afterBuildPageShell");
        Loaded += OnLoaded;
    }

    private void BuildPageShell()
    {
        var root = new Grid
        {
            Padding = new Thickness(56, 64, 56, 40)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12
        };
        Grid.SetRow(header, 0);
        header.Children.Add(new TextBlock
        {
            Text = "Get started with OpenClaw",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = "OpenClaw lets agents run commands, read and write files, and capture screenshots on this PC. Only set it up on a computer you trust.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            Opacity = 0.7,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var footer = new StackPanel
        {
            Spacing = 16
        };
        Grid.SetRow(footer, 2);

        InfoCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 18, 20, 18)
        };
        var infoGrid = new Grid
        {
            ColumnSpacing = 12
        };
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoGrid.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xC8, 0xF8)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "i",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        InfoText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.85
        };
        Grid.SetColumn(InfoText, 1);
        infoGrid.Children.Add(InfoText);
        InfoCard.Child = infoGrid;
        footer.Children.Add(InfoCard);

        StartButton = new Button
        {
            Content = "Install new WSL Gateway",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 44,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        StartButton.Click += StartButton_Click;
        footer.Children.Add(StartButton);

        var advancedSetup = new HyperlinkButton
        {
            Content = "Advanced setup",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xC8, 0xF8)),
            FontSize = 14
        };
        advancedSetup.Click += AdvancedSetup_Click;
        footer.Children.Add(advancedSetup);

        root.Children.Add(header);
        root.Children.Add(footer);
        Content = root;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        InfoCard.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(255, 0xF0, 0xF0, 0xF0));

        InfoText.Text = "This local setup installs a small WSL Linux instance dedicated to OpenClaw. "
                      + "If you'd rather connect to an existing or remote gateway, choose Advanced setup.";

        if (LobsterHero != null)
            StartLobsterBreatheAnimation();
    }

    private void StartLobsterBreatheAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(LobsterHero);
        var compositor = visual.Compositor;
        var centerX = LobsterHero.ActualWidth > 0 ? LobsterHero.ActualWidth / 2 : LobsterHero.Width / 2;
        var centerY = LobsterHero.ActualHeight > 0 ? LobsterHero.ActualHeight / 2 : LobsterHero.Height / 2;
        visual.CenterPoint = new Vector3((float)centerX, (float)centerY, 0f);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.025f, 1.025f, 1f));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        pulse.Duration = TimeSpan.FromMilliseconds(4200);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", pulse);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");

        var existing = ExistingConfigDetector.Detect(dataDir, _config!.DistroName);
        var summary = ExistingConfigDetector.BuildReplacementSummary(existing);

        var dialog = new ContentDialog
        {
            Title = existing.HasLocalGateway || existing.HasDistro
                ? "Replace existing WSL gateway?"
                : "Install a new WSL gateway?",
            Content = summary,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            App.MainWindow?.NavigateToCapabilities();
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        // Launch tray app navigated to connection settings
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenClaw.Tray.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "OpenClaw.Tray.WinUI.exe"),
        };

        var trayPath = candidates.FirstOrDefault(File.Exists);
        var args = "--page connection";

        if (trayPath != null)
            Process.Start(new ProcessStartInfo(trayPath, args) { UseShellExecute = true });
        else
        {
            try { Process.Start(new ProcessStartInfo("OpenClaw.Tray.WinUI.exe", args) { UseShellExecute = true }); }
            catch { /* best effort */ }
        }

        App.MainWindow?.Close();
    }
}
