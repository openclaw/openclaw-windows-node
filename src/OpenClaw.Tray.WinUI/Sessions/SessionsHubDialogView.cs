using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>
/// Renders the controller's pending confirmation or input dialog as a controlled
/// ContentDialog. Destructive confirmations default to Cancel so Enter never deletes.
/// </summary>
internal static class SessionsHubDialogView
{
    private static string T(string key) => SessionsHubPresentation.T(key);

    public static Element Render(SessionsHubViewContext context)
    {
        var dialog = context.State.Dialog;
        if (dialog is null)
            return Empty();
        var controller = context.Controller;
        Element body = dialog.Kind switch
        {
            SessionsHubDialogKind.Prompt => VStack(8,
                TextBlock(dialog.Body).TextWrapping(TextWrapping.Wrap),
                string.IsNullOrWhiteSpace(dialog.InputLabel)
                    ? Empty()
                    : TextBlock(dialog.InputLabel).FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                TextBox(dialog.Text, text => controller.UpdateDialog(d => d with { Text = text }), dialog.Placeholder)
                    .MaxLength(120)
                    .AutomationName(dialog.Title)
                    .AutomationId("SessionsHubPromptInput")),
            SessionsHubDialogKind.NewWorktree => NewWorktree(context, dialog),
            _ => TextBlock(dialog.Body).TextWrapping(TextWrapping.Wrap).AutomationId("SessionsHubDialogBody"),
        };
        var destructive = dialog.Destructive;
        var tone = destructive ? "red" : "blue";
        var content = VStack(14,
            HStack(12,
                SessionsHubView.IconTile(dialog.Icon, tone, 36),
                TextBlock(dialog.Title).FontSize(20).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .TextWrapping(TextWrapping.Wrap).VAlign(VerticalAlignment.Center))
                .AutomationId("SessionsHubDialogHeader"),
            body,
            Facts(dialog),
            Items(dialog),
            string.IsNullOrWhiteSpace(dialog.NoteTitle) && string.IsNullOrWhiteSpace(dialog.NoteMessage)
                ? Empty()
                : (InfoBar(dialog.NoteTitle, dialog.NoteMessage) with { IsOpen = true })
                    .Severity(destructive ? InfoBarSeverity.Error : InfoBarSeverity.Warning)
                    .IsClosable(false)
                    .AutomationId("SessionsHubDialogNote"));
        return (ContentDialog("", content.MinWidth(380).MaxWidth(520), dialog.ConfirmLabel) with
            {
                IsOpen = true,
                CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
                DefaultButton = destructive ? ContentDialogButton.None : ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = dialog.IsValid && context.State.IsConnected,
                OnClosed = result => controller.CloseDialog(result == ContentDialogResult.Primary),
            })
            .Set(native =>
            {
                if (destructive)
                {
                    native.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
                    native.Opened += (_, _) => FocusCloseButton(native);
                }
            })
            .AutomationId("SessionsHubDialog")
            .WithKey("dialog:" + RuntimeHelpers.GetHashCode(dialog.Completion));
    }

