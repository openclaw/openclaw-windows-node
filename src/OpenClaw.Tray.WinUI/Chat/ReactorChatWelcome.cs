using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

[GenerateReactorWrapper(typeof(NativeChatWelcomeAvatar), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeChatWelcomeAvatarElement;

/// <summary>Native welcome content. The composer owns its header; the root places secondary content below it.</summary>
internal static class ReactorChatWelcome
{
    internal static Element Header(WorkspaceAgent agent, ChatWelcomeContent? content, double viewportWidth) =>
        VStack(8,
            NativeChatWelcomeAvatarElement.NativeChatWelcomeAvatar()
                .Set(avatar => avatar.Update(agent, ChatVisuals.WelcomeAvatarSize(viewportWidth)))
                .Size(ChatVisuals.WelcomeAvatarSize(viewportWidth), ChatVisuals.WelcomeAvatarSize(viewportWidth))
                .HAlign(HorizontalAlignment.Center).Margin(0, 0, 0, 8)
                .AccessibilityView(AccessibilityView.Raw)
                .AutomationId("ChatWelcomeAvatar"),
            TextBlock(agent.Name)
                .FontSize(24).FontWeight(FontWeights.SemiBold)
                .TextWrapping(TextWrapping.Wrap)
                .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Stretch)
                .AutomationId("ChatWelcomeAgent"),
            TextBlock(content is { Status: ChatWelcomeContentStatus.Available, Catalog: { } catalog }
                    ? catalog.Hint
                    : LocalizationHelper.GetString(content?.Status switch
                    {
                        ChatWelcomeContentStatus.Loading => "Chat_Welcome_Loading",
                        ChatWelcomeContentStatus.Failed => "Chat_Welcome_Failed",
                        _ => "Chat_Welcome_Unsupported"
                    }))
                .FontSize(14).Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .TextWrapping(TextWrapping.Wrap)
                .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Stretch)
                .AutomationId("ChatWelcomeHint"))
            .Margin(0, 0, 0, 16)
            .HAlign(HorizontalAlignment.Stretch);

    internal static Element Secondary(
        IReadOnlyList<ChatThread> recents,
        Action<string>? onRecentPicked,
        Action<string>? onSuggestionPicked,
        bool disabled,
        ChatWelcomeContent? content)
    {
        var catalog = content?.Status == ChatWelcomeContentStatus.Available ? content.Catalog : null;
        var children = new List<Element>();
        if (recents.Count > 0)
        {
            children.Add(TextBlock(catalog?.RecentChatsLabel ?? LocalizationHelper.GetString("Chat_ZeroState_RecentChats"))
                .FontWeight(FontWeights.SemiBold).FontSize(14)
                .Margin(0, 0, 0, 4));
            children.AddRange(recents.Select(thread => RecentChat(thread, onRecentPicked, disabled)));
        }
        else if (catalog is not null)
        {
            foreach (var suggestion in catalog.Suggestions)
            {
                children.Add(Choice(suggestion.Label, "ChatSuggestion_" + suggestion.Id,
                    () => onSuggestionPicked?.Invoke(suggestion.Prompt), !disabled && onSuggestionPicked is not null)
                    .WithKey(suggestion.Id));
            }
        }
        return VStack(8, children.ToArray())
            .MaxWidth(ChatVisuals.ReadingWidth)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("ChatWelcomeSecondary");
    }

    private static Element RecentChat(ChatThread thread, Action<string>? onRecentPicked, bool disabled)
    {
        var timestamp = ChatWelcomePresentation.LastUsed(thread.UpdatedAt, DateTimeOffset.UtcNow);
        var lastUsed = thread.UpdatedAt is { } date
            ? LocalizationHelper.Format("WorkspaceShell_SessionMenu_LastActiveFormat", date.ToLocalTime().ToString("g"))
            : "";
        var row = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            TextBlock(thread.DisplayTitle).FontSize(13)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .TextAlignment(TextAlignment.Left).VAlign(VerticalAlignment.Center)
                .AutomationId("ChatRecentTitle_" + thread.Id).Grid(column: 0),
            TextBlock(timestamp).FontSize(12)
                .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                .Margin(timestamp.Length > 0 ? 12 : 0, 0, 0, 0)
                .VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Right)
                .AutomationId("ChatRecentTime_" + thread.Id).Grid(column: 1));
        return Choice(thread.DisplayTitle, $"ChatRecent_{thread.Id}",
                () => onRecentPicked?.Invoke(thread.Id), !disabled && onRecentPicked is not null, row, lastUsed)
            .Padding(12, 8)
            .WithKey(thread.Id);
    }

    private static Element Choice(string text, string id, Action action, bool enabled,
        Element? content = null, string? lastUsed = null) =>
        Button(content ?? TextBlock(text).TextWrapping(TextWrapping.Wrap), action)
            .IsEnabled(enabled).MinHeight(40).BorderThickness(0)
            .Set(button =>
            {
                if (content is null) return;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetHelpText(button, lastUsed ?? "");
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button,
                    string.IsNullOrEmpty(lastUsed) ? text : $"{text}\n{lastUsed}");
            })
            .Resources(resources =>
            {
                ChatVisuals.ToolbarButtonResources(resources);
                resources.Set("ButtonBackground", Theme.Ref("ControlAltFillColorSecondaryBrush"));
            })
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId(id).AutomationName(text);
}
