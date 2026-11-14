using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatReasoningPickerProps(
    IReadOnlyList<ThinkingLevelOption> Levels,
    string? CurrentLevel,
    string CurrentLabel,
    bool Enabled,
    Action<string> Select,
    string? DefaultLevel = null)
{
    public string? EffectiveLevel => string.IsNullOrEmpty(CurrentLevel) ? DefaultLevel : CurrentLevel;
}

/// <summary>Shows an effort menu using only controller-supplied advertised choices.</summary>
internal sealed class ChatReasoningPicker : Component<ChatReasoningPickerProps>
{
    public override Element Render()
    {
        var props = Props;
        var anchor = UseRef<Button?>(null);
        var active = UseRef(true);
        UseEffect((Func<Action>)(() =>
        {
            active.Current = true;
            return () =>
            {
                active.Current = false;
                if (anchor.Current?.Flyout is MenuFlyout menu)
                    menu.Opening -= RestoreSelection;
                anchor.Current?.Flyout?.Hide();
                anchor.Current = null;
            };
        }), Array.Empty<object>());

        var label = ChatMarkdownPresentation.Localized("Chat_Composer_Effort_Title", "Effort");
        var index = props.Levels.ToList().FindIndex(level => level.Id == props.EffectiveLevel);
        var target = Button(
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    Component<ChatEffortGauge, ChatEffortGaugeProps>(
                        new(index, props.Levels.Count, props.EffectiveLevel == "off")),
                    TextBlock(label).Set(ChatVisuals.StyleFlyoutBody).Margin(12, 0, 0, 0).Grid(column: 1),
                    TextBlock(props.CurrentLabel).Set(ChatVisuals.StylePickerValue).Margin(8, 0)
                        .Grid(column: 2),
                    TextBlock(FluentIconCatalog.ChevronR)
                        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12)
                        .AccessibilityHidden().Grid(column: 3)),
                null)
            // The 1px button border plus this padding equals the stock menu item's 11,4,11,5 padding.
            .SubtleButton().MinHeight(0).Padding(10, 3, 10, 4).CornerRadius(4)
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId("ChatReasoningPicker").AutomationName($"{label}: {props.CurrentLabel}")
            .IsEnabled(props.Enabled && props.Levels.Count > 0)
            .OnMountAdd(control => anchor.Current = (Button)control);

        return MenuFlyout(target, props.Levels.Select(level =>
            (Microsoft.UI.Reactor.Core.MenuFlyoutItemBase)ToggleMenuItem(level.Label, level.Id == props.EffectiveLevel, _ =>
            {
                if (active.Current && Props.Enabled && Props.Levels.Any(option => option.Id == level.Id))
                    Props.Select(level.Id);
            })).ToArray())
            .Set(menu =>
            {
                ChatVisuals.StylePicker(menu);
                menu.Placement = FlyoutPlacementMode.Right;
                menu.Opening -= RestoreSelection;
                menu.Opening += RestoreSelection;
                for (var i = 0; i < menu.Items.Count; i++)
                {
                    menu.Items[i].IsEnabled = Props.Enabled;
                    AutomationProperties.SetAutomationId(menu.Items[i], props.Levels[i].Id);
                }
            });
    }

    private void RestoreSelection(object? sender, object args)
    {
        foreach (var item in ((MenuFlyout)sender!).Items.OfType<ToggleMenuFlyoutItem>())
            item.IsChecked = AutomationProperties.GetAutomationId(item) == Props.EffectiveLevel;
    }
}
