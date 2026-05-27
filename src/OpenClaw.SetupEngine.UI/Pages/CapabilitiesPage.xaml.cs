using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupConfig? _config;
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();

    // (config property, display name, description, fluent icon glyph)
    private static readonly (string Key, string Name, string Desc, string Glyph)[] Capabilities =
    [
        ("System", "System", "Shell commands, files, clipboard", "\uE756"),
        ("Canvas", "Canvas", "Whiteboard and annotations", "\uE790"),
        ("Screen", "Screen capture", "Screenshots and recording", "\uE7F4"),
        ("Camera", "Camera", "Webcam photos and video", "\uE722"),
        ("Location", "Location", "Share device location", "\uE81D"),
        ("Browser", "Browser", "Web navigation and automation", "\uE774"),
        ("Device", "Device", "Volume, brightness, system info", "\uE772"),
        ("Tts", "Text-to-speech", "Speak text aloud", "\uE767"),
        ("Stt", "Speech-to-text", "Transcribe spoken audio", "\uE720"),
    ];

    public CapabilitiesPage()
    {
        Program.WriteStartupBreadcrumb("CapabilitiesPage.ctor.begin");
        BuildPageShell();
        Program.WriteStartupBreadcrumb("CapabilitiesPage.ctor.afterBuildPageShell");
    }

    private void BuildPageShell()
    {
        var root = new Grid
        {
            Padding = new Thickness(40, 28, 40, 28)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(header, 0);
        header.Children.Add(new TextBlock
        {
            Text = "Configure capabilities",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = "Choose which capabilities this node will advertise. You can change these later.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            Opacity = 0.6,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        CapGrid = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 4
        };
        CapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        CapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var scroller = new ScrollViewer
        {
            Content = CapGrid,
            Margin = new Thickness(0, 20, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroller, 1);

        var continueButton = new Button
        {
            Content = "Continue",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 16, 0, 0),
            Height = 44,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        continueButton.Resources["ButtonBackground"] = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xC8, 0xF8));
        continueButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 0x52, 0xB0, 0xDA));
        continueButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 0x46, 0x9B, 0xBC));
        continueButton.Resources["ButtonForeground"] = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        continueButton.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        continueButton.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        continueButton.Click += Continue_Click;
        Grid.SetRow(continueButton, 2);

        root.Children.Add(header);
        root.Children.Add(scroller);
        root.Children.Add(continueButton);
        Content = root;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Program.WriteStartupBreadcrumb("CapabilitiesPage.OnNavigatedTo.begin");
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        BuildToggles();
        Program.WriteStartupBreadcrumb("CapabilitiesPage.OnNavigatedTo.afterBuildToggles");
    }

    private void BuildToggles()
    {
        var caps = _config!.Capabilities;
        var totalRows = (Capabilities.Length + 1) / 2; // ceiling division for 2 columns

        // Add row definitions
        for (int i = 0; i < totalRows; i++)
            CapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < Capabilities.Length; i++)
        {
            var (key, name, desc, glyph) = Capabilities[i];
            var prop = typeof(CapabilitiesConfig).GetProperty(key);
            var isEnabled = (bool)(prop?.GetValue(caps) ?? true);

            var toggle = new ToggleSwitch
            {
                IsOn = isEnabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
            };
            _toggles[key] = toggle;

            // Card-like item: icon + text + toggle
            var item = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Padding = new Thickness(10, 12, 6, 12),
            };

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.85,
            };

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textStack.Children.Add(new TextBlock { Text = desc, FontSize = 11, Opacity = 0.55 });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(toggle, 2);
            item.Children.Add(icon);
            item.Children.Add(textStack);
            item.Children.Add(toggle);

            int row = i / 2;
            int col = i % 2;
            Grid.SetRow(item, row);
            Grid.SetColumn(item, col);
            CapGrid.Children.Add(item);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Program.WriteStartupBreadcrumb("CapabilitiesPage.Continue.begin");
        var caps = _config!.Capabilities;
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (_toggles.TryGetValue(key, out var toggle))
            {
                var prop = typeof(CapabilitiesConfig).GetProperty(key);
                prop?.SetValue(caps, toggle.IsOn);
            }
        }

        App.MainWindow?.NavigateToProgress();
        Program.WriteStartupBreadcrumb("CapabilitiesPage.Continue.returned");
    }
}
