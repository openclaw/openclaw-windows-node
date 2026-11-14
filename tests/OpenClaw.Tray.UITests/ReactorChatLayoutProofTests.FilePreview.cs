// Mounted proof that sidebar rows and attachment cards open the shared read-only file tab.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    private const string WorkspaceFileText = "Hello from the workspace.\nThis copy lives in C:\\workspace\\hello.txt.";
    private const string ArtifactFileText = "Hello from OpenClaw!\nThis attachment was produced by the agent.";

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task FileLinks_OpenSidebarPreviewAndKeepDownloadSeparate(string themeName)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Window? window = null;
        Border? surface = null;
        ChatThemeProofScope? theme = null;
        var reads = new List<ChatWorkspaceFileRequest>();
        var downloads = 0;
        var panels = new ChatConversationPanelState();
        var files = new ChatSessionFilesState();
        static ChatAssistantMediaPresentation Attachment(string name, string mime, string id, long size) =>
            new(ChatMediaContentKind.File, name, mime, null, new()
            {
                Kind = ChatMediaContentKind.File, Source = ChatMediaContentSource.Structured,
                ArtifactId = id, SizeBytes = size,
            });
        var text = Attachment("hello.txt", "text/plain", "artifact-1", 78);
        var zip = Attachment("agent-images.zip", "application/zip", "artifact-2", 64690236);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope(themeName);
                panels.Bind(this, "main", 1, 1, 1, [], () => true, subagentsCurrent: () => true);
                files.Bind("main", 1, 1, 1, false, _ => Task.FromResult(new SessionFileList
                {
                    Key = "main",
                    Root = @"C:\workspace",
                    Files =
                    [
                        new() { Name = "hello.txt", Path = @"C:\workspace\hello.txt", Kind = "modified", Size = 78 },
                        new() { Name = "AGENTS.md", Path = "AGENTS.md", Kind = "read", Size = 6451 },
                    ],
                }));
                panels.OpenFiles(panels.Revision);
                void OpenAttachment(ChatAssistantMediaPresentation item) =>
                    panels.OpenFile(new("main", item.DisplayName, item.Reference.ArtifactId), panels.Revision);
                Task<AssistantFileDownloadStatus> Download(string _, ChatMediaContentInfo __, CancellationToken ___)
                {
                    downloads++;
                    return Task.FromResult(AssistantFileDownloadStatus.Started);
                }
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<ReactorConversationPanelHost, ReactorConversationPanelHostProps>(new(
                    VStack(12,
                            TextBlock("I wrote hello.txt and packaged the images for you.").TextWrapping(TextWrapping.Wrap),
                            ChatAssistantMediaRenderer.Render(text, "main", null, Download, OpenAttachment),
                            ChatAssistantMediaRenderer.Render(zip, "main", null, Download, OpenAttachment))
                        .Margin(24).VAlign(VerticalAlignment.Top),
                    panels,
                    Files: Component<ReactorChatFiles, ReactorChatFilesProps>(new(files, files.Version,
                        path => panels.OpenFile(new("main", path), panels.Revision))),
                    LoadWorkspaceFile: (request, _) =>
                    {
                        reads.Add(request);
                        return Task.FromResult(request.ArtifactId switch
                        {
                            null => new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, WorkspaceFileText),
                            "artifact-1" => new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, ArtifactFileText),
                            _ => new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.NotPreviewable),
                        });
                    })));
                surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    // Solid fill: RenderTargetBitmap cannot capture Mica, so a translucent surface misrenders Dark.
                    "Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" />");
                surface.Width = 1100;
                surface.Height = 640;
                surface.HorizontalAlignment = HorizontalAlignment.Left;
                surface.VerticalAlignment = VerticalAlignment.Top;
                surface.RequestedTheme = theme.ElementTheme;
                surface.Child = host;
                window = new Window { Content = surface };
                _captureWindow = window;
                var position = ui.IsSlow ? 80 : -32000;
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position, position, 1180, 720));
                window.Activate();
            });

            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<Button>(surface!).Count(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatFilesOpenFile" && button.IsLoaded) == 2,
                "file list mounts invokable rows"));
            await CaptureAsync(surface!, $"FileLinks-{themeName}-1-FilesList");

            await ui.RunOnUIAsync(() => Invoke(FilesRow(surface!, "hello.txt")));
            await WaitForTextAsync(surface!, "Hello from the workspace.", "workspace file opens in the sidebar");
            Assert.Equal(new ChatWorkspaceFileRequest("main", @"C:\workspace\hello.txt"), Assert.Single(reads));
            await CaptureAsync(surface!, $"FileLinks-{themeName}-2-WorkspacePreview");

            await ui.RunOnUIAsync(() => Invoke(AttachmentOpen(surface!, 0)));
            await WaitForTextAsync(surface!, "Hello from OpenClaw!",
                "attachment opens its artifact rather than a same-named workspace file");
            Assert.Equal(new ChatWorkspaceFileRequest("main", "hello.txt", "artifact-1"), reads[1]);
            await CaptureAsync(surface!, $"FileLinks-{themeName}-3-AttachmentPreview");

            await ui.RunOnUIAsync(() => Invoke(AttachmentOpen(surface!, 1)));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => reads.Count == 3 && FindDescendants<TextBlock>(surface!).Any(block =>
                    AutomationProperties.GetAutomationId(block) == "ChatWorkspaceFileStatus" && block.ActualHeight > 0),
                "binary attachment shows the non-previewable state"));
            Assert.Equal(new ChatWorkspaceFileRequest("main", "agent-images.zip", "artifact-2"), reads[2]);
            await CaptureAsync(surface!, $"FileLinks-{themeName}-4-NotPreviewable");

            Assert.Equal(0, downloads);
            await ui.RunOnUIAsync(() =>
            {
                var download = FindDescendants<Button>(surface!).First(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatAssistantFileDownload");
                Assert.Equal("Download", AutomationProperties.GetName(download));
                Assert.Equal("Download", ToolTipService.GetToolTip(download));
                Assert.Equal("\uE896", Assert.IsType<TextBlock>(download.Content).Text);
                Invoke(download);
            });
            Assert.Equal(1, downloads);
            Assert.Equal(3, reads.Count);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                window?.Close();
                _captureWindow = null;
                theme?.Dispose();
            });
        }
    }

    [Fact]
    public async Task PanelTabs_AdoptNativeDragOrderAndShowUnselectedCloseOnHover()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Border? surface = null;
        ChatThemeProofScope? theme = null;
        var reads = 0;
        var panels = new ChatConversationPanelState();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope("Light");
                panels.Bind(this, "main", 1, 1, 1, [], () => true, subagentsCurrent: () => true);
                panels.OpenFiles(panels.Revision);
                panels.OpenFile(new("main", "hello.txt"), panels.Revision);
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<ReactorConversationPanelHost, ReactorConversationPanelHostProps>(new(
                    TextBlock("Conversation"), panels, Files: TextBlock("Files body"),
                    LoadWorkspaceFile: (_, _) =>
                    {
                        reads++;
                        return Task.FromResult(new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, "Body"));
                    })));
                surface = new Border { Child = host, Width = 1100, Height = 640, RequestedTheme = theme.ElementTheme };
                ui.Container.Children.Add(surface);
                _captureWindow = ui.TestWindow;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindOptional<TabView>(surface!, "ConversationPanelTabs") is { TabItems.Count: 2 } && reads == 1,
                "files and file tabs mount"));
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                Assert.True(view.CanReorderTabs);
                Assert.Equal(TabViewCloseButtonOverlayMode.OnPointerOver, view.CloseButtonOverlayMode);
                // Native drag-reorder moves the item within TabItems: remove, then insert.
                var dragged = view.TabItems[0];
                view.TabItems.RemoveAt(0);
                view.TabItems.Add(dragged);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => panels.Tabs.SequenceEqual(["file", "files"]),
                "panel state adopts the dragged order"));
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                Assert.Equal(["hello.txt", "Files"], view.TabItems.Cast<TabViewItem>()
                    .Select(item => item.Header as string));
                Assert.Equal("file", panels.Selected);
                view.SelectedIndex = 1;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => panels.Selected == "files", "selecting a moved tab resolves its own slot"));
            Assert.Equal(1, reads);
            await CaptureAsync(surface!, "PanelTabs-Reordered");

            // Drag the selected tab back to the front, then close it from its native close button.
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                var dragged = view.TabItems[1];
                view.TabItems.RemoveAt(1);
                view.TabItems.Insert(0, dragged);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => panels.Tabs.SequenceEqual(["files", "file"]) && panels.Selected == "files",
                "the moved selected tab keeps its selection"));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindControl<TabView>(surface!, "ConversationPanelTabs").TabItems.Cast<TabViewItem>()
                    .FirstOrDefault(item => item.Header as string == "Files") is { } item &&
                    FindDescendants<Button>(item).Any(button => button.Name == "CloseButton" && button.IsLoaded),
                "the selected tab's close button mounts"));
            await ui.RunOnUIAsync(() => Invoke(FindDescendants<Button>(
                    FindControl<TabView>(surface!, "ConversationPanelTabs").TabItems.Cast<TabViewItem>()
                        .Single(item => item.Header as string == "Files"))
                .Single(button => button.Name == "CloseButton")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => panels.Tabs.SequenceEqual(["file"]) && panels.Selected == "file",
                "closing a moved tab closes that slot"));
            Assert.Equal(1, reads);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                theme?.Dispose();
                _captureWindow = null;
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelTabs_RetiredDragOrderCannotReorderAReboundOrUnmountedPanel(bool unmount)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Border? surface = null;
        TabView? view = null;
        var panels = new ChatConversationPanelState();
        void OpenFilesThenFile(object provider)
        {
            panels.Bind(provider, "main", 1, 1, 1, [], () => true, subagentsCurrent: () => true);
            panels.OpenFiles(panels.Revision);
            panels.OpenFile(new("main", "hello.txt"), panels.Revision);
        }
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                OpenFilesThenFile(this);
                host = new ReactorHostControl();
                host.Mount(_ => Component<ReactorConversationPanelHost, ReactorConversationPanelHostProps>(new(
                    TextBlock("Conversation"), panels, Files: TextBlock("Files body"),
                    LoadWorkspaceFile: (_, _) => Task.FromResult(new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, "")))));
                surface = new Border { Child = host, Width = 1100, Height = 640 };
                ui.Container.Children.Add(surface);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindOptional<TabView>(surface!, "ConversationPanelTabs") is { TabItems.Count: 2 },
                "files and file tabs mount"));
            // Rerender before teardown so a per-render handler would differ from the one attached at mount.
            await ui.RunOnUIAsync(() => panels.Select("files"));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindControl<TabView>(surface!, "ConversationPanelTabs").SelectedIndex == 0,
                "selection rerenders the strip"));
            await ui.RunOnUIAsync(() =>
            {
                view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                if (unmount)
                {
                    host!.Dispose();
                    surface!.Child = null;
                    view.TabItems.Clear();
                    view.TabItems.Add(new TabViewItem { Content = new Border { Tag = "file" } });
                    view.TabItems.Add(new TabViewItem { Content = new Border { Tag = "files" } });
                }
                else
                {
                    var dragged = view.TabItems[0];
                    view.TabItems.RemoveAt(0);
                    view.TabItems.Add(dragged);
                    // The conversation is replaced before the queued drag order runs; it reopens the same slots.
                    OpenFilesThenFile(new object());
                }
            });
            // A later UI dispatch runs after the queued drag order.
            await ui.RunOnUIAsync(() => Assert.Equal(["files", "file"], panels.Tabs));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
            });
        }
    }

    private Task WaitForTextAsync(FrameworkElement surface, string prefix, string description) =>
        ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
            () => FindDescendants<TextBlock>(surface).Any(block => block.Text.StartsWith(prefix, StringComparison.Ordinal)),
            description));

    private static Button FilesRow(FrameworkElement surface, string name) =>
        FindDescendants<Button>(surface).First(button =>
            AutomationProperties.GetAutomationId(button) == "ChatFilesOpenFile" &&
            AutomationProperties.GetName(button).StartsWith(name + ",", StringComparison.Ordinal));

    private static Button AttachmentOpen(FrameworkElement surface, int index) =>
        FindDescendants<Button>(surface).Where(button =>
            AutomationProperties.GetAutomationId(button) == "ChatAssistantFileOpen").ElementAt(index);
}
