using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Cron;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationToastProps(AutomationsNotice Notice, AutomationsText Text, bool IsLatest, Action<long> Dismiss)
{
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
}

internal sealed class AutomationToast : Component<AutomationToastProps>
{
    private static readonly TimeSpan NormalLifetime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ActionLifetime = TimeSpan.FromSeconds(7);

    public override Element Render()
    {
        var (notice, text, latest, dismiss) = Props;
        var hasAction = Props.Action is not null && !string.IsNullOrEmpty(Props.ActionLabel);
        UseEffect((Func<Action>)(() =>
        {
            if (notice.IsError) return () => { };
            var cts = new CancellationTokenSource();
            _ = Task.Delay(hasAction ? ActionLifetime : NormalLifetime, cts.Token).ContinueWith(task =>
            {
                if (!task.IsCanceled) dismiss(notice.Id);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return () => cts.Cancel();
        }), notice.Id, hasAction);

        var (_, message) = text.Notice(notice);
        var information = notice.Kind == AutomationsNoticeKind.RunNotStarted && !notice.IsError;
        var brush = information ? Theme.SecondaryText : Theme.Ref(notice.IsError ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush");
        var glyph = notice.IsError ? GlyphError : information ? GlyphInformation : GlyphOk;
        return Border(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                Glyph(glyph, 18, brush).Margin(0, 0, 10, 0).Grid(column: 0),
                TextBlock(message).FontSize(14).Foreground(Theme.PrimaryText).TextWrapping(TextWrapping.Wrap)
                    .MaxLines(3).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(message)
                    .AutomationName(message).AutomationId(latest ? "AutomationsNotice" : $"AutomationToastMessage:{notice.Id}")
                    .Grid(column: 1),
                HStack(8,
                    hasAction ? (Element)Button(Props.ActionLabel!, () => { Props.Action!(); dismiss(notice.Id); })
                        .AutomationName(Props.ActionLabel!).ApplyStyle("SubtleButtonStyle").Margin(8, 0, 0, 0) : Empty(),
                    notice.IsError ? IconButton("\uE711", text["Review_Close"], () => dismiss(notice.Id),
                        latest ? "AutomationToastClose" : $"AutomationToastClose:{notice.Id}") : Empty()).Grid(column: 2)))
            .Padding(16, 12).CornerRadius(CardRadius)
            .Background(Theme.Ref("FlyoutPresenterBackground")).BorderBrush(Theme.Ref("FlyoutBorderThemeBrush")).BorderThickness(1)
            .Translation(0, 0, 16).Set(native => native.Shadow = new ThemeShadow())
            .MaxWidth(ToastMaxWidth);
    }
}
