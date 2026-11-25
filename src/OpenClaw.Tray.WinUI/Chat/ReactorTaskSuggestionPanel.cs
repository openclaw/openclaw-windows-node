// Native plain-text suggested-task cards. The dock never selects a session or edits the composer draft.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorTaskSuggestionPanelProps(ChatTaskSuggestionCoordinator Coordinator,
    string SessionKey, string? AgentId, double ViewportWidth, double ViewportHeight);

/// <summary>Connection-owned proposals with explicit launch choices and selectable instructions.</summary>
internal sealed class ReactorTaskSuggestionPanel : Component<ReactorTaskSuggestionPanelProps>
{
    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(props.Coordinator.Snapshot(), threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            props.Coordinator.ObserveSession(props.SessionKey);
            EventHandler changed = (_, _) => setSnapshot(props.Coordinator.Snapshot());
            props.Coordinator.Changed += changed;
            setSnapshot(props.Coordinator.Snapshot());
            return () => props.Coordinator.Changed -= changed;
        }), props.Coordinator);
        var cards = snapshot.Cards.Where(card => card.Suggestion.SessionKey == props.SessionKey
            && (card.Suggestion.AgentId is null || props.AgentId is null || card.Suggestion.AgentId == props.AgentId)).ToArray();
        var identityUnavailable = snapshot.IdentityUnavailable && snapshot.IdentityNoticeSession == props.SessionKey;
        if (cards.Length == 0 && !identityUnavailable) return Empty();
        var children = new List<Element>
        {
            TextBlock(L("Title", "Suggested tasks")).Set(ChatVisuals.StyleFlyoutHeading)
        };
        if (identityUnavailable)
            children.Add(TextBlock(L("IdentityUnavailable", "Suggested tasks need a verified Gateway operator identity. Check your Gateway access, then retry."))
                .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        if (snapshot.RefreshFailed && cards.Length > 0)
            children.Add(TextBlock(L("RefreshFailed", "Suggested tasks could not be refreshed."))
                .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        children.Add(Button(L("Refresh", "Refresh tasks"), () => _ = props.Coordinator.RefreshAsync())
            .IsEnabled(snapshot.Connected).Set(ReactorTaskSuggestionCard.KeepFocusInside).HAlign(HorizontalAlignment.Left));
        children.AddRange(cards.Select(card => Component<ReactorTaskSuggestionCard, ReactorTaskSuggestionCardProps>(
            new(props.Coordinator, card, snapshot.Connected && !card.Busy
                    && (card.Attempt is null || (card.CanRetry && card.AttemptEpoch == snapshot.ConnectionEpoch)),
                snapshot.CanAccept, snapshot.CanDismiss, snapshot.AdministratorRequired, snapshot.CanHideLocally)).WithKey(card.Suggestion.Id)));
        return ScrollViewer(VStack(8, children.ToArray()))
            .MaxHeight(Math.Min(320, props.ViewportHeight * .42))
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(ChatVisuals.Gutter(props.ViewportWidth), 8, ChatVisuals.Gutter(props.ViewportWidth), 12)
            .Set(scroll =>
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }).AutomationId("ChatTaskSuggestions");
    }

    internal static string L(string key, string fallback) =>
        ChatMarkdownPresentation.Localized("Chat_Tasks_" + key, fallback);
}

internal sealed record ReactorTaskSuggestionCardProps(ChatTaskSuggestionCoordinator Coordinator, ChatTaskCard Card,
    bool Enabled, bool CanAccept, bool CanDismiss, bool AdministratorRequired, bool CanHideLocally);

