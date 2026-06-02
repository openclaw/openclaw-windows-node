using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI.Hosting;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Panel-only window. ChatExplorationsWindow 의 좌측 패널만 단독으로 띄워서
/// 백드롭/투명도 자체를 검증할 수 있게 한다. 메인 explorations window 가
/// 닫혀도 계속 살아있다.
/// </summary>
public sealed class ChatExplorationsPanelWindow : WindowEx
{
    private FunctionalHostControl? _host;

    public ChatExplorationsPanelWindow()
    {
        Title = "Chat explorations — panel only";
        this.SetWindowSize(420, 720);
        SystemBackdrop = new MicaBackdrop();

        var target = new Border();
        Content = target;

        _host = new FunctionalHostControl();
        _host.Mount(new OpenClawTray.Chat.Explorations.ChatExplorationsPanel());
        target.Child = _host;

        Closed += (_, _) =>
        {
            try { _host?.Dispose(); }
            catch (Exception ex) { Logger.Warn($"Failed to dispose chat explorations panel host: {ex.Message}"); }
            _host = null;
        };
    }
}
