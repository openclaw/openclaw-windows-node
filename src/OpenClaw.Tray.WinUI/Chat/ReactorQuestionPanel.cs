using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorQuestionPanelProps(ChatQuestionCoordinator Coordinator, string SessionKey, string? AgentId);

/// <summary>Pinned above the composer so an awaiting-input request cannot disappear below the transcript.</summary>
internal sealed class ReactorQuestionPanel : Component<ReactorQuestionPanelProps>
{
    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(props.Coordinator.Snapshot(), threadSafe: true);
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
        var children = new List<Element>();
        if (snapshot.Error is { } error)
            children.Add(TextBlock(ErrorText(error)).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        foreach (var prompt in prompts)
        {
            children.Add(Component<ReactorQuestionCard, ReactorQuestionCardProps>(
                new(props.Coordinator, prompt, props.SessionKey, props.AgentId, snapshot.Connected))
                .WithKey(prompt.Request.Id));
        }
        if (children.Count == 0)
            return Empty();
        children.Add(Button(L("Chat_Questions_Refresh", "Refresh questions"), () => _ = props.Coordinator.RefreshAsync())
            .AutomationName(L("Chat_Questions_Refresh", "Refresh questions"))
            .IsEnabled(snapshot.Connected).HAlign(HorizontalAlignment.Left));
        return ScrollViewer(VStack(8, children.ToArray()).Margin(12))
            .MaxHeight(360)
            .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
            .AutomationId("ChatQuestionPanel");
    }

    internal static string L(string key, string fallback) => ChatMarkdownPresentation.Localized(key, fallback);

    internal static string ErrorText(string error) => error switch
    {
        "Questions could not be refreshed. Retry or open web chat." => L("Chat_Questions_RefreshFailed", error),
        "This question is expired or disconnected. Refresh before answering." => L("Chat_Questions_Unavailable", error),
        "The answer was not confirmed. Refresh to check its status before retrying." => L("Chat_Questions_Unconfirmed", error),
        "An agent question could not be read. Refresh or open web chat to respond." => L("Chat_Questions_Invalid", error),
        _ => error,
    };
}

internal sealed record ReactorQuestionCardProps(
    ChatQuestionCoordinator Coordinator, ChatQuestionPrompt Prompt, string SessionKey, string? AgentId, bool Connected);