    private static void FocusCloseButton(ContentDialog dialog)
    {
        var closeText = dialog.CloseButtonText;
        if (string.IsNullOrWhiteSpace(closeText))
            return;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(dialog);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is Button button && Equals(button.Content, closeText))
            {
                button.Focus(FocusState.Programmatic);
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static Element Facts(SessionsHubDialog dialog)
    {
        if (dialog.Facts.Count == 0)
            return Empty();
        var cells = new List<Element>();
        for (var i = 0; i < dialog.Facts.Count; i++)
        {
            var fact = dialog.Facts[i];
            cells.Add(TextBlock(fact.Label).FontSize(12).Foreground(Theme.SecondaryText)
                .Margin(0, 0, 12, 6).Grid(row: i, column: 0));
            cells.Add(TextBlock(fact.Value).FontSize(12).TextWrapping(TextWrapping.Wrap)
                .IsTextSelectionEnabled().Margin(0, 0, 0, 6).Grid(row: i, column: 1));
        }
        return Border(Grid([GridSize.Px(120), GridSize.Star()],
                dialog.Facts.Select(_ => GridSize.Auto).ToArray(), cells.ToArray()))
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .CornerRadius(8)
            .Padding(12)
            .AutomationId("SessionsHubDialogFacts");
    }

    private static Element Items(SessionsHubDialog dialog)
    {
        if (dialog.Items.Count == 0)
            return Empty();
        var rows = dialog.Items.Select(item =>
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(item.Text).TextWrapping(TextWrapping.Wrap).Grid(column: 0),
                TextBlock(item.Reason).FontSize(12).Foreground(Theme.SecondaryText)
                    .Margin(12, 0, 0, 0).Grid(column: 1))).ToArray();
        return VStack(8,
            TextBlock(dialog.ItemsLabel ?? "").FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .Foreground(Theme.SecondaryText),
            VStack(6, rows)).AutomationId("SessionsHubDialogItems");
    }

    private static Element NewWorktree(SessionsHubViewContext context, SessionsHubDialog dialog)
    {
        var controller = context.Controller;
        var nameValid = WorktreesProjection.IsValidName(dialog.Name.Trim());
        var status = !nameValid ? T("NewWorktreeNameInvalid") : dialog.Status;
        var repoSuggestions = (context.State.Worktrees.Rows ?? Array.Empty<WorktreeRecord>())
            .Select(record => record.RepoRoot)
            .Where(repo => !string.IsNullOrWhiteSpace(repo))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        Element baseField = dialog.Branches.Count > 0
            ? ComboBox(dialog.Branches.ToArray(), Math.Max(0, dialog.Branches.ToList().IndexOf(dialog.BaseRef)),
                    index => controller.UpdateDialog(d => d with { BaseRef = index >= 0 && index < d.Branches.Count ? d.Branches[index] : d.BaseRef }))
                .Header(T("NewWorktreeBase"))
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationId("SessionsHubNewWorktreeBase")
            : TextBox(dialog.BaseRef, text => controller.UpdateDialog(d => d with { BaseRef = text }), T("NewWorktreeBasePlaceholder"), T("NewWorktreeBase"))
                .AutomationName(T("NewWorktreeBase"))
                .AutomationId("SessionsHubNewWorktreeBase");
        return VStack(12,
            TextBox(dialog.Repo, text => controller.UpdateDialog(d => d with { Repo = text, Branches = Array.Empty<string>(), BaseRef = "" }),
                    T("NewWorktreeRepoPlaceholder"), T("NewWorktreeRepo"))
                .AutomationName(T("NewWorktreeRepo"))
                .OnLostFocus((_, _) => _ = controller.LoadDialogBranchesAsync())
                .AutomationId("SessionsHubNewWorktreeRepo"),
            repoSuggestions.Length == 0
                ? Empty()
                : HStack(6, repoSuggestions.Select(repo =>
                    Button(WorktreesProjection.RepoName(repo), () =>
                        {
                            controller.UpdateDialog(d => d with
                            {
                                Repo = repo,
                                Branches = Array.Empty<string>(),
                                BaseRef = "",
                            });
                            _ = controller.LoadDialogBranchesAsync();
                        })
                        .SubtleButton()
                        .ToolTip(repo)
                        .AutomationName(repo)
                        .WithKey(repo)).ToArray()),
            TextBox(dialog.Name, text => controller.UpdateDialog(d => d with { Name = text }), T("NewWorktreeNamePlaceholder"), T("NewWorktreeName"))
                .MaxLength(64)
                .AutomationName(T("NewWorktreeName"))
                .AutomationId("SessionsHubNewWorktreeName"),
            baseField,
            TextBlock(status ?? "").FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)
                .LiveRegion().AutomationId("SessionsHubNewWorktreeStatus"),
            TextBlock(T("NewWorktreeDoesNotStartSession")).FontSize(12).Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.Wrap));
    }
}
