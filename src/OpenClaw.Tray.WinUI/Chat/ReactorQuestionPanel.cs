using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorQuestionPanelProps(
    ChatQuestionCoordinator Coordinator, string SessionKey, string? AgentId,
    double ViewportWidth = 800, double ViewportHeight = 620, Action<bool>? OnCollapsedChanged = null, bool? Collapsed = null);

/// <summary>The transient question dock replaces the writing surface without replacing its retained draft.</summary>
internal sealed class ReactorQuestionPanel : Component<ReactorQuestionPanelProps>
{
    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(props.Coordinator.Snapshot(), threadSafe: true);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var (localCollapsed, setCollapsed) = UseState(false);
        var collapsed = props.Collapsed ?? localCollapsed;
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => setSnapshot(props.Coordinator.Snapshot());
            props.Coordinator.Changed += changed;
            props.Coordinator.Start();
            setSnapshot(props.Coordinator.Snapshot());
            return () => props.Coordinator.Changed -= changed;
        }), props.Coordinator);
        var prompts = snapshot.Prompts.Where(prompt =>
            ChatQuestionCoordinator.Matches(prompt.Request, props.SessionKey, props.AgentId)).ToArray();
        var pending = prompts.Where(prompt => prompt.Request.Status == "pending").ToArray();
        var selected = Array.FindIndex(pending, prompt => prompt.Request.Id == selectedId);
        if (selected < 0) selected = 0;
        void Collapse(bool value)
        {
            setCollapsed(value);
            props.OnCollapsedChanged?.Invoke(value);
        }
        var children = new List<Element>();
        if (snapshot.Error is { } error)
            children.Add(TextBlock(ErrorText(error)).Set(ChatVisuals.StylePrimaryText)
                .TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        for (var index = 0; index < pending.Length; index++)
        {
            var prompt = pending[index];
            var visible = index == selected;
            children.Add(Border(Component<ReactorQuestionCard, ReactorQuestionCardProps>(
                new(props.Coordinator, prompt, props.SessionKey, props.AgentId, snapshot.Connected,
                    collapsed, Collapse, index + 1, pending.Length,
                    () => setSelectedId(pending[(selected + pending.Length - 1) % pending.Length].Request.Id),
                    () => setSelectedId(pending[(selected + 1) % pending.Length].Request.Id))))
                .WithKey(prompt.Request.Id)
                .IsVisible(visible));
        }
        foreach (var terminal in prompts.Where(prompt => prompt.Request.Status is not ("pending" or "answered")))
            children.Add(TextBlock(ResultText(terminal.Request.Status)).Set(ChatVisuals.StyleSecondaryText)
                .FontSize(12).Margin(14, 6)
                .WithKey("result-" + terminal.Request.Id)
                .AutomationId("ChatQuestionResult_" + terminal.Request.Id).LiveRegion(AutomationLiveSetting.Polite));
        if (snapshot.Error is not null || pending.Any(prompt => prompt.NeedsRefresh))
            children.Add(Button(L("Chat_Questions_Refresh", "Refresh questions"), () => _ = props.Coordinator.RefreshAsync())
                .AutomationName(L("Chat_Questions_Refresh", "Refresh questions"))
                .IsEnabled(snapshot.Connected).HAlign(HorizontalAlignment.Left));
        if (children.Count == 0)
            return Empty();
        return ScrollViewer(VStack(8, children.ToArray()))
            .MaxHeight(Math.Min(576, props.ViewportHeight * .58))
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(ChatVisuals.Gutter(props.ViewportWidth), 8, ChatVisuals.Gutter(props.ViewportWidth), 14)
            .HAlign(HorizontalAlignment.Stretch)
            .Set(scroll =>
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            })
            .AutomationId("ChatQuestionPanel");
    }

    internal static string L(string key, string fallback) => ChatMarkdownPresentation.Localized(key, fallback);
    internal static string ResultText(string status) => status switch
    {
        "answered" => L("Chat_Questions_Answered", "Question answered"),
        "cancelled" => L("Chat_Questions_Cancelled", "Question cancelled"),
        "unavailable" => L("Chat_Questions_NoLongerAvailable", "Question no longer available"),
        _ => L("Chat_Questions_Expired", "Question expired"),
    };
    internal static string ErrorText(string error) => error switch
    {
        "Questions could not be refreshed. Retry or open web chat." => L("Chat_Questions_RefreshFailed", error),
        "This question is expired or disconnected. Refresh before answering." => L("Chat_Questions_Unavailable", error),
        "The answer was not confirmed. Refresh to check its status before retrying." => L("Chat_Questions_Unconfirmed", error),
        "This question is being resolved. Refresh to check its status." => L("Chat_Questions_Resolving", error),
        "Choose a valid answer for each question." => L("Chat_Questions_InvalidAnswer", error),
        "An agent question could not be read. Refresh or open web chat to respond." => L("Chat_Questions_Invalid", error),
        _ => error,
    };
}

