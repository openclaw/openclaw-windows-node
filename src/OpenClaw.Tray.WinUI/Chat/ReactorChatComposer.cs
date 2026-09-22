using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// View-only props for the composer. <see cref="Session"/> carries the host-mount
/// <see cref="ChatComposerViewModel"/> and <see cref="ChatComposerController"/>;
/// <see cref="OnSendRequested"/> lets the root bump its own #1089 scroll-follow
/// token before a send is attempted, exactly as the pre-D2 root did inline.
/// </summary>
internal sealed record ReactorChatComposerViewProps(
    ChatComposerSession Session,
    ChatComposerInputs Inputs,
    ChatDataSnapshot InputSnapshot,
    Action OnSendRequested,
    bool IsCompact);

/// <summary>
/// Declarative Reactor view for the composer. It owns control construction, popup/
/// control references, caret and focus application, keyboard forwarding, automation
/// properties, and theme/high-contrast resource application. It holds no draft/send/
/// voice/slash workflow state, calls no provider API directly, and performs no
/// lifecycle parsing or attachment security decisions: all of that lives in
/// <see cref="ChatComposerViewModel"/> and <see cref="ChatComposerController"/>,
/// which it reads and calls through <see cref="ReactorChatComposerViewProps.Session"/>.
/// </summary>
internal sealed class ReactorChatComposer : Component<ReactorChatComposerViewProps>
{

    public override Element Render()
    {
        var props = Props;
        var vm = props.Session.ViewModel;
        var controller = props.Session.Controller;
        var inputs = props.Inputs;
        var colorScheme = UseColorScheme();
        var (viewportWidth, setViewportWidth) = UseState(props.IsCompact ? 480d : 800d);

        // The Reactor view subscribes to the view model exactly once per mount and
        // unsubscribes on unmount. This render-invalidation counter is an adapter
        // detail: it is not a second copy of composer state, only a re-render token.
        var (renderRevision, setRenderRevision) = UseState(vm.RenderRevision, threadSafe: true);
        var inputControl = UseRef<TextBox?>(null);
        var slashPopup = UseRef<Microsoft.UI.Xaml.Controls.Primitives.Popup?>(null);
        var slashPopupContentRef = UseRef<(string Key, FrameworkElement? Content)>((string.Empty, null));
        var controllerRef = UseRef(controller);
        controllerRef.Current = controller;
        var pasteHandler = UseRef<TextControlPasteEventHandler>(async (_, args) =>
        {
            if (GetBitmapClipboardContent() is not { } clipboardContent)
                return;

            // Paste is a synchronous routed event. Suppress the default text paste
            // before awaiting bitmap extraction so a multi-format clipboard cannot
            // insert text alongside the image attachment.
            args.Handled = true;
            await controllerRef.Current.PasteImageAsync(clipboardContent);
        });

        UseEffect((Func<Action>)(() =>
        {
            void OnChanged(object? sender, PropertyChangedEventArgs args) => setRenderRevision(vm.RenderRevision);
            vm.PropertyChanged += OnChanged;
            if (renderRevision != vm.RenderRevision)
                setRenderRevision(vm.RenderRevision);
            return () =>
            {
                vm.PropertyChanged -= OnChanged;
                CloseSlashPopup(slashPopup);
            };
        }), Array.Empty<object>());

        UseEffect((Func<Action>)(() =>
        {
            props.Session.ApplyInputs(inputs);
            return static () => { };
        }), props.InputSnapshot, inputs.CurrentThread);

        var text = vm.Draft;
        var isSending = vm.IsSending;
        var isRecording = vm.IsRecording;
        var slashDisplay = vm.SlashDisplay;

        void FocusAndPlaceCaretAtEnd()
        {
            inputControl.Current?.DispatcherQueue?.TryEnqueue(() =>
            {
                if (inputControl.Current is not { } textBox)
                    return;

                textBox.Focus(FocusState.Programmatic);
                var caret = textBox.Text?.Length ?? 0;
                textBox.SelectionStart = caret;
                textBox.SelectionLength = 0;
            });
        }

        void CommitSlash(string value, ReactorSlashMenuState nextState)
        {
            vm.CommitSlashText(value, nextState);
            FocusAndPlaceCaretAtEnd();
        }

        UseEffect((Func<Action>)(() =>
        {
            if (vm.ShouldRequestCatalogOnOpen())
                controller.RequestCommandCatalog();
            return static () => { };
        }), slashDisplay.ShouldRequestCatalog);
        UseEffect((Func<Action>)(() =>
        {
            vm.ReconcileAfterCatalogRefresh();
            return static () => { };
        }), inputs.AvailableCommands);

        void Send()
        {
            if (!vm.CanSend)
                return;

            props.OnSendRequested();
            _ = controller.SendAsync();
        }

        var modelChoices = inputs.ModelChoices is { Count: > 0 }
            ? inputs.ModelChoices
            : inputs.AvailableModels
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => new ChatModelChoice(model, model))
                .ToArray();
        var catalogModels = modelChoices.ToArray();
        var defaultReasoningLabel = Localized("Chat_Composer_Reasoning_Default", "Default");
        var selectedModel = catalogModels.FirstOrDefault(
            model => model.MatchesModel(inputs.CurrentThread.Model, inputs.CurrentThread.ModelProvider));
        var thinkingLevels = inputs.ThinkingProfile?.Levels?.ToArray() ?? [];
        var knownThinkingIndex = Array.FindIndex(thinkingLevels, option => option.Id == inputs.CurrentThread.ThinkingLevel);
        var thinkingIndex = string.IsNullOrEmpty(inputs.CurrentThread.ThinkingLevel)
            ? 0
            : knownThinkingIndex < 0 ? -1 : knownThinkingIndex + 1;
        var thinkingNames = new[] { defaultReasoningLabel }
            .Concat(thinkingLevels.Select(option => option.Label))
            .ToArray();
        var thinkingLabel = thinkingIndex < 0 ? inputs.CurrentThread.ThinkingLevel! : thinkingNames[thinkingIndex];
        var compactEffort = viewportWidth <= ChatVisuals.CompactEffortBreakpoint;
        var compactSession = viewportWidth < ChatVisuals.CompactSessionBreakpoint;
        var actionLabel = inputs.TurnActive
            ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
            : Localized("Chat_Composer_Tooltip_Send", "Send");
        const double controlCornerRadius = 4;

