// Files pill above the composer. It shows the selected chat's session file count and opens the
// shared Files tab: Component<ReactorChatFilesPill, ReactorChatFilesPillProps>(new(panels, files, panels.Revision, ready, files.Version)).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatFilesPillProps(
    ChatConversationPanelState Panels, ChatSessionFilesState Files, long Revision, bool Ready, long FilesVersion);

/// <summary>Shown once the session has files or while the Files tab is open; selects the tab.</summary>
internal sealed class ReactorChatFilesPill : Component<ReactorChatFilesPillProps>
{
    public override Element Render()
    {
        var panels = Props.Panels;
        var files = Props.Files;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        var (_, setThemeRevision) = UseState(0L, threadSafe: true);
        var themeRevision = UseRef(0L);
        var highContrast = ChatThemeChanges.IsHighContrast();
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setRevision(++revision);
            panels.Changed += Changed;
            files.Changed += Changed;
            return () =>
            {
                panels.Changed -= Changed;
                files.Changed -= Changed;
            };
        }), panels, files);

        var count = files.FileCount;
        // A failed first read keeps the pill so the tab's status and Refresh stay reachable.
        var hasContent = count > 0 || files.Status == ChatSessionFilesStatus.Failed;
        if (!Props.Ready || !panels.AreSubagentsReady ||
            !hasContent && !panels.Tabs.Contains(ChatConversationPanelState.FilesSlot))
            return Empty();

        var label = L("Chat_Files_Title", "Files");
        var accessibleName = string.Format(L("Chat_Files_PillName", "Session files: {0}"), count);
        var icon = ChatPanelPill.Muted(TextBlock(FluentIconCatalog.Folder)
                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(ChatVisuals.SubagentPillIconSize), highContrast)
            .AccessibilityView(AccessibilityView.Raw);
        // The root passes the bound admission revision; stable state references alone would not re-render the pill.
        var revisionAtRender = Props.Revision;
        return ChatPanelPill.Render(icon, label,
                count.ToString(System.Globalization.CultureInfo.CurrentCulture), "ChatFilesPillCount",
                accessibleName, "ChatFilesPill", highContrast, () => panels.OpenFiles(revisionAtRender))
            .OnMount(control => ChatThemeChanges.Watch((FrameworkElement)control,
                () => setThemeRevision(++themeRevision.Current)))
            .OnUnmount(control => ChatThemeChanges.Unwatch((FrameworkElement)control));
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
