using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatReasoningPickerProps(
    Element Target,
    IReadOnlyList<ThinkingLevelOption> Levels,
    string? CurrentLevel,
    string CurrentLabel,
    bool Enabled,
    Action<string> Select,
    Action Clear,
    double AvailableWidth,
    string? DefaultLevel = null)
{
    public string? EffectiveLevel => string.IsNullOrEmpty(CurrentLevel) ? DefaultLevel : CurrentLevel;
}

/// <summary>Native effort presentation; advertised choices and mutations remain controller-owned.</summary>
internal sealed class ChatReasoningPicker : Component<ChatReasoningPickerProps>
{
    public override Element Render()
    {
        var props = Props;
        var anchor = UseRef<Button?>(null);
        var sliderControl = UseRef<Slider?>(null);
        var inputPolicy = UseRef(new ChatEffortInputPolicy());
        var pointerPressed = UseRef<PointerEventHandler?>(null);
        var pointerReleased = UseRef<PointerEventHandler?>(null);
        var pointerCaptureLost = UseRef<PointerEventHandler?>(null);
        var pointerCanceled = UseRef<PointerEventHandler?>(null);
        var (_, redrawAfterCancellation) = UseState(0L);
        var previewKey = UseRef<KeyEventHandler?>(null);
        void CancelPointer(uint pointer, long revision)
        {
            if (inputPolicy.Current.CancelPointer(pointer, revision))
                redrawAfterCancellation(revision);
        }
        var selectStop = UseRef<Action<double>>(_ => { });
        selectStop.Current = value =>
        {
            var selected = (int)Math.Round(value);
            if (selected >= 0 && selected < props.Levels.Count)
                props.Select(props.Levels[selected].Id);
        };
        var index = props.Levels.ToList().FindIndex(level => level.Id == props.CurrentLevel);
        var sliderIndex = props.Levels.ToList().FindIndex(level => level.Id == props.EffectiveLevel);
        var defaultLabel = ChatMarkdownPresentation.Localized("Chat_Composer_Reasoning_Default", "Default");
        var label = ChatMarkdownPresentation.Localized("Chat_Composer_Effort_Title", "Effort");

        Element choices;
        if (props.Levels.Count > 1)
        {
            choices = VStack(0,
                Slider(inputPolicy.Current.IsPointerActive ? Optional<double>.Unset : (double)Math.Max(0, sliderIndex),
                    0, props.Levels.Count - 1, value =>
                    {
                        var revision = inputPolicy.Current.QueueValueChange();
                        var slider = sliderControl.Current;
                        var selected = Math.Clamp((int)Math.Round(value), 0, props.Levels.Count - 1);
                        var id = props.Levels[selected].Id;
                        slider?.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(sliderControl.Current, slider) && inputPolicy.Current.CanCommit(revision))
                                props.Select(id);
                        });
                    })
                    .TickFrequency(1).TickPlacement(TickPlacement.BottomRight).SnapsTo(SliderSnapsTo.Ticks)
                    .ThumbToolTip(false).MinHeight(32)
                    .AutomationId("ChatReasoningSlider")
                    .AutomationName($"{label}: {props.CurrentLabel}")
                    .OnMountAdd(control =>
                    {
                        var slider = (Slider)control;
                        sliderControl.Current = slider;
                        // Template children own capture and may mark events handled.
                        // Invalidate the value callback queued before this event bubbles.
                        pointerPressed.Current = (_, args) =>
                        {
                            var point = args.GetCurrentPoint(slider);
                            if (args.Pointer.IsInContact && !point.Properties.IsRightButtonPressed
                                && !point.Properties.IsMiddleButtonPressed
                                && !point.Properties.IsXButton1Pressed && !point.Properties.IsXButton2Pressed)
                                inputPolicy.Current.BeginPointer(args.Pointer.PointerId);
                        };
                        pointerReleased.Current = (_, args) =>
                        {
                            if (inputPolicy.Current.EndPointer(args.Pointer.PointerId))
                                selectStop.Current(slider.Value);
                        };
                        pointerCaptureLost.Current = (_, args) =>
                        {
                            var pointer = args.Pointer.PointerId;
                            if (inputPolicy.Current.GetPointerRevision(pointer) is not { } revision)
                                return;
                            if (args.Pointer.IsInContact)
                            {
                                CancelPointer(pointer, revision);
                                return;
                            }
                            // Native Slider releases capture before PointerReleased bubbles here.
                            // Let that event finish, but never commit from capture loss itself.
                            if (!slider.DispatcherQueue.TryEnqueue(() => CancelPointer(pointer, revision)))
                            {
                                CancelPointer(pointer, revision);
                                System.Diagnostics.Debug.WriteLine("Effort pointer cleanup could not be queued. Pending gesture canceled.");
                            }
                        };
                        pointerCanceled.Current = (_, args) =>
                        {
                            var pointer = args.Pointer.PointerId;
                            if (inputPolicy.Current.GetPointerRevision(pointer) is { } revision)
                                CancelPointer(pointer, revision);
                        };
                        previewKey.Current = (_, args) =>
                        {
                            if (Props.Enabled && Props.Levels.Count > 0
                                && !Props.Levels.Any(option => option.Id == Props.EffectiveLevel)
                                && args.Key is global::Windows.System.VirtualKey.Home
                                    or global::Windows.System.VirtualKey.Left or global::Windows.System.VirtualKey.Down
                                    or global::Windows.System.VirtualKey.PageDown)
                            {
                                inputPolicy.Current.CancelPending();
                                selectStop.Current(0);
                                args.Handled = true;
                            }
                        };
                        slider.AddHandler(UIElement.PointerPressedEvent, pointerPressed.Current, true);
                        slider.AddHandler(UIElement.PointerReleasedEvent, pointerReleased.Current, true);
                        slider.AddHandler(UIElement.PointerCaptureLostEvent, pointerCaptureLost.Current, true);
                        slider.AddHandler(UIElement.PointerCanceledEvent, pointerCanceled.Current, true);
                        slider.PreviewKeyDown += previewKey.Current;
                    })
                    .OnUnmount(control =>
                    {
                        inputPolicy.Current.CancelPending();
                        if (pointerPressed.Current is { } pressed)
                            ((Slider)control).RemoveHandler(UIElement.PointerPressedEvent, pressed);
                        if (pointerReleased.Current is { } released)
                            ((Slider)control).RemoveHandler(UIElement.PointerReleasedEvent, released);
                        if (pointerCaptureLost.Current is { } captureLost)
                            ((Slider)control).RemoveHandler(UIElement.PointerCaptureLostEvent, captureLost);
                        if (pointerCanceled.Current is { } canceled)
                            ((Slider)control).RemoveHandler(UIElement.PointerCanceledEvent, canceled);
                        if (previewKey.Current is { } key)
                            ((Slider)control).PreviewKeyDown -= key;
                        pointerPressed.Current = null;
                        pointerReleased.Current = null;
                        pointerCaptureLost.Current = null;
                        pointerCanceled.Current = null;
                        previewKey.Current = null;
                        sliderControl.Current = null;
                    })
                    .IsEnabled(props.Enabled),
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(ChatMarkdownPresentation.Localized("Chat_Composer_Effort_Faster", "Faster"))
                        .FontSize(12).Foreground(Theme.Ref("ChatSecondaryTextBrush")),
                    TextBlock(ChatMarkdownPresentation.Localized("Chat_Composer_Effort_Smarter", "Smarter"))
                        .FontSize(12).Foreground(Theme.Ref("ChatSecondaryTextBrush")).Grid(column: 1)));
        }
        else if (props.Levels.Count == 1)
        {
            var option = props.Levels[0];
            choices = Button(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock(option.Label).FontSize(13).TextWrapping(TextWrapping.Wrap),
                        TextBlock(index == 0 ? FluentIconCatalog.Check : string.Empty)
                            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14)
                            .Set(ChatVisuals.StylePickerAccent).Grid(column: 1)
                            .Set(text => text.IsTextScaleFactorEnabled = false)),
                    () => props.Select(option.Id))
                .SubtleButton().MinHeight(32).Padding(8, 4).CornerRadius(8)
                .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .AutomationId("ChatReasoningSingleChoice").AutomationName(option.Label).IsEnabled(props.Enabled);
        }
        else
        {
            choices = Empty();
        }

        var clear = Button(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(defaultLabel).FontSize(13),
                    TextBlock(string.IsNullOrEmpty(props.CurrentLevel) ? FluentIconCatalog.Check : string.Empty)
                        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14)
                        .Set(ChatVisuals.StylePickerAccent).Grid(column: 1)
                        .Set(text => text.IsTextScaleFactorEnabled = false)),
                () =>
                {
                    inputPolicy.Current.CancelPending();
                    props.Clear();
                    anchor.Current?.Flyout?.Hide();
                })
            .SubtleButton().MinHeight(32).Padding(12, 8).CornerRadius(8)
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId("ChatReasoningDefault").AutomationName(defaultLabel).IsEnabled(props.Enabled);

        return Flyout(props.Target.OnMountAdd(control =>
            {
                anchor.Current = (Button)control;
            }),
            VStack(0,
                VStack(8,
                    Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                        TextBlock(label).FontSize(12).SemiBold().MaxWidth(120).TextWrapping(TextWrapping.Wrap),
                        TextBlock(props.CurrentLabel).FontSize(12).SemiBold().TextWrapping(TextWrapping.Wrap)
                            .Margin(8, 0, 0, 0).HAlign(HorizontalAlignment.Right).TextAlignment(TextAlignment.Right)
                            .Set(ChatVisuals.StylePickerAccent).Grid(column: 1)),
                    choices).Padding(12),
                Border(null).Height(1).Background(Theme.Ref("ChatStrokeBrush")),
                clear.Margin(4))
                .Width(Math.Min(328, Math.Max(200, props.AvailableWidth - 24)))
                .AutomationId("ChatReasoningPopup").AutomationName(label))
            .Set(ChatVisuals.StylePicker);
    }
}
