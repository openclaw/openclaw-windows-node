// List content for the shared conversation panel's Files tab. Rows open read-only previews; the chat root
// owns the ChatSessionFilesState read: Component<ReactorChatFiles, ReactorChatFilesProps>(new(files, files.Version)).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatFilesProps(
    ChatSessionFilesState Files, long FilesVersion, Action<string>? OpenFile = null);

/// <summary>Session files the agent edited or read, with the workspace root and a manual refresh.</summary>
internal sealed class ReactorChatFiles : Component<ReactorChatFilesProps>
{
    public override Element Render()
    {
        var files = Props.Files;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setRevision(++revision);
            files.Changed += Changed;
            return () => files.Changed -= Changed;
        }), files);

        var state = files.Files;
        var rows = files.Rows;
        var status = files.Status switch
        {
            ChatSessionFilesStatus.Loading when rows.Count == 0 => L("Chat_Files_Loading", "Loading files..."),
            ChatSessionFilesStatus.Unsupported => L("Chat_Files_Unsupported", "This Gateway does not provide session files."),
            ChatSessionFilesStatus.Failed => L("Chat_Files_Failed", "Files could not be loaded. Refresh to try again."),
            ChatSessionFilesStatus.Loaded when rows.Count == 0 => L("Chat_Files_Empty", "No files in this session yet."),
            _ => null,
        };
        var refreshLabel = L("Chat_Files_Refresh", "Refresh files");
        var header = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            TextBlock(state.WorkspacePath).FontSize(12).Set(ChatVisuals.StyleSecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(state.WorkspacePath)
                .VAlign(VerticalAlignment.Center).AutomationId("ChatFilesRoot").Grid(column: 0),
            Button(TextBlock(FluentIconCatalog.Refresh).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .FontSize(14).AccessibilityView(AccessibilityView.Raw), files.Refresh)
                .Resources(ChatVisuals.ToolbarButtonResources)
                .IsEnabled(files.Status != ChatSessionFilesStatus.Loading)
                .AutomationName(refreshLabel).ToolTip(refreshLabel)
                .AutomationId("ChatFilesRefresh").Grid(column: 1));
        var children = new List<Element> { header };
        if (status is not null)
            children.Add(TextBlock(status).TextWrapping(TextWrapping.Wrap).Set(ChatVisuals.StyleSecondaryText)
                .AutomationId("ChatFilesStatus")
                .LiveRegion(AutomationLiveSetting.Polite));
        children.Add(VStack(0, rows.Select(entry => Row(entry, Props.OpenFile).WithKey(entry.RelativePath)).ToArray())
            .AutomationId("ChatFilesRows"));
        var title = L("Chat_Files_Title", "Files");
        return Border(ScrollViewer(VStack(ChatVisuals.BlockGap, children.ToArray()).Margin(ChatVisuals.BlockGap))
                .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
                .AutomationId("ChatFilesList"))
            .AutomationId("ChatFilesPane")
            .AutomationName(title);
    }

    private static Element Row(WorkspaceFilesModel.WorkspaceFileEntry entry, Action<string>? openFile)
    {
        var badge = !entry.Exists ? L("WorkspacePage_Badge_Missing", "Missing")
            : entry.Touched ? L("WorkspacePage_Badge_Edited", "Edited")
            : entry.Read ? L("WorkspacePage_Badge_Read", "Read")
            : null;
        var directory = WorkspaceFilesModel.DirectoryOf(entry.RelativePath);
        var detail = string.Join(" · ", new[] { directory, WorkspaceFilesModel.FormatSize(entry.Size) }
            .Where(part => part.Length > 0));
        var accessible = string.Join(", ", new[] { entry.Name, badge ?? "", detail }.Where(part => part.Length > 0));
        var content = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                VStack(2,
                    TextBlock(entry.Name).FontSize(ChatVisuals.BodySize)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(entry.RelativePath),
                    detail.Length > 0
                        ? TextBlock(detail).FontSize(12).Set(ChatVisuals.StyleSecondaryText)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                        : Empty())
                    .VAlign(VerticalAlignment.Center).Grid(column: 0),
                badge is null ? Empty() : Border(TextBlock(badge).FontSize(11)
                        .Foreground(Theme.Ref(entry.Exists ? "ChatTextBrush" : "SystemFillColorCriticalBrush")))
                    .Padding(7, 0).CornerRadius(9)
                    .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                    .Margin(ChatVisuals.CompactGap, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center).Grid(column: 1))
            .Padding(ChatVisuals.BlockGap, ChatVisuals.CompactGap);
        return Button(content, () => openFile?.Invoke(entry.RequestPath))
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Padding(0).BorderThickness(0)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .IsEnabled(entry.CanPreview && !entry.IsDirectory && openFile is not null)
            .AutomationName(accessible)
            .AutomationId("ChatFilesOpenFile")
            .ToolTip(entry.RelativePath);
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
