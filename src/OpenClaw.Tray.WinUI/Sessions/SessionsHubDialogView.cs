using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        Element content = dialog.Kind switch
        {
            SessionsHubDialogKind.Prompt => VStack(8,
                TextBlock(dialog.Body).TextWrapping(TextWrapping.Wrap),
                TextBox(dialog.Text, text => controller.UpdateDialog(d => d with { Text = text }), dialog.Placeholder)
                    .MaxLength(120)
                    .AutomationName(dialog.Title)
                    .AutomationId("SessionsHubPromptInput")),
            SessionsHubDialogKind.NewWorktree => NewWorktree(context, dialog),
            _ => TextBlock(dialog.Body).TextWrapping(TextWrapping.Wrap).AutomationId("SessionsHubDialogBody"),
        };
        var destructive = dialog.Destructive;
        return (ContentDialog(dialog.Title, content.MinWidth(360).MaxWidth(480), dialog.ConfirmLabel) with
            {
                IsOpen = true,
                CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
                DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = dialog.IsValid,
                OnClosed = result => controller.CloseDialog(result == ContentDialogResult.Primary),
            })
            .Set(native =>
            {
                if (destructive)
                    native.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            })
            .AutomationId("SessionsHubDialog")
            .WithKey("dialog:" + RuntimeHelpers.GetHashCode(dialog.Completion));
    }

    private static Element NewWorktree(SessionsHubViewContext context, SessionsHubDialog dialog)
    {
        var controller = context.Controller;
        var nameValid = WorktreesProjection.IsValidName(dialog.Name.Trim());
        var status = !nameValid ? T("NewWorktreeNameInvalid") : dialog.Status;
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
            TextBox(dialog.Name, text => controller.UpdateDialog(d => d with { Name = text }), T("NewWorktreeNamePlaceholder"), T("NewWorktreeName"))
                .MaxLength(64)
                .AutomationName(T("NewWorktreeName"))
                .AutomationId("SessionsHubNewWorktreeName"),
            baseField,
            TextBlock(status ?? "").FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)
                .LiveRegion().AutomationId("SessionsHubNewWorktreeStatus"));
    }
}
