using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

internal static class UsageExportFeedback
{
    public static Element? Dialog(UsagePageState state, UsagePageController controller, UsageViewport viewport)
    {
        if (state.ExportBlock is not { } block) return null;
        return Component<UsageExportDialog, UsageExportDialogModel>(new(block, controller, viewport));
    }

    internal static Element DialogBody(UsageExportBlock block, double width, bool narrow, Action<bool> close)
    {
        var coverage = block.Coverage.Total is { } total
            ? F("Usage_Export_CoverageFormat", Count(block.Coverage.Loaded), Count(total))
            : F("Usage_Export_CoverageUnknownFormat", Count(block.Coverage.Loaded));
        var badge = Border(TextBlock("\uE7BA").FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(18).Foreground(Caution).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center).AccessibilityHidden())
            .Size(36, 36).CornerRadius(18).Background(CautionTint);
        var factWidth = width;
        return VStack(PageResponsiveLayout.GapStack,
            Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto], badge.Margin(0, 0, PageResponsiveLayout.GapGroup, 0).Grid(column: 0),
                TextBlock(S("Usage_Export_BlockedTitle")).FontSize(20).FontWeight(FontWeights.SemiBold)
                    .TextWrapping(TextWrapping.WrapWholeWords).VAlign(VerticalAlignment.Center)
                    .AutomationId("UsageExportBlockedTitle").Grid(column: 1)),
            TextBlock(S("Usage_Export_BlockedMessage")).TextWrapping(TextWrapping.WrapWholeWords),
            VStack(10,
                Fact(S("Usage_Filter_Range"), RangeFact(block.Filters), factWidth),
                Fact(S("Usage_Sessions_Title"), coverage, factWidth)),
            Border(null).Height(1).Background(CardStroke),
            Grid(narrow ? [GridSize.Star()] : [GridSize.Star(), GridSize.Star()],
                narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                Button(TextBlock(S("Usage_Export_DailyAlternative")).TextWrapping(TextWrapping.NoWrap), () => close(true))
                    .AccentButton().HorizontalContentAlignment(HorizontalAlignment.Center).HAlign(HorizontalAlignment.Stretch)
                    .AutomationName(S("Usage_Export_DailyAlternative")).AutomationId("UsageExportDialogPrimary")
                    .Margin(0, 0, narrow ? 0 : PageResponsiveLayout.GapInner / 2, 0).Grid(row: 0, column: 0),
                Button(TextBlock(S("Usage_Export_Close")).TextWrapping(TextWrapping.NoWrap), () => close(false))
                    .HorizontalContentAlignment(HorizontalAlignment.Center).HAlign(HorizontalAlignment.Stretch)
                    .AutomationName(S("Usage_Export_Close")).AutomationId("UsageExportDialogClose")
                    .Margin(narrow ? 0 : PageResponsiveLayout.GapInner / 2, narrow ? PageResponsiveLayout.GapInner : 0, 0, 0)
                    .Grid(row: narrow ? 1 : 0, column: narrow ? 0 : 1)).AutomationId("UsageExportDialogActions"))
            .Width(width).AutomationId("UsageExportBlockedContent");
    }

    private static Element Fact(string label, string value, double width) =>
        Grid(width < UsagePageLayout.FactStackWidth ? [GridSize.Star()] : [GridSize.Px(120), GridSize.Star()],
            width < UsagePageLayout.FactStackWidth ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
            TextBlock(label).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
            TextBlock(value).FontSize(12).TextWrapping(TextWrapping.WrapWholeWords)
                .Margin(0, width < UsagePageLayout.FactStackWidth ? 4 : 0, 0, 0)
                .Grid(row: width < UsagePageLayout.FactStackWidth ? 1 : 0, column: width < UsagePageLayout.FactStackWidth ? 0 : 1));

    private static string RangeFact(UsageFilters filters) => filters.Preset switch
    {
        UsagePreset.Today => S("Usage_Preset_Today"),
        UsagePreset.Last7Days => S("Usage_Export_Range7d"),
        UsagePreset.Last30Days => S("Usage_Export_Range30d"),
        _ => F("Usage_RangeFormat",
            filters.Start.Year == DateTime.Today.Year ? UsageFormat.DayLabel(filters.Start, Culture) : UsageFormat.FullDate(filters.Start, Culture),
            filters.End.Year == DateTime.Today.Year ? UsageFormat.DayLabel(filters.End, Culture) : UsageFormat.FullDate(filters.End, Culture)),
    };

    public static Element? Toast(UsagePageState state, UsagePageProps props)
    {
        if (state.ExportNotice is not { } notice) return null;
        var failed = notice.Error is not null;
        return Border(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(failed ? "\uE783" : "\uE73E").FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .Foreground(failed ? Critical : Success).FontSize(18).AccessibilityHidden()
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                TextBlock(Message(notice)).TextWrapping(TextWrapping.WrapWholeWords).Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                    .VAlign(VerticalAlignment.Center).AutomationId("UsageExportToastMessage").Grid(column: 1),
                !failed && notice.Folder is { } folder
                    ? SubtleButton(S("Usage_Export_OpenFolder"), () => { _ = props.OpenExportFolder(folder); }, "UsageExportOpenFolder")
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 2)
                    : null))
            .Padding(16, 12).Background(Theme.Ref("FlyoutPresenterBackground"))
            .BorderBrush(Theme.Ref("FlyoutBorderThemeBrush")).BorderThickness(1)
            .Set(control => control.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow())
            .Translation(0, 0, 16)
            .CornerRadius(CardRadius)
            .MaxWidth(PageResponsiveLayout.MinWidth).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom).Margin(PageResponsiveLayout.PaddingNarrow)
            .AutomationId("UsageExportToast");
    }

    internal sealed class UsageExportDialogReference
    {
        public ContentDialog? Dialog { get; set; }
        public bool? Daily { get; set; }
        public event Action? Opened;
        public void NotifyOpened() => Opened?.Invoke();
        public void Dispose()
        {
            Dialog?.Hide();
            Dialog = null;
        }
        public void Close(bool daily)
        {
            Daily = daily;
            Dialog?.Hide();
        }
    }

    internal sealed record UsageExportDialogModel(UsageExportBlock Block, UsagePageController Controller, UsageViewport Viewport);

    internal sealed class UsageExportDialog : Component<UsageExportDialogModel>
    {
        public override Element Render()
        {
            var reference = UseRef(new UsageExportDialogReference()).Current;
            UseEffect((Func<Action>)(() => reference.Dispose), reference);
            return (ContentDialog("", Component<UsageExportDialogBody, UsageExportDialogProps>(new(Props.Block, Props.Viewport, reference)), "") with
            {
                IsOpen = true,
                DefaultButton = ContentDialogButton.None,
                OnOpened = reference.NotifyOpened,
                OnClosed = _ => Props.Controller.CompleteExportAlternative(reference.Daily ?? false),
            }).Set(dialog => reference.Dialog = dialog).AutomationName(S("Usage_Export_BlockedTitle"));
        }
    }

    internal sealed record UsageExportDialogProps(UsageExportBlock Block, UsageViewport Viewport, UsageExportDialogReference Reference);

    internal sealed class UsageExportDialogBody : Component<UsageExportDialogProps>
    {
        public override Element Render()
        {
            var (size, setSize) = UseState((Host: Props.Viewport.Width, Window: Props.Viewport.Width));
            UseEffect((Func<Action>)(() =>
            {
                void Resized() => setSize((Props.Viewport.Width, Props.Reference.Dialog?.XamlRoot?.Size.Width ?? Props.Viewport.Width));
                Props.Viewport.Changed += Resized;
                Props.Reference.Opened += Resized;
                Resized();
                return () =>
                {
                    Props.Viewport.Changed -= Resized;
                    Props.Reference.Opened -= Resized;
                };
            }), Props.Viewport);
            var dialogWidth = Math.Min(UsagePageLayout.DialogMaxWidth, size.Window - UsagePageLayout.DialogWindowInset);
            UseEffect(() =>
            {
                if (Props.Reference.Dialog is { } dialog)
                {
                    dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
                    dialog.Resources["ContentDialogMinWidth"] = 0d;
                }
            }, dialogWidth);
            return UsageExportFeedback.DialogBody(Props.Block, Math.Max(0, dialogWidth - UsagePageLayout.DialogWindowInset),
                PageResponsiveLayout.TierFor(size.Host) == PageWidthTier.Narrow, Props.Reference.Close);
        }
    }

    public static string Message(UsageExportNotice notice) => notice.Error is { } error
        ? S("Usage_Export_Error_" + error) : F("Usage_Export_SavedFormat", notice.FileName ?? "");
}