internal sealed class ReactorQuestionCard : Component<ReactorQuestionCardProps>
{
    public override Element Render()
    {
        var props = Props;
        var request = props.Prompt.Request;
        var drafts = UseRef(new Dictionary<string, ChatQuestionDraft>(StringComparer.Ordinal));
        var (revision, setRevision) = UseState(0, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var mounted = UseRef(true);
        var pending = request.Status == "pending";
        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => { mounted.Current = false; drafts.Current.Clear(); };
        }), Array.Empty<object>());
        if (!pending)
        {
            drafts.Current.Clear();
            var label = request.Status switch
            {
                "answered" => L("Chat_Questions_Answered", "Question answered"),
                "cancelled" => L("Chat_Questions_Cancelled", "Question cancelled"),
                "unavailable" => L("Chat_Questions_NoLongerAvailable", "Question no longer available"),
                _ => L("Chat_Questions_Expired", "Question expired"),
            };
            return TextBlock(label).Set(ChatVisuals.StyleSecondaryText)
                .AutomationId("ChatQuestionResult_" + request.Id).LiveRegion(AutomationLiveSetting.Polite);
        }

        var enabled = props.Connected && !props.Prompt.Submitting && !props.Prompt.NeedsRefresh;
        var children = new List<Element>
        {
            TextBlock(L("Chat_Questions_Waiting", "Waiting for your answer"))
                .FontWeight(FontWeights.SemiBold).LiveRegion(AutomationLiveSetting.Polite),
        };
        foreach (var question in request.Questions)
        {
            if (!drafts.Current.TryGetValue(question.Id, out var draft))
            {
                draft = new ChatQuestionDraft();
                foreach (var answer in question.DefaultAnswers)
                {
                    if (question.Options.Any(option => option.Value == answer))
                        draft.Selected.Add(answer);
                    else
                        draft.FreeText = string.Join("\n", new[] { draft.FreeText, answer }.Where(text => text.Length > 0));
                }
                drafts.Current.Add(question.Id, draft);
            }
            children.Add(TextBlock(question.Text).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true));
            if (question.RequiresWebControl)
            {
                children.Add(TextBlock(L("Chat_Questions_WebRequired",
                    "Open web chat to answer this question. It requires resource selection or secret-store controls."))
                    .TextWrapping(TextWrapping.Wrap));
                continue;
            }
            for (var optionIndex = 0; optionIndex < question.Options.Count; optionIndex++)
            {
                var option = question.Options[optionIndex];
                var label = string.IsNullOrWhiteSpace(option.Description)
                    ? option.Label : $"{option.Label}\n{option.Description}";
                void Select(bool selected)
                {
                    if (!question.MultiSelect)
                    {
                        draft.Selected.Clear();
                        draft.FreeText = "";
                    }
                    if (selected)
                        draft.Selected.Add(option.Value);
                    else
                        draft.Selected.Remove(option.Value);
                    setRevision(revision + 1);
                }
                var optionId = "ChatQuestionOption_" + question.Id + "_" + optionIndex;
                children.Add(question.MultiSelect
                    ? CheckBox(draft.Selected.Contains(option.Value), Select, label)
                        .Set(control => SetOptionContent(control, label))
                        .IsEnabled(enabled).AutomationName(label).AutomationId(optionId)
                    : RadioButton(label, draft.Selected.Contains(option.Value),
                        selected => { if (selected) Select(true); }, request.Id + "_" + question.Id)
                        .Set(control => SetOptionContent(control, label))
                        .IsEnabled(enabled).AutomationName(label).AutomationId(optionId));
            }
            if (question.AllowOther || question.Options.Count == 0)
            {
                void Edit(string text)
                {
                    draft.FreeText = text;
                    if (!question.MultiSelect && text.Length > 0)
                        draft.Selected.Clear();
                    setRevision(revision + 1);
                }
                var label = L("Chat_Questions_Answer", "Your answer");
                children.Add(question.IsSecret
                    ? PasswordBox(draft.FreeText, Edit, label).IsEnabled(enabled)
                        .AutomationName(label).AutomationId("ChatQuestionInput_" + question.Id)
                    : TextBox(draft.FreeText, Edit, label).AcceptsReturn(question.AnswerAsLines)
                        .MaxLength(4096).TextWrapping(TextWrapping.Wrap).IsEnabled(enabled)
                        .AutomationName(label).AutomationId("ChatQuestionInput_" + question.Id));
            }
            if (question.HasUnavailableLink)
                children.Add(TextBlock(L("Chat_Questions_LinkFailed", "The question link could not be opened."))
                    .TextWrapping(TextWrapping.Wrap));
            if (question.Url is { } url)
            {
                children.Add(Button(L("Chat_Questions_OpenLink", "Open question link"), async () =>
                {
                    try
                    {
                        if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
                            setError(L("Chat_Questions_LinkFailed", "The question link could not be opened."));
                    }
                    catch (Exception)
                    {
                        setError(L("Chat_Questions_LinkFailed", "The question link could not be opened."));
                    }
                }).AutomationName(L("Chat_Questions_OpenLink", "Open question link")).HAlign(HorizontalAlignment.Left));
            }
        }
        var answers = request.Questions.ToDictionary(question => question.Id,
            question => drafts.Current[question.Id].Answers(question), StringComparer.Ordinal);
        var valid = true;
        try { ChatQuestionDraft.Validate(request, answers); }
        catch (ArgumentException) { valid = false; }

        async Task Submit(bool cancel)
        {
            setError(null);
            try
            {
                await props.Coordinator.SubmitAsync(props.SessionKey, props.AgentId, request.Id, cancel ? null : answers);
            }
            catch (Exception)
            {
                setError(L("Chat_Questions_SendFailed", "The answer could not be sent. Refresh and try again."));
            }
            finally
            {
                foreach (var question in request.Questions.Where(question => question.IsSecret))
                    if (drafts.Current.TryGetValue(question.Id, out var draft))
                        draft.FreeText = "";
                if (mounted.Current)
                    setRevision(revision + 1);
            }
        }

        if (!props.Connected || props.Prompt.NeedsRefresh)
            children.Add(TextBlock(L("Chat_Questions_Reconnect", "Reconnect and refresh questions before answering."))
                .TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        if ((props.Prompt.Error ?? error) is { } message)
            children.Add(TextBlock(ReactorQuestionPanel.ErrorText(message)).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        children.Add(VStack(8,
            Button(props.Prompt.Submitting ? L("Chat_Questions_Submitting", "Submitting...")
                    : L("Chat_Questions_Submit", "Submit answer"), () => _ = Submit(false))
                .AutomationName(L("Chat_Questions_Submit", "Submit answer"))
                .IsEnabled(enabled && valid).AutomationId("ChatQuestionSubmit_" + request.Id),
            Button(L("Chat_Questions_Cancel", "Cancel question"), () => _ = Submit(true))
                .AutomationName(L("Chat_Questions_Cancel", "Cancel question"))
                .IsEnabled(enabled).AutomationId("ChatQuestionCancel_" + request.Id)));
        return Border(VStack(8, children.ToArray())).Padding(12).CornerRadius(8)
            .Set(ChatVisuals.StyleQuestionCard).BorderThickness(1)
            .AutomationId("ChatQuestion_" + request.Id);
    }

    private static void SetOptionContent(ContentControl control, string label)
    {
        if (control.Content is not Microsoft.UI.Xaml.Controls.TextBlock text)
        {
            text = new Microsoft.UI.Xaml.Controls.TextBlock { TextWrapping = TextWrapping.Wrap };
            control.Content = text;
        }
        text.Text = label;
        control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