        Element IconButton(
            string glyph,
            string automationName,
            Action onClick,
            bool enabled = true,
            string? automationId = null,
            string? itemStatus = null)
        {
            return Button(
                    TextBlock(glyph)
                        .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(16)
                        .Set(text => text.IsTextScaleFactorEnabled = false),
                    onClick)
                .AutomationName(automationName)
                .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .Resources(ChatVisuals.ToolbarButtonResources)
                .Width(32)
                .Height(32)
                .MinWidth(32)
                .MinHeight(32)
                .Padding(0)
                .CornerRadius(controlCornerRadius)
                .IsEnabled(enabled)
                .BorderThickness(0)
                .AutomationId(string.IsNullOrWhiteSpace(automationId) ? string.Empty : automationId)
                .ToolTip(automationName)
                .Set(button =>
                {
                    ComposerAutomationVisibility.Prepare(button);
                    if (itemStatus is not null)
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, itemStatus);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        Element PickerButton(
            string label,
            string automationName,
            string automationId,
            bool enabled,
            double maxLabelWidth,
            string? itemStatus = null)
        {
            return Button(
                    Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        TextBlock(label)
                            .MaxWidth(maxLabelWidth)
                            .FontSize(13)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .TextWrapping(TextWrapping.NoWrap)
                            .HAlign(HorizontalAlignment.Stretch)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(column: 0),
                        TextBlock(FluentIconCatalog.ChevronDown)
                            .Margin(4, 0, 0, 0)
                            .VAlign(VerticalAlignment.Center)
                            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                            .FontSize(12)
                            .Set(text => text.IsTextScaleFactorEnabled = false)
                            .Grid(column: 1)),
                    () => { })
                .AutomationName(automationName)
                .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .Resources(ChatVisuals.ToolbarButtonResources)
                .MinHeight(32)
                .MinWidth(0)
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Stretch)
                .CornerRadius(controlCornerRadius)
                .IsEnabled(enabled)
                .BorderThickness(0)
                .AutomationId(automationId)
                .ToolTip(automationName)
                .Set(button =>
                {
                    button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    ComposerAutomationVisibility.Prepare(button);
                    if (itemStatus is not null)
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, itemStatus);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        Element EffortIconButton()
        {
            var effectiveLevel = string.IsNullOrEmpty(inputs.CurrentThread.ThinkingLevel)
                ? inputs.ThinkingProfile?.Default : inputs.CurrentThread.ThinkingLevel;
            var gaugeIndex = Array.FindIndex(thinkingLevels, option => option.Id == effectiveLevel);
            // Resource overrides survive Reactor's generated theme-binding style on updates.
            return Button(HStack(4,
                    Component<ChatEffortGauge, ChatEffortGaugeProps>(new(gaugeIndex, thinkingLevels.Length, effectiveLevel == "off")),
                    TextBlock(FluentIconCatalog.ChevronDown).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12)
                        .VAlign(VerticalAlignment.Center)
                        .AccessibilityView(AccessibilityView.Raw)
                        .Set(text => text.IsTextScaleFactorEnabled = false)), () => { })
                .Resources(ChatVisuals.ToolbarButtonResources)
                .Width(44).MinWidth(44).Height(44).Padding(4, 0).CornerRadius(controlCornerRadius).BorderThickness(0)
                .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .AutomationId("ChatComposerReasoningPicker")
                .AutomationName($"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {thinkingLabel}")
                .ToolTip($"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {thinkingLabel}")
                .IsEnabled(inputs.CanChangeThinking)
                .Set(button => ComposerAutomationVisibility.Prepare(button))
                .OnUnmount(control => ComposerAutomationVisibility.Detach((FrameworkElement)control));
        }

        var attachmentRows = vm.PendingAttachments
            .Select(attachment =>
                (Element)Border(Grid(
                    [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    TextBlock(FluentIconCatalog.Document).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(16).Margin(0, 0, 8, 0).VAlign(VerticalAlignment.Center)
                        .Set(text => text.IsTextScaleFactorEnabled = false).Grid(column: 0),
                    TextBlock(attachment.FileName).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .ToolTip(attachment.FileName).VAlign(VerticalAlignment.Center).Grid(column: 1),
                    IconButton(FluentIconCatalog.Exit,
                        Localized("Chat_Attachment_Remove", "Remove attachment"),
                        () => controller.RemoveAttachment(attachment)).Grid(column: 2)))
                    .Padding(8, 4).CornerRadius(8)
                    .Width(Math.Min(240, Math.Max(128, viewportWidth - 2 * ChatVisuals.Gutter(viewportWidth) - 32)))
                    .MinHeight(56).Margin(4)
                    .Background(Theme.Ref("ChatCardBrush")))
            .ToArray();
        var audioLevel = Math.Clamp(vm.VoiceAudioLevel, 0f, 1f);
        var voiceFeedbackText = string.IsNullOrWhiteSpace(vm.VoiceTranscript)
            ? Localized("Chat_Voice_ListeningPrompt", "Listening…")
            : vm.VoiceTranscript;
        var waveformBars = Enumerable.Range(0, 8)
            .Select(index =>
                (Element)Border(Empty())
                    .Width(4)
                    .Height(4 + 4 * Math.Round(audioLevel * (index % 3 == 1 ? 3 : 2)))
                    .CornerRadius(2)
                    .VAlign(VerticalAlignment.Center)
                    .Background(Theme.SecondaryText))
            .ToArray();
        Element voiceFeedback = !isRecording
            ? Empty()
            : Border(
                    Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        ScrollViewer(TextBlock(voiceFeedbackText)
                            .FontSize(12).TextWrapping(TextWrapping.Wrap)
                            .Foreground(Theme.Ref("ChatSecondaryTextBrush")))
                            .MaxHeight(80)
                            .Set(scroll =>
                            {
                                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                                scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                            }).Grid(column: 0),
                        HStack(4, waveformBars).Margin(12, 0, 0, 0).Grid(column: 1)))
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Stretch);
        var queuedRows = inputs.QueuedMessages
            .Select((message, index) =>
            {
                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var actionKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailed"
                    : "Chat_Composer_QueuedMessageCancel";
                var actionAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageCancelAutomationFormat";
                var rowAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageAutomationFormat";
                var action = message.SendState == ChatQueuedMessageSendState.Sending
                    ? Empty()
                    : Button(TextBlock(FluentIconCatalog.Exit)
                            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                            .FontSize(12).Set(text => text.IsTextScaleFactorEnabled = false),
                            () => controller.CancelQueuedMessage(message.Id))
                        .SubtleButton()
                        .MinWidth(32).MinHeight(32).Padding(4)
                        .ToolTip(Localized(actionKey, failed ? "Remove failed message" : "Cancel"))
                        .AutomationId($"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}")
                        .AutomationName(string.Format(
                            CultureInfo.CurrentCulture,
                            Localized(actionAutomationKey, "{0}: {1}"),
                            index + 1,
                            message.Text));
                var state = failed
                    ? (Element)TextBlock(Localized("Chat_Composer_QueuedMessageFailed", "Failed"))
                        .FontSize(12)
                    : Empty();
                var error = failed && !string.IsNullOrWhiteSpace(message.ErrorText)
                    ? (Element)TextBlock(message.ErrorText!).FontSize(12).TextWrapping(TextWrapping.Wrap)
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    : Empty();
                return (Element)Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        VStack(
                                4,
                                state,
                                TextBlock(message.Text).FontSize(12).TextWrapping(TextWrapping.Wrap),
                                error)
                            .HAlign(HorizontalAlignment.Stretch).Grid(column: 0),
                        action.Grid(column: 1))
                    .AutomationName(string.Format(
                        CultureInfo.CurrentCulture,
                        Localized(rowAutomationKey, "{0}"),
                        message.Text));
            })
            .ToArray();
        var queuedCountText = queuedRows.Length == 1
            ? Localized("Chat_Composer_QueuedCountSingle", "1 queued message")
            : string.Format(
            CultureInfo.CurrentCulture,
            Localized("Chat_Composer_QueuedCountFormat", "{0} queued messages"),
            queuedRows.Length);
        Element queuedPanel = queuedRows.Length == 0
            ? Empty()
            : Border(
                    VStack(
                        8,
                        TextBlock(queuedCountText)
                            .FontSize(13)
                            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                        ScrollView(VStack(4, queuedRows))
                            .MaxHeight(viewportWidth < ChatVisuals.FooterBreakpoint ? 144 : 220)
                            .Set(scrollView =>
                            {
                                scrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                                scrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                                scrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
                                scrollView.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                            })))
                .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)
                .AutomationName(queuedCountText)
                .Padding(8).CornerRadius(8)
                .Background(Theme.Ref("ChatCardBrush"));

        var slashPopupVisible = slashDisplay.IsVisible
            && (slashDisplay.IsLoading
                || (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is not null)
                || slashDisplay.Commands.Count > 0);
        var popupCatalogKey = inputs.AvailableCommands is null
            ? "missing"
            : RuntimeHelpers.GetHashCode(inputs.AvailableCommands).ToString(CultureInfo.InvariantCulture);
        var popupArgumentCommandKey = slashDisplay.ArgCommand?.Name
            ?? slashDisplay.ArgCommand?.DisplayName()
            ?? string.Empty;
        var popupStateKey = string.Join(
            "|",
            slashPopupVisible,
            slashDisplay.IsLoading,
            slashDisplay.IsArgsMode,
            popupArgumentCommandKey,
            slashDisplay.Query,
            slashDisplay.SelectedIndex,
            slashDisplay.SelectableCount,
            popupCatalogKey,
            colorScheme);
        FrameworkElement? slashPopupContent;
        if (!slashPopupVisible)
        {
            slashPopupContentRef.Current = (string.Empty, null);
            slashPopupContent = null;
        }
        else if (slashPopupContentRef.Current.Key == popupStateKey)
        {
            slashPopupContent = slashPopupContentRef.Current.Content;
        }
        else if (slashDisplay.IsLoading)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashHintPopup(
                Localized("Chat_Composer_Slash_Loading", "Loading commands...")));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else if (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is { } argCommand)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashArgPopup(
                argCommand,
                slashDisplay.ArgChoices,
                slashDisplay.SelectedIndex,
                choice => CommitSlash(
                    argCommand.BuildArgInsertionText(choice.Value),
                    ReactorSlashMenuState.Closed)));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashPopup(
                slashDisplay.Groups,
                slashDisplay.SelectedIndex,
                slashDisplay.Query,
                colorScheme,
                command =>
                {
                    CommitSlash(
                        command.FirstArgChoices().Count > 0 ? command.DisplayName() + " " : command.BuildInsertionText(),
                        command.FirstArgChoices().Count > 0
                            ? new ReactorSlashMenuState(true, string.Empty, 0, true)
                            : ReactorSlashMenuState.Closed);
                }));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }

        var transparentInputBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var input = TextBox(
                text,
                vm.SetDraft,
                PlaceholderFor(inputs.ConnectionState))
            .AutomationId("ChatComposerInput")
            .AutomationName(PlaceholderFor(inputs.ConnectionState))
            .OnKeyDown((sender, args) =>
            {
                if (slashDisplay.IsVisible)
                {
                    switch (args.Key)
                    {
                        case global::Windows.System.VirtualKey.Down when slashDisplay.HasSelection:
                            args.Handled = true;
                            vm.MoveSlashSelection(1);
                            return;

                        case global::Windows.System.VirtualKey.Up when slashDisplay.HasSelection:
                            args.Handled = true;
                            vm.MoveSlashSelection(-1);
                            return;

                        case global::Windows.System.VirtualKey.Enter:
                        case global::Windows.System.VirtualKey.Tab:
                            if (slashDisplay.HasSelection)
                            {
                                args.Handled = true;
                                var commit = vm.CommitSelectedSlashItem();
                                if (commit.Accepted)
                                    FocusAndPlaceCaretAtEnd();
                                return;
                            }

                            if (slashDisplay.IsLoading)
                            {
                                args.Handled = true;
                                if (args.Key == global::Windows.System.VirtualKey.Tab)
                                    vm.DismissSlashMenu();
                                return;
                            }
                             break;

                        case global::Windows.System.VirtualKey.Escape:
                            args.Handled = true;
                            vm.DismissSlashMenu();
                            return;
                    }

                    if (slashDisplay.IsLoading
                        && (args.Key == global::Windows.System.VirtualKey.Up
                            || args.Key == global::Windows.System.VirtualKey.Down))
                    {
                        args.Handled = true;
                        return;
                    }
                }

                if (args.Key != global::Windows.System.VirtualKey.Enter)
                    return;

                args.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    global::Windows.System.VirtualKey.Shift);
                if (shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)
                    && sender is Microsoft.UI.Xaml.Controls.TextBox textBox)
                {
                    var current = textBox.Text ?? string.Empty;
                    var start = Math.Clamp(textBox.SelectionStart, 0, current.Length);
                    var end = Math.Clamp(start + textBox.SelectionLength, start, current.Length);
                    vm.SetDraft(current[..start] + "\n" + current[end..]);
                    textBox.SelectionStart = start + 1;
                    textBox.SelectionLength = 0;
                    return;
                }

                Send();
            })
            .TextWrapping(TextWrapping.Wrap)
            .MinHeight(44)
            .MaxHeight(200)
            .Padding(0)
            .IsEnabled(inputs.ConnectionState == "connected")
            .BorderThickness(0)
            .BorderBrush(transparentInputBrush)
            .Background(transparentInputBrush)
            .AcceptsReturn(false)
            .FontSize(16)
            .Foreground(Theme.Ref("ChatTextBrush"))
            .Set(control =>
            {
                inputControl.Current = control;
                control.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
                control.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
                control.Resources["TextControlBackground"] = transparentInputBrush;
                control.Resources["TextControlBackgroundFocused"] = transparentInputBrush;
                control.Resources["TextControlBackgroundPointerOver"] = transparentInputBrush;
                control.Resources["TextControlBorderBrush"] = transparentInputBrush;
                control.Resources["TextControlBorderBrushFocused"] = transparentInputBrush;
                control.Resources["TextControlBorderBrushPointerOver"] = transparentInputBrush;
                ComposerAutomationVisibility.Prepare(control);
            })
            .OnMount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste += pasteHandler.Current;
                textBox.ContextFlyout = CreateComposerContextFlyout(
                    textBox,
                    () => controllerRef.Current);
            })
            .OnUnmount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste -= pasteHandler.Current;
                textBox.ContextFlyout = null;
                ComposerAutomationVisibility.Detach(textBox);
            });
        UseEffect((Func<Action>)(() =>
        {
            if (inputControl.Current is { } anchor)
                DriveSlashPopup(slashPopup, anchor, slashPopupContent, slashPopupVisible);
            else
                CloseSlashPopup(slashPopup);
            return static () => { };
        }), popupStateKey);

        var sessionItemStatus = GatewayFixtureRenderObservation.Create(
            props.InputSnapshot, inputs.CurrentThread.Id, GatewayFixtureIsolation.IsEnabled);
        var sessionPicker = MenuFlyout(
            compactSession
                ? IconButton(FluentIconCatalog.Sessions,
                    $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {inputs.CurrentThread.Title}",
                    () => { }, !inputs.MessageOptionsDisabled && inputs.AvailableChannels.Count > 1,
                    "ChatComposerSessionPicker", sessionItemStatus)
                : PickerButton(
                inputs.CurrentThread.Title,
                $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {inputs.CurrentThread.Title}",
                "ChatComposerSessionPicker",
                !inputs.MessageOptionsDisabled && inputs.AvailableChannels.Count > 1,
                viewportWidth < ChatVisuals.FooterBreakpoint ? 80 : 120,
                sessionItemStatus),
            inputs.AvailableChannels
                .Select(thread => RadioMenuItem(
                    thread.Title,
                    "chat-sessions",
                    string.Equals(thread.Id, inputs.CurrentThread.Id, StringComparison.Ordinal),
                    () => controller.SelectChannel(thread.Id)))
                .ToArray())
            .Set(ChatVisuals.StylePicker);

        var modelPickerLabel = string.IsNullOrWhiteSpace(inputs.CurrentThread.Model)
            ? catalogModels.FirstOrDefault(model => model.IsDefault)?.DisplayName
                ?? Localized("Chat_Composer_Accessibility_Model", "Model")
            : selectedModel?.DisplayName
                ?? ChatModelChoice.BuildSelectionId(inputs.CurrentThread.Model!, inputs.CurrentThread.ModelProvider);
        var modelPicker = Component<ChatModelPicker, ChatModelPickerProps>(new(
            PickerButton(
                modelPickerLabel,
                $"{Localized("Chat_Composer_Accessibility_Model", "Model")}: {modelPickerLabel}",
                "ChatComposerModelPicker",
                inputs.CanChangeSessionOptions,
                200)
                .MinWidth(compactEffort ? 44 : 0)
                .MinHeight(compactEffort ? 44 : 32)
                .Padding(compactEffort ? 0 : 8, compactEffort ? 0 : 4),
            catalogModels, inputs.CurrentThread.Model, inputs.CurrentThread.ModelProvider,
            inputs.CanChangeSessionOptions,
            choice => controller.SetModel(choice.SelectionId),
            viewportWidth));

        var reasoningPicker = Component<ChatReasoningPicker, ChatReasoningPickerProps>(new(
            compactEffort ? EffortIconButton() : PickerButton(
                thinkingLabel,
                $"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {thinkingLabel}",
                "ChatComposerReasoningPicker",
                inputs.CanChangeThinking,
                96),
            thinkingLevels, inputs.CurrentThread.ThinkingLevel, thinkingLabel, inputs.CanChangeThinking,
            controller.SetThinkingLevel, controller.ClearThinkingLevel, viewportWidth,
            inputs.ThinkingProfile?.Default));

        var attachButton = IconButton(
            FluentIconCatalog.ChatAttach,
            Localized("Chat_Composer_Tooltip_Attach", "Attach"),
            () => props.Session.HostActions.AttachmentPickerRequest?.Invoke(),
            props.Session.HostActions.AttachmentPickerRequest is not null,
            "ChatComposerAttach");
        var voiceButton = IconButton(
            isRecording
                ? FluentIconCatalog.Stop
                : FluentIconCatalog.VoiceAct,
            isRecording
                ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
                : Localized("Chat_Composer_Tooltip_Voice", "Voice"),
            () =>
            {
                if (isRecording)
                    controller.StopVoiceRecording();
                else
                    controller.StartVoiceRecording();
            },
            props.Session.HostActions.VoiceCaptureRequest is not null,
            "ChatComposerVoice");

        Element primaryAction = Button(
                    TextBlock(inputs.TurnActive ? FluentIconCatalog.Stop : FluentIconCatalog.ChatSubmit)
                        .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(16)
                        .Set(text => text.IsTextScaleFactorEnabled = false),
                    () =>
                    {
                        if (inputs.TurnActive)
                            controller.Stop();
                        else
                            Send();
                    })
                .AccentButton()
                .AutomationName(actionLabel)
                .Width(32)
                .Height(32)
                .MinWidth(32)
                .MinHeight(32)
                .Padding(0)
                .CornerRadius(16)
                .AutomationId("ChatComposerPrimaryAction")
                .IsEnabled(inputs.TurnActive || vm.CanSend)
                .ToolTip(actionLabel)
                .Set(button => ComposerAutomationVisibility.Prepare(button))
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));

        var leading = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            attachButton.Grid(column: 0),
            sessionPicker.Margin(compactSession ? 0 : 4, 0, 0, 0).Grid(column: 1))
            .MaxWidth(compactSession ? 64 : 184)
            .HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);
        var pickers = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            Grid([GridSize.Star()], [GridSize.Auto], modelPicker).Grid(column: 0),
            Grid([GridSize.Star()], [GridSize.Auto], reasoningPicker).Grid(column: 1))
            .MaxWidth(320)
            .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center);
        var rightToolbar = HStack(4, voiceButton, primaryAction)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);
        var toolbar = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            leading.Margin(0, 0, compactSession ? 0 : 4, 0).Grid(column: 0),
            pickers.Grid(column: 1),
            rightToolbar.Margin(4, 0, 0, 0).Grid(column: 2));

        var composerChildren = new List<Element>();
        if (inputs.ConnectionState != "connected")
            composerChildren.Add(TextBlock(PlaceholderFor(inputs.ConnectionState))
                .FontSize(12).TextWrapping(TextWrapping.Wrap)
                .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .AutomationId("ChatComposerConnectionStatus")
                .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite));
        if (isRecording)
            composerChildren.Add(voiceFeedback);
        if (attachmentRows.Length > 0)
            composerChildren.Add(ScrollViewer(HStack(8, attachmentRows))
                .AutomationId("ChatAttachmentRail")
                .Set(scroll =>
                {
                    scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    scroll.HorizontalContentAlignment = HorizontalAlignment.Left;
                }));
        if (queuedRows.Length > 0)
            composerChildren.Add(queuedPanel);
        composerChildren.Add(input);
        composerChildren.Add(toolbar);

        var writingSurface = Border(
            VStack(8, composerChildren.ToArray())
            .Padding(16, 12, 16, 8))
            .BorderThickness(1)
            .CornerRadius(ChatVisuals.ComposerRadius)
            .MinHeight(112)
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(ChatVisuals.Gutter(viewportWidth), 12)
            .Background(Theme.Ref("ChatComposerBrush"))
            .BorderBrush(Theme.Ref("ChatStrokeBrush"))
            .HAlign(HorizontalAlignment.Stretch);
        return Border(writingSurface)
            .OnMount(control => ChatVisuals.Observe(control, setViewportWidth))
            .OnUnmount(ChatVisuals.StopObserving);
    }

    private static void CloseSlashPopup(Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef)
    {
        if (popupRef.Current is not { } popup)
            return;

        popup.IsOpen = false;
        if (popup.Child is ReactorHostControl host)
            host.Dispose();
        popup.Child = null;
        popup.PlacementTarget = null;
    }

    private static ReactorHostControl CreateSlashPopupHost(Element content)
    {
        var host = new ReactorHostControl();
        host.Mount(_ => content);
        return host;
    }

    private static void DriveSlashPopup(
        Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef,
        TextBox anchor,
        FrameworkElement? content,
        bool visible)
    {
        var popup = popupRef.Current;
        if (popup is null)
        {
            popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
            {
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true,
            };
            popupRef.Current = popup;
        }

        if (!visible || content is null || anchor.XamlRoot is null)
        {
            CloseSlashPopup(popupRef);
            return;
        }

        content.Width = Math.Max(280, anchor.ActualWidth > 0 ? anchor.ActualWidth : 360);
        popup.XamlRoot = anchor.XamlRoot;
        popup.PlacementTarget = anchor;
        popup.DesiredPlacement = Microsoft.UI.Xaml.Controls.Primitives.PopupPlacementMode.Top;
        if (popup.Child is ReactorHostControl previousHost
            && !ReferenceEquals(previousHost, content))
            previousHost.Dispose();
        popup.Child = content;
        popup.IsOpen = true;
    }

    private static Element BuildSlashHintPopup(string text)
    {
        return SlashShell(
            TextBlock(text)
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .Margin(8, 6, 8, 6));
    }

    private static Element BuildSlashPopup(
        IReadOnlyList<CommandCategoryGroup> groups,
        int selectedIndex,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var rows = new List<Element>();
        var index = 0;
        foreach (var group in groups)
        {
            rows.Add(SlashCategoryHeader(CommandCategories.Label(group.Category)));
            foreach (var command in group.Commands)
            {
                rows.Add(SlashRow(command, index == selectedIndex, query, colorScheme, onPick));
                index++;
            }
        }

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashCategoryHeader(string text)
    {
        return TextBlock((text ?? string.Empty).ToUpperInvariant())
            .FontSize(11)
            .SemiBold()
            .CharacterSpacing(60)
            .Foreground(Theme.TertiaryText)
            .Margin(8, 8, 8, 2);
    }

    private static Element BuildSlashArgPopup(
        GatewayCommand command,
        IReadOnlyList<GatewayCommandArgChoice> choices,
        int selectedIndex,
        Action<GatewayCommandArgChoice> onPick)
    {
        var argDescription = command.Args?.FirstOrDefault()?.Description;
        var headerText = !string.IsNullOrWhiteSpace(argDescription)
            ? $"{command.DisplayName()}  {argDescription}"
            : !string.IsNullOrWhiteSpace(command.Description)
                ? $"{command.DisplayName()}  {command.Description}"
                : command.DisplayName();
        var rows = new List<Element>
        {
            TextBlock(headerText)
                .FontSize(11)
                .SemiBold()
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .Foreground(Theme.TertiaryText)
                .Margin(8, 6, 8, 2),
        };
        for (var index = 0; index < choices.Count; index++)
            rows.Add(SlashArgRow(command, choices[index], index == selectedIndex, onPick));

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashArgRow(
        GatewayCommand command,
        GatewayCommandArgChoice choice,
        bool selected,
        Action<GatewayCommandArgChoice> onPick)
    {
        var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label;
        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                HStack(
                    8,
                    TextBlock(label)
                        .FontSize(13)
                        .SemiBold()
                        .VAlign(VerticalAlignment.Center)
                        .Foreground(Theme.PrimaryText),
                    TextBlock($"{command.DisplayName()} {choice.Value}")
                        .FontSize(12)
                        .VAlign(VerticalAlignment.Center)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .MaxLines(1)
                        .Foreground(Theme.SecondaryText)),
                () => onPick(choice))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Choose {label} for {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .HorizontalContentAlignment(HorizontalAlignment.Left)
            .BorderThickness(0)
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashShell(Element child)
    {
        return Border(child)
            .Padding(4)
            .CornerRadius(8)
            .Background(Theme.Ref("AcrylicBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.Ref("SurfaceStrokeColorFlyoutBrush"), 1)
            .Translation(0, 0, 32)
            .Set(border => border.Shadow = new ThemeShadow());
    }

    private static Element SlashRow(
        GatewayCommand command,
        bool selected,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var cells = new List<Element>
        {
            TextBlock(SlashGlyph(command))
                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.SecondaryText)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                .Grid(row: 0, column: 0),
            TextBlock(command.DisplayName())
                .FontSize(13)
                .SemiBold()
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.PrimaryText)
                .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                .Grid(row: 0, column: 1),
        };
        var args = command.ArgTemplate();
        if (!string.IsNullOrWhiteSpace(args))
        {
            cells.Add(
                TextBlock(args)
                    .FontSize(12)
                    .FontFamily("Consolas")
                    .VAlign(VerticalAlignment.Center)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 2));
        }

        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            cells.Add(
                TextBlock(command.Description!)
                    .FontSize(12)
                    .VAlign(VerticalAlignment.Center)
                    .HAlign(HorizontalAlignment.Right)
                    .TextAlignment(TextAlignment.Right)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1)
                    .Foreground(Theme.SecondaryText)
                    .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                    .Grid(row: 0, column: 3));
        }

        var options = command.OptionCount();
        if (options > 0)
        {
            cells.Add(SlashBadge($"{options} options").Grid(row: 0, column: 4));
        }

        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                Grid(
                    [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    cells.ToArray())
                    .Set(grid => grid.ColumnSpacing = 8)
                    .VAlign(VerticalAlignment.Center),
                () => onPick(command))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Insert {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .BorderThickness(0)
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashBadge(string text)
    {
        return Border(
                TextBlock(text)
                    .FontSize(10)
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")))
            .Padding(6, 1, 6, 1)
            .CornerRadius(4)
            .VAlign(VerticalAlignment.Center)
            .Background(Theme.AccentSecondary);
    }

    private static void ApplyQueryHighlight(TextBlock textBlock, string? query, ColorScheme colorScheme)
    {
        textBlock.TextHighlighters.Clear();
        var text = textBlock.Text ?? string.Empty;
        var normalized = (query ?? string.Empty).Trim().TrimStart('/').Trim();
        if (normalized.Length == 0 || text.Length < normalized.Length || colorScheme == ColorScheme.HighContrast)
            return;

        var isDark = colorScheme == ColorScheme.Dark;
        if (ThemeRef.Resolve("AccentFillColorDefaultBrush", isDark) is not SolidColorBrush accent
            || ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush foreground)
            return;

        var accentColor = accent.Color;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(31, accentColor.R, accentColor.G, accentColor.B)),
            Foreground = foreground,
        };

        for (var index = 0; index <= text.Length - normalized.Length;)
        {
            var found = text.IndexOf(normalized, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;
            highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
            {
                StartIndex = found,
                Length = normalized.Length,
            });
            index = found + normalized.Length;
        }

        if (highlighter.Ranges.Count > 0)
            textBlock.TextHighlighters.Add(highlighter);
    }

    private static string SlashGlyph(GatewayCommand command)
    {
        var name = (command.NativeName ?? command.DisplayName()).Trim().TrimStart('/').ToLowerInvariant()
            .Replace(':', '_')
            .Replace('.', '_')
            .Replace('-', '_');
        return name switch
        {
            "help" or "commands" => "\uE82D",
            "status" or "usage" => "\uE9D9",
            "export" or "export_session" => "\uE896",
            "skill" or "fast" => "\uE945",
            "model" or "models" or "think" => "\uE713",
            "new" => "\uE710",
            "reset" or "redirect" => "\uE72C",
            "compact" => "\uE9F3",
            "stop" => "\uE71A",
            "clear" => "\uE74D",
            "agents" => "\uE7F4",
            "subagents" => "\uE8B7",
            "steer" => "\uE724",
            "tts" => "\uE767",
            _ => "\uE756",
        };
    }

    private static global::Windows.ApplicationModel.DataTransfer.DataPackageView? GetBitmapClipboardContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && content.Contains(
                    global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    ? content
                    : null;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return null;
        }
    }

    private static MenuFlyout CreateComposerContextFlyout(
        TextBox textBox,
        Func<ChatComposerController> getController)
    {
        var undoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Undo,
            textBox.Undo);
        var redoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Redo,
            textBox.Redo);
        var cutItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Cut,
            textBox.CutSelectionToClipboard);
        var copyItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Copy,
            textBox.CopySelectionToClipboard);
        var pasteItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Paste,
            () =>
            {
                if (GetBitmapClipboardContent() is { } clipboardContent)
                    _ = getController().PasteImageAsync(clipboardContent);
                else
                    PasteTextFromClipboard(textBox);
            });
        var selectAllItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.SelectAll,
            textBox.SelectAll);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            pasteItem,
            "ChatComposerPasteMenuItem");

        var editSeparator = new MenuFlyoutSeparator();
        var selectAllSeparator = new MenuFlyoutSeparator();
        var menu = new MenuFlyout();
        menu.Items.Add(undoItem);
        menu.Items.Add(redoItem);
        menu.Items.Add(editSeparator);
        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(selectAllSeparator);
        menu.Items.Add(selectAllItem);
        menu.Opening += (_, _) =>
        {
            var state = ChatComposerContextMenuState.Project(
                textBox.CanUndo,
                textBox.CanRedo,
                textBox.SelectionLength > 0,
                ClipboardContainsPasteContent(),
                !string.IsNullOrEmpty(textBox.Text));
            undoItem.Visibility = ToVisibility(state.ShowUndo);
            redoItem.Visibility = ToVisibility(state.ShowRedo);
            cutItem.Visibility = ToVisibility(state.ShowCut);
            copyItem.Visibility = ToVisibility(state.ShowCopy);
            pasteItem.Visibility = ToVisibility(state.ShowPaste);
            selectAllItem.Visibility = ToVisibility(state.ShowSelectAll);
            editSeparator.Visibility = ToVisibility(state.ShowEditSeparator);
            selectAllSeparator.Visibility = ToVisibility(state.ShowSelectAllSeparator);
        };
        return menu;
    }

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static MenuFlyoutItem CreateStandardMenuItem(
        Microsoft.UI.Xaml.Input.StandardUICommandKind kind,
        Action execute)
    {
        var command = new Microsoft.UI.Xaml.Input.StandardUICommand(kind);
        command.CanExecuteRequested += (_, args) => args.CanExecute = true;
        command.ExecuteRequested += (_, _) => execute();
        return new MenuFlyoutItem
        {
            Command = command,
            Visibility = Visibility.Collapsed,
        };
    }

    private static bool ClipboardContainsPasteContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && (content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    || content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return false;
        }
    }

    private static void PasteTextFromClipboard(TextBox textBox)
    {
        try
        {
            textBox.PasteFromClipboard();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard text paste failed: {ex.Message}");
        }
    }

    private static string PlaceholderFor(string connectionState) => connectionState switch
    {
        "connected" => Localized("Chat_Composer_Placeholder_Connected", "Message Assistant (Enter to send)"),
        "connecting" => Localized("Chat_Composer_Placeholder_Connecting", "Connecting…"),
        "incompatible-gateway" => Localized(
            "Chat_Composer_Placeholder_IncompatibleGateway",
            "Gateway update required: incompatible version"),
        _ => Localized("Chat_Composer_Placeholder_NotConnected", "Not connected"),
    };

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

