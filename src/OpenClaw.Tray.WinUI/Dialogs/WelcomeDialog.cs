using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// First-run welcome dialog for new users.
/// </summary>
public sealed class WelcomeDialog : WindowEx
{
    private readonly TaskCompletionSource<ContentDialogResult> _tcs = new();
    private ContentDialogResult _result = ContentDialogResult.None;

    public WelcomeDialog()
    {
        Title = "欢迎使用 OpenClaw";
        this.SetWindowSize(480, 440);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");

        // 应用 Mica 背景以获得现代 Windows 11 外观
        SystemBackdrop = new MicaBackdrop();

        // 直接在窗口中构建 UI（不需要 ContentDialog）
        var root = new Grid
        {
            Padding = new Thickness(32),
            RowSpacing = 16
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 龙虾头部
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        headerPanel.Children.Add(new TextBlock
        {
            Text = "🦞",
            FontSize = 48
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = "欢迎使用 OpenClaw！",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(headerPanel, 0);
        root.Children.Add(headerPanel);

        // 内容
        var content = new StackPanel { Spacing = 16 };

        content.Children.Add(new TextBlock
        {
            Text = "OpenClaw 托盘是你 Windows 的 OpenClaw 伴侣，这是一款 AI 驱动的个人助手。",
            TextWrapping = TextWrapping.Wrap
        });

        var gettingStarted = new StackPanel { Spacing = 8 };
        gettingStarted.Children.Add(new TextBlock
        {
            Text = "开始使用，你需要：",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var bulletList = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 0, 0) };
        bulletList.Children.Add(new TextBlock { Text = "• 一个运行中的 OpenClaw 网关" });
        bulletList.Children.Add(new TextBlock { Text = "• 从仪表板获取的 API 令牌" });
        gettingStarted.Children.Add(bulletList);
        content.Children.Add(gettingStarted);

        var docsButton = new HyperlinkButton
        {
            Content = "📚 查看文档",
            NavigateUri = new Uri("https://docs.molt.bot/web/dashboard")
        };
        content.Children.Add(docsButton);

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // 按钮
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var laterButton = new Button { Content = "稍后" };
        laterButton.Click += (s, e) =>
        {
            _result = ContentDialogResult.None;
            Close();
        };
        buttonPanel.Children.Add(laterButton);

        var settingsButton = new Button
        {
            Content = "打开设置",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        settingsButton.Click += (s, e) =>
        {
            _result = ContentDialogResult.Primary;
            Close();
        };
        buttonPanel.Children.Add(settingsButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        Content = root;

        Closed += (s, e) => _tcs.TrySetResult(_result);
    }

    public new Task<ContentDialogResult> ShowAsync()
    {
        Activate();
        return _tcs.Task;
    }
}
