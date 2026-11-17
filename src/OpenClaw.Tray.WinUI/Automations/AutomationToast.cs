using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
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
        var information = notice.IsInformational;
        return (InfoBar(null, message) with
        {
            Severity = notice.IsError ? InfoBarSeverity.Error
                : information ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
            IsClosable = notice.IsError,
            ActionButtonContent = hasAction ? Props.ActionLabel : null,
            OnActionButtonClick = hasAction ? () => { Props.Action!(); dismiss(notice.Id); } : null,
            OnClosed = notice.IsError ? () => dismiss(notice.Id) : null,
        })
            .MaxWidth(ToastMaxWidth)
            .AutomationName(message)
            .AutomationId(latest ? "AutomationsNotice" : $"AutomationToastMessage:{notice.Id}");
    }
}
