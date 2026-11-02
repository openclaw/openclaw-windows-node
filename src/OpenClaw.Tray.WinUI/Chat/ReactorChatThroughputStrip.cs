using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;
using System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatThroughputStripProps(
    OpenClawChatDataProvider? Provider,
    string ThreadId,
    ChatContextUsage? Context,
    bool TurnActive);

/// <summary>
/// Declarative caption strip below the composer: tokens/sec (current, mean, p95) and a
/// context-window bar. Thinking and usage events change throughput without a chat snapshot, so the
/// strip subscribes to the provider's throughput notifications and re-renders itself (avoiding a
/// timeline re-render per reasoning delta). The stats are always read from the provider in
/// <see cref="Render"/>, so they belong to the current <c>ThreadId</c> even right after a switch.
/// </summary>
internal sealed class ReactorChatThroughputStrip : Component<ReactorChatThroughputStripProps>
{
    /// <summary>Viewport width below which the context bar shrinks so the tps text keeps room.</summary>
    private const double NarrowContextBarBreakpoint = 400;

    public override Element Render()
    {
        var props = Props;
        var provider = props.Provider;
        var threadId = props.ThreadId;
        var (viewportWidth, setViewportWidth) = UseState(800d);
        var (_, setRenderVersion) = UseState(0, threadSafe: true);
        var renderVersion = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            if (provider is null)
                return () => { };

            Action<string> onChanged = changedThreadId =>
            {
                if (!string.Equals(changedThreadId, threadId, StringComparison.Ordinal))
                    return;
                lock (renderVersion)
                {
                    renderVersion.Current++;
                    setRenderVersion(renderVersion.Current);
                }
            };
            provider.ThroughputChanged += onChanged;
            return () => provider.ThroughputChanged -= onChanged;
        }), provider ?? (object)string.Empty, threadId);
        var throughput = provider?.GetThroughput(threadId) ?? ChatThroughputStats.Empty;
        var gutter = ChatVisuals.Gutter(viewportWidth);

        var throughputText = TextBlock(ChatUsageFormatter.FormatThroughput(throughput, props.TurnActive))
            .FontSize(12)
            .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
            .TextTrimming(Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center)
            .ToolTip(LocalizationHelper.GetString("Chat_Throughput_Tooltip"))
            .AutomationId("ChatThroughputText");

        var strip = Border(
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    throughputText.Grid(column: 0),
                    ContextGroup(props.Context, viewportWidth).Grid(column: 1)))
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(gutter, -4, gutter, 8)
            .Padding(16, 0)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("ChatThroughputStrip");

        return Border(strip)
            .OnMount(control => ChatVisuals.Observe(control, setViewportWidth))
            .OnUnmount(ChatVisuals.StopObserving);
    }

    private static Element ContextGroup(ChatContextUsage? context, double viewportWidth)
    {
        if (context is not { } ctx)
            return Empty();

        var text = ChatUsageFormatter.Format(ctx.UsedTokens, ctx.ContextTokens, ctx.Percent) ?? string.Empty;
        var label = LocalizationHelper.GetString("Chat_ContextUsage_Tooltip");
        var barWidth = viewportWidth < NarrowContextBarBreakpoint ? 56d : 96d;
        var percent = Math.Clamp(ctx.Percent ?? 0, 0, 100);
        var fillKey = percent >= 90 ? "SystemFillColorCriticalBrush"
            : percent >= 75 ? "SystemFillColorCautionBrush"
            : "AccentFillColorDefaultBrush";

        var bar = Border(
                Border(Empty())
                    .Width(Math.Round(barWidth * percent / 100d))
                    .Height(4)
                    .CornerRadius(2)
                    .HAlign(HorizontalAlignment.Left)
                    .Background(Theme.Ref(fillKey)))
            .Width(barWidth)
            .Height(4)
            .CornerRadius(2)
            .Background(Theme.Ref("ChatStrokeBrush"))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);

        return HStack(6,
                bar,
                TextBlock(text)
                    .FontSize(12)
                    .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                    .VAlign(VerticalAlignment.Center))
            .VAlign(VerticalAlignment.Center)
            .Margin(12, 0, 0, 0)
            .AutomationId("ChatContextUsage")
            .AutomationName($"{label}: {text}")
            .ToolTip(label);
    }
}