internal sealed record ReactorQuestionCardProps(
    ChatQuestionCoordinator Coordinator, ChatQuestionPrompt Prompt, string SessionKey, string? AgentId, bool Connected,
    bool Collapsed, Action<bool> OnCollapsed, int RequestIndex, int RequestCount, Action PreviousRequest, Action NextRequest);

internal sealed class ReactorQuestionCard : Component<ReactorQuestionCardProps>
{
    public override Element Render()
    {
        var props = Props;
        var request = props.Prompt.Request;
        var drafts = UseRef(new Dictionary<string, ChatQuestionDraft>(StringComparer.Ordinal));
        var (revision, setRevision) = UseState(0, threadSafe: true);
        var (currentIndex, setCurrentIndex) = UseState(0);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var mounted = UseRef(true);
        var freeInput = UseRef<Control?>(null);
        var optionControls = UseRef(new Dictionary<string, Control>(StringComparer.Ordinal));
        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => { mounted.Current = false; drafts.Current.Clear(); optionControls.Current.Clear(); };
        }), Array.Empty<object>());
        foreach (var item in request.Questions)
        {
            if (drafts.Current.ContainsKey(item.Id)) continue;
            var initial = new ChatQuestionDraft();
            foreach (var answer in item.DefaultAnswers)
            {
                if (item.Options.Any(option => option.Value == answer))
                    initial.Selected.Add(answer);
                else
                    initial.FreeText = string.Join("\n", new[] { initial.FreeText, answer }.Where(text => text.Length > 0));
            }
            drafts.Current.Add(item.Id, initial);
        }
        var question = request.Questions[Math.Min(currentIndex, request.Questions.Count - 1)];
        var draft = drafts.Current[question.Id];
        var enabled = props.Connected && !props.Prompt.Submitting && !props.Prompt.NeedsRefresh;
        // Rendering is queued, so actions must read drafts again at invocation.
        Dictionary<string, string[]> ReadAnswers() => request.Questions.ToDictionary(item => item.Id,
            item => drafts.Current[item.Id].Answers(item), StringComparer.Ordinal);
        bool CanSubmit()
        {
            try { ChatQuestionDraft.Validate(request, ReadAnswers()); return true; }
            catch (ArgumentException) { return false; }
        }
        bool HasCurrentAnswer() =>
            !question.RequiresWebControl && (question.AllowEmpty || draft.Answers(question).Length > 0);
        var valid = CanSubmit();
        var last = currentIndex == request.Questions.Count - 1;
        var canAdvance = HasCurrentAnswer();

        async Task Submit(bool cancel)
        {
            setError(null);
            try
            {
                await props.Coordinator.SubmitAsync(props.SessionKey, props.AgentId, request.Id, cancel ? null : ReadAnswers());
            }
            catch (Exception)
            {
                if (mounted.Current)
                    setError(L("Chat_Questions_SendFailed", "The answer could not be sent. Refresh and try again."));
            }
            finally
            {
                foreach (var item in request.Questions.Where(item => item.IsSecret))
                    if (drafts.Current.TryGetValue(item.Id, out var secretDraft))
                        secretDraft.FreeText = "";
                if (mounted.Current) setRevision(revision + 1);
            }
        }
        void Advance()
        {
            if (!enabled || !HasCurrentAnswer()) return;
            if (!last) setCurrentIndex(currentIndex + 1);
            else if (CanSubmit()) _ = Submit(false);
        }
        void Select(int index, bool selected, bool advance = true)
        {
            if (!enabled) return;
            var option = question.Options[index];
            if (!question.MultiSelect)
            {
                draft.Selected.Clear();
                draft.FreeText = "";
            }
            if (selected) draft.Selected.Add(option.Value);
            else draft.Selected.Remove(option.Value);
            setRevision(revision + 1);
            if (advance && selected && !question.MultiSelect && !last)
                setCurrentIndex(currentIndex + 1);
        }
        void Edit(string text)
        {
            draft.FreeText = text;
            if (!question.MultiSelect && !string.IsNullOrWhiteSpace(text))
                draft.Selected.Clear();
            setRevision(revision + 1);
        }
        var header = Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto], [GridSize.Auto],
            TextBlock(props.Collapsed ? question.Text : L("Chat_Questions_Waiting", "Waiting for your answer"))
                .Set(ChatVisuals.StylePrimaryText).FontSize(12).FontWeight(FontWeights.SemiBold)
                .VAlign(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis)
                .LiveRegion(AutomationLiveSetting.Polite),
            props.RequestCount > 1
                ? HStack(2,
                    QuietButton("\u2039", L("Chat_Questions_PreviousRequest", "Previous request"), props.PreviousRequest),
                    TextBlock($"{props.RequestIndex}/{props.RequestCount}").Set(ChatVisuals.StyleSecondaryText).FontSize(12),
                    QuietButton("\u203a", L("Chat_Questions_NextRequest", "Next request"), props.NextRequest)).Grid(column: 1)
                : Empty(),
            TextBlock($"{currentIndex + 1}/{request.Questions.Count}")
                .FontSize(12).Set(ChatVisuals.StyleSecondaryText).Margin(8, 0)
                .VAlign(VerticalAlignment.Center).Grid(column: 2).AutomationId("ChatQuestionProgress_" + request.Id),
            QuietButton(props.Collapsed ? "\uE70D" : "\uE70E",
                props.Collapsed ? L("Chat_Questions_Expand", "Expand question") : L("Chat_Questions_Collapse", "Collapse question"),
                () => props.OnCollapsed(!props.Collapsed))
                .FontFamily(Helpers.FluentIconCatalog.SymbolThemeFontFamily).Size(28, 28).Grid(column: 3)
                .AutomationId("ChatQuestionCollapse_" + request.Id));
        var content = new List<Element>
        {
            TextBlock(question.Text).FontSize(14).FontWeight(FontWeights.SemiBold).Set(ChatVisuals.StylePrimaryText)
                .TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true).AutomationId("ChatQuestionPrompt_" + request.Id),
        };
        if (question.RequiresWebControl)
            content.Add(TextBlock(L("Chat_Questions_WebRequired",
                "Open web chat to answer this question. It requires resource selection or secret-store controls."))
                .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap));
        else
        {
            var options = new List<Element>();
            for (var index = 0; index < question.Options.Count; index++)
            {
                var option = question.Options[index];
                var capturedIndex = index;
                var label = string.IsNullOrWhiteSpace(option.Description) ? option.Label : $"{option.Label}. {option.Description}";
                var id = "ChatQuestionOption_" + question.Id + "_" + index;
                options.Add(question.MultiSelect
                    ? CheckBox(draft.Selected.Contains(option.Value), value => Select(capturedIndex, value), option.Label)
                        .Set(control => SetOptionContent(control, option, capturedIndex, false))
                        .IsEnabled(enabled).AutomationName(label).AutomationId(id)
                    : RadioButton(option.Label, draft.Selected.Contains(option.Value),
                        value =>
                        {
                            if (value) Select(capturedIndex, true,
                                !KeyDown(VirtualKey.Left) && !KeyDown(VirtualKey.Right)
                                && !KeyDown(VirtualKey.Up) && !KeyDown(VirtualKey.Down));
                        }, request.Id + "_" + question.Id)
                        .Set(control =>
                        {
                            SetOptionContent(control, option, capturedIndex, true);
                            optionControls.Current[id] = control;
                        })
                        .IsEnabled(enabled).AutomationName(label).AutomationId(id));
            }
            if (options.Count > 0) content.Add(VStack(7, options.ToArray()));
            if (question.AllowOther || question.Options.Count == 0)
            {
                var label = L("Chat_Questions_Answer", "Your answer");
                var placeholder = question.Options.Count > 0
                    ? L("Chat_Questions_OtherPlaceholder", "Type your own answer here") : label;
                Element input = question.IsSecret
                    ? PasswordBox(draft.FreeText, Edit, placeholder).IsEnabled(enabled)
                        .OnMount(control => freeInput.Current = control as Control).OnUnmount(_ => freeInput.Current = null)
                        .AutomationName(label).AutomationId("ChatQuestionInput_" + question.Id)
                    : TextBox(draft.FreeText, Edit, placeholder).AcceptsReturn(true)
                        .MaxLength(4096).MaxHeight(160).TextWrapping(TextWrapping.Wrap).IsEnabled(enabled)
                        .Set(control => ChatVisuals.StyleQuestionInput(control, question.Options.Count > 0))
                        .OnMount(control => freeInput.Current = control as Control).OnUnmount(_ => freeInput.Current = null)
                        .AutomationName(label).AutomationId("ChatQuestionInput_" + question.Id);
                content.Add(question.Options.Count == 0 ? input :
                    Border(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        Border(TextBlock(draft.FreeText.Length > 0 ? "\u2713" : "").FontSize(11)
                                .Set(ChatVisuals.StylePrimaryText).Center())
                            .Size(16, 16).CornerRadius(4).BorderThickness(1).Set(ChatVisuals.StyleQuestionOption)
                            .Margin(0, 0, 9, 0).VAlign(VerticalAlignment.Center),
                        input.Grid(column: 1),
                        TextBlock(question.Options.Count < 9 ? $"{question.Options.Count + 1}" : "")
                            .FontSize(12).Set(ChatVisuals.StyleSecondaryText).Margin(9, 0, 0, 0)
                            .VAlign(VerticalAlignment.Center).Grid(column: 2)))
                        .Padding(11, 6).CornerRadius(11).BorderThickness(1).Set(ChatVisuals.StyleQuestionOption));
                if (!question.IsSecret)
                    content.Add(TextBlock(L("Chat_Questions_KeyboardHint", "Enter adds a line / Ctrl+Enter to continue"))
                        .FontSize(11).Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap));
            }
        }
        if (question.HasUnavailableLink)
            content.Add(TextBlock(L("Chat_Questions_LinkFailed", "The question link could not be opened."))
                .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap));
        if (question.Url is { } url)
            content.Add(Button(L("Chat_Questions_OpenLink", "Open question link"), async () =>
            {
                try
                {
                    if (!await Launcher.LaunchUriAsync(new Uri(url)))
                        setError(L("Chat_Questions_LinkFailed", "The question link could not be opened."));
                }
                catch (Exception)
                {
                    if (mounted.Current) setError(L("Chat_Questions_LinkFailed", "The question link could not be opened."));
                }
            }).AutomationName(L("Chat_Questions_OpenLink", "Open question link")).HAlign(HorizontalAlignment.Left));
        if (!props.Connected || props.Prompt.NeedsRefresh)
            content.Add(TextBlock(L("Chat_Questions_Reconnect", "Reconnect and refresh questions before answering."))
                .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        if ((props.Prompt.Error ?? error) is { } message)
            content.Add(TextBlock(ReactorQuestionPanel.ErrorText(message)).Set(ChatVisuals.StylePrimaryText)
                .TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        content.Add(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            currentIndex > 0
                ? QuietButton(L("Chat_Questions_Back", "Back"), L("Chat_Questions_Back", "Back"),
                    () => setCurrentIndex(currentIndex - 1)).IsEnabled(enabled).HAlign(HorizontalAlignment.Left)
                    .AutomationId("ChatQuestionBack_" + request.Id)
                : Empty(),
            HStack(8,
                QuietButton(L("Chat_Questions_Skip", "Skip"), L("Chat_Questions_Cancel", "Cancel question"), () => _ = Submit(true))
                    .IsEnabled(enabled).AutomationId("ChatQuestionCancel_" + request.Id),
                Button(props.Prompt.Submitting ? L("Chat_Questions_Submitting", "Submitting...")
                        : last ? L("Chat_Questions_SubmitShort", "Submit") : L("Chat_Questions_Next", "Next"), Advance)
                    .Set(ChatVisuals.StyleQuestionSubmit).FontSize(12).Padding(12, 6).MinHeight(30)
                    .AutomationName(last ? L("Chat_Questions_Submit", "Submit answer") : L("Chat_Questions_Next", "Next"))
                    .IsEnabled(enabled && (last ? valid : canAdvance)).AutomationId("ChatQuestionSubmit_" + request.Id))
                .Grid(column: 1)));
        return Border(VStack(12, header,
                VStack(12, content.ToArray()).IsVisible(!props.Collapsed)))
            .Padding(14).CornerRadius(20).BorderThickness(1).Set(ChatVisuals.StyleQuestionCard)
            .AutomationId("ChatQuestion_" + request.Id)
            .OnPreviewKeyDown((_, args) =>
            {
                if (!enabled || props.Collapsed) return;
                if (KeyDown(VirtualKey.Menu) || KeyDown(VirtualKey.Shift)) return;
                var editing = FindAncestor<TextBox>(args.OriginalSource as DependencyObject) is not null
                    || FindAncestor<PasswordBox>(args.OriginalSource as DependencyObject) is not null;
                if (!KeyDown(VirtualKey.Control)
                    && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
                    && FindAncestor<Microsoft.UI.Xaml.Controls.RadioButton>(args.OriginalSource as DependencyObject) is { } radio)
                {
                    var prefix = "ChatQuestionOption_" + question.Id + "_";
                    var id = AutomationProperties.GetAutomationId(radio);
                    if (id.StartsWith(prefix, StringComparison.Ordinal)
                        && int.TryParse(id.AsSpan(prefix.Length), out var index)
                        && index >= 0 && index < question.Options.Count)
                    {
                        args.Handled = true;
                        var direction = args.Key is VirtualKey.Up or VirtualKey.Left ? -1 : 1;
                        var next = (index + direction + question.Options.Count) % question.Options.Count;
                        Select(next, true, advance: false);
                        optionControls.Current[prefix + next].Focus(FocusState.Keyboard);
                    }
                }
                else if (args.Key == VirtualKey.Enter && KeyDown(VirtualKey.Control)
                    && (editing || FindAncestor<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(
                        args.OriginalSource as DependencyObject) is not null))
                {
                    args.Handled = true;
                    Advance();
                }
                else if (!editing && !KeyDown(VirtualKey.Control) && args.Key >= VirtualKey.Number1 && args.Key <= VirtualKey.Number9)
                {
                    var index = (int)args.Key - (int)VirtualKey.Number1;
                    if (index < question.Options.Count)
                    {
                        args.Handled = true;
                        Select(index, !question.MultiSelect || !draft.Selected.Contains(question.Options[index].Value));
                    }
                    else if (index == question.Options.Count && question.AllowOther)
                    {
                        args.Handled = true;
                        freeInput.Current?.Focus(FocusState.Keyboard);
                    }
                }
            });
    }

    private static bool KeyDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is T result) return result;
        return null;
    }

    private static ButtonElement QuietButton(string text, string name, Action action) =>
        Button(text, action).Set(ChatVisuals.StyleQuestionQuietButton).FontSize(12).Padding(8, 6).MinHeight(30).AutomationName(name);

    private static void SetOptionContent(ContentControl control, GatewayQuestionOption option, int index, bool radio)
    {
        control.Style = (Style)Application.Current.Resources["ChatQuestionOptionStyle"];
        control.ClearValue(Control.BackgroundProperty);
        control.ClearValue(Control.BorderBrushProperty);
        control.ClearValue(Control.ForegroundProperty);
        control.CornerRadius = new CornerRadius(radio ? 8 : 4);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (control.Content is not Microsoft.UI.Xaml.Controls.Grid grid)
        {
            grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 9 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel { Spacing = 2 };
            var title = new Microsoft.UI.Xaml.Controls.TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var description = new Microsoft.UI.Xaml.Controls.TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            ChatVisuals.StylePrimaryText(title);
            ChatVisuals.StyleSecondaryText(description);
            copy.Children.Add(title);
            copy.Children.Add(description);
            grid.Children.Add(copy);
            var shortcut = new Microsoft.UI.Xaml.Controls.TextBlock { FontSize = 12, MinWidth = 18, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            ChatVisuals.StyleSecondaryText(shortcut);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(shortcut, 1);
            AutomationProperties.SetAccessibilityView(shortcut, AccessibilityView.Raw);
            grid.Children.Add(shortcut);
            control.Content = grid;
        }
        var stack = (StackPanel)grid.Children[0];
        ((Microsoft.UI.Xaml.Controls.TextBlock)stack.Children[0]).Text = option.Label;
        var detail = (Microsoft.UI.Xaml.Controls.TextBlock)stack.Children[1];
        detail.Text = option.Description ?? "";
        detail.Visibility = string.IsNullOrWhiteSpace(option.Description) ? Visibility.Collapsed : Visibility.Visible;
        ((Microsoft.UI.Xaml.Controls.TextBlock)grid.Children[1]).Text = index < 9 ? $"{index + 1}" : "";
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