internal sealed class ReactorTaskSuggestionCard : Component<ReactorTaskSuggestionCardProps>
{
    public override Element Render()
    {
        var props = Props;
        var card = props.Card;
        var task = card.Suggestion;
        var (instructions, setInstructions) = UseState(false);
        var (options, setOptions) = UseState(false);
        var (cwd, setCwd) = UseState("");
        var (profile, setProfile) = UseState("");
        var (copied, setCopied) = UseState(false);
        var (copyFailed, setCopyFailed) = UseState(false);
        var children = new List<Element>
        {
            TextBlock(task.Title).Set(ChatVisuals.StyleFlyoutHeading).TextWrapping(TextWrapping.Wrap),
            TextBlock(task.Tldr).Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true),
            TextBlock(task.Cwd).Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true),
            ToggleButton(TextBlock(instructions ? L("HideInstructions", "Hide instructions") : L("Instructions", "Show instructions")),
                instructions, setInstructions).AutomationId("ChatTaskInstructions")
                .AutomationName(instructions ? L("HideInstructions", "Hide instructions") : L("Instructions", "Show instructions"))
                .Set(KeepFocusInside).HAlign(HorizontalAlignment.Left),
            TextBlock(task.Prompt).Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap)
                .IsTextSelectionEnabled(true).IsVisible(instructions),
            Button(copied ? L("Copied", "Instructions copied") : L("Copy", "Copy instructions"), () =>
            {
                try
                {
                    var data = new DataPackage();
                    data.SetText(task.Prompt);
                    Clipboard.SetContent(data);
                    setCopied(true);
                    setCopyFailed(false);
                }
                catch { setCopyFailed(true); }
            }).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left),
            TextBlock(L("CopyFailed", "Instructions could not be copied."))
                .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).IsVisible(copyFailed)
                .LiveRegion(AutomationLiveSetting.Polite)
        };
        if (card.StartedKey is not null)
            children.Add(TextBlock(L("Started", "Task started. Find it in Sessions."))
                .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
        else if (card.Attempt is not null)
        {
            children.Add(TextBlock(card.Busy ? L("Starting", "Starting task...")
                    : L("Unconfirmed", "The task start was not confirmed. Retrying checks the same task, not a new task. After reconnecting, check Sessions or web chat."))
                .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
            if (!card.Busy)
            {
                children.Add(Button(L("Retry", "Retry same task"), () => _ = props.Coordinator.AcceptAsync(card.Attempt))
                    .IsEnabled(props.Enabled && props.CanAccept).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left));
                children.Add(TextBlock(L("HideHelp", "Hide this notice locally. The task outcome remains unknown."))
                    .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap));
                children.Add(Button(L("Hide", "Hide locally"), () => props.Coordinator.HideLocally(task.Id))
                    .IsEnabled(props.CanHideLocally).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left));
            }
        }
        else
        {
            if (card.RepositoryRequired)
                children.Add(TextBlock(L("RepositoryRequired", "Enter an absolute repository path on the Gateway with a commit. The task has not started."))
                    .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
            if (card.CloudProfileRequired)
                children.Add(TextBlock(L("CloudProfileRequired", "Choose a configured cloud profile. The task has not started."))
                    .Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
            void Launch(string mode) => _ = props.Coordinator.AcceptAsync(new(task.Id, mode,
                mode == "cloud" ? profile.Trim() : null, mode == "worktree" && card.RepositoryRequired ? cwd.Trim() : null));
            children.Add(Button(L("Local", "Start in a new session"), () => Launch("local"))
                .IsEnabled(props.Enabled && props.CanAccept).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left));
            children.Add(Button(L("Options", "Launch options"), () => setOptions(!options))
                .IsEnabled(props.Enabled && props.CanAccept).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left));
            children.Add(VStack(8,
                Button(L("Session", "Start in this conversation"), () => Launch("session")).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left),
                TextBox(cwd, setCwd).Header(L("Repository", "Repository path on Gateway"))
                    .AutomationName(L("Repository", "Repository path on Gateway")).MaxLength(4096).IsVisible(card.RepositoryRequired),
                Button(L("Worktree", "Start in new worktree"), () => Launch("worktree"))
                    .IsEnabled(!card.RepositoryRequired || !string.IsNullOrWhiteSpace(cwd)).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left),
                TextBox(profile, setProfile).Header(L("CloudProfile", "Configured cloud profile ID"))
                    .AutomationName(L("CloudProfile", "Configured cloud profile ID")).MaxLength(128),
                Button(L("Cloud", "Start in cloud"), () => Launch("cloud"))
                    .IsEnabled(!string.IsNullOrWhiteSpace(profile)).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left))
                .IsEnabled(props.Enabled && props.CanAccept).IsVisible(options));
            if (card.DismissFailed)
                children.Add(TextBlock(L("DismissFailed", "The task could not be dismissed. Try again."))
                    .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite));
            children.Add(Button(L("Dismiss", "Dismiss task"), () => _ = props.Coordinator.DismissAsync(task.Id))
                .IsEnabled(props.Enabled && props.CanDismiss).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left));
            if (!props.CanAccept)
                children.Add(TextBlock(props.AdministratorRequired
                        ? L("AdministratorRequired", "Administrator access to the Gateway is required to start suggested tasks.")
                        : L("Compatibility", "Update the Gateway or use web chat to launch this task."))
                    .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap));
        }
        return Border(VStack(8, children.ToArray())).Padding(12).CornerRadius(12).BorderThickness(1)
            .Set(ChatVisuals.StyleQuestionCard).AutomationId("ChatTaskSuggestion_" + task.Id);
    }

    // A scroll viewport reveals the control bounds, not WinUI's default outside (-3) focus adorner.
    // Keep the native system focus visual inside the same revealed rectangle, including at the edges.
    internal static void KeepFocusInside(Microsoft.UI.Xaml.Controls.Control control) => control.FocusVisualMargin = new Thickness(0);

    private static string L(string key, string fallback) => ReactorTaskSuggestionPanel.L(key, fallback);
}
