using OpenClaw.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;
using OpenClawTray.Chat.Explorations;
using OpenClawTray.FunctionalUI.Hosting;
using System;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// 좌측 ChatExplorationsPanel + 우측 라이브 OpenClawChatRoot.
/// 실제 백엔드 없이 <see cref="FakeChatDataProvider"/>로 데모 메시지를 채워서
/// 양쪽 버블/아바타/툴카드 모두 즉시 시각화된다.
/// 토글이 ChatExplorationState 를 갱신하면 우측 챗이 즉시 다시 그려진다.
/// </summary>
public sealed class ChatExplorationsWindow : WindowEx
{
    private FunctionalHostControl? _panelHost;
    private FunctionalHostControl? _chatHost;

    public ChatExplorationsWindow()
    {
        Title = "Chat explorations";
        this.SetWindowSize(1100, 720);
        SystemBackdrop = new MicaBackdrop();

        var panelTarget = new Border();
        Grid.SetColumn(panelTarget, 0);

        var splitter = new Border
        {
            Width = 1,
            Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        };
        Grid.SetColumn(splitter, 1);

        var chatTarget = new Border();
        Grid.SetColumn(chatTarget, 2);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(panelTarget);
        grid.Children.Add(splitter);
        grid.Children.Add(chatTarget);

        Content = grid;

        // Panel 은 자체 Window 의 Content 에 마운트할 수 없으므로 Border 타깃으로.
        _panelHost = new FunctionalHostControl();
        _panelHost.Mount(new ChatExplorationsPanel());
        panelTarget.Child = _panelHost;

        _chatHost = new FunctionalHostControl();
        _chatHost.Mount(new OpenClawChatRoot(new FakeChatDataProvider(), initialThreadId: null));
        chatTarget.Child = _chatHost;

        Closed += (_, _) =>
        {
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            try { _chatHost?.Dispose(); } catch { }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            try { _panelHost?.Dispose(); } catch { }
            _chatHost = null;
            _panelHost = null;
        };
    }
}