internal static class ComposerAutomationVisibility
{
    // Intentional Reactor escape hatch. Readiness depends on post-layout measurements
    // and temporary Loaded/SizeChanged subscriptions, so it cannot be represented by
    // static modifiers alone. Prepare runs on every render, detaches stale handlers,
    // and reapplies the correct pooled-control state before subscribing when needed.
    public static void Prepare(FrameworkElement control)
    {
        Detach(control);
        if (HasUsableLayout(control))
        {
            ApplyReadyState(control);
            return;
        }

        control.IsHitTestVisible = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        control.Loaded += OnLoaded;
        control.SizeChanged += OnSizeChanged;
    }

    public static void Detach(FrameworkElement control)
    {
        control.Loaded -= OnLoaded;
        control.SizeChanged -= OnSizeChanged;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void TryEnableHitTesting(object sender)
    {
        if (sender is not FrameworkElement control || !HasUsableLayout(control))
            return;

        ApplyReadyState(control);
    }

    private static void ApplyReadyState(FrameworkElement control)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Control);
        control.IsHitTestVisible = true;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
            .FromElement(control)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(control);
        peer?.RaisePropertyChangedEvent(
            Microsoft.UI.Xaml.Automation.AutomationElementIdentifiers.IsOffscreenProperty,
            true,
            false);
        Detach(control);
    }

    private static bool HasUsableLayout(FrameworkElement control) =>
        control.IsLoaded
        && control.Visibility == Visibility.Visible
        && control.ActualWidth > 0
        && control.ActualHeight > 0;
}
