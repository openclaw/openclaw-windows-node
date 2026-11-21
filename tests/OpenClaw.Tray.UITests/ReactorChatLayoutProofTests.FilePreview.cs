// Mounted proof that sidebar rows and attachment cards open the shared read-only file tab.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Chat;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using Windows.Foundation;
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
                // Removing the selected tab makes the native strip select its neighbor, which can arrive after
                // the reinsert (as on CI). Send that late selection deterministically.
                view.SelectedIndex = 1;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => panels.Tabs.SequenceEqual(["files", "file"]) && panels.Selected == "files" &&
                    FindControl<TabView>(surface!, "ConversationPanelTabs").SelectedIndex == 0,
                "the moved selected tab keeps its selection",
                () => $"state=[{string.Join(",", panels.Tabs)}] selected={panels.Selected} native=[" +
                    string.Join(",", FindControl<TabView>(surface!, "ConversationPanelTabs").TabItems
                        .Cast<TabViewItem>().Select(item => item.Header)) + "] nativeSelected=" +
                    FindControl<TabView>(surface!, "ConversationPanelTabs").SelectedIndex));
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

    // Wide-mode close, expand and collapse ease the panel column like its open. While it moves, the moving
    // part keeps its resting width at the start of its column and a closing panel stays drawn without
    // taking input. Intermediate widths depend on frame timing, so the proof samples every frame and
    // layout pass and asserts only states that exist solely during the motion, plus the settled layout.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelColumn_EasesCloseExpandAndCollapseLikeOpen(bool animate)
    {
        await ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(animate);
        ReactorHostControl? host = null;
        Border? surface = null;
        ChatThemeProofScope? theme = null;
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
                        Task.FromResult(new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, "Body")))));
                surface = new Border { Child = host, Width = 1100, Height = 640, RequestedTheme = theme.ElementTheme };
                ui.Container.Children.Add(surface);
                _captureWindow = ui.TestWindow;
            });
            double area = 0, split = 0;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () =>
                {
                    if (FindOptional<TabView>(surface!, "ConversationPanelTabs") is not { TabItems.Count: 2 })
                        return false;
                    var probe = new PanelColumnProbe(surface!);
                    area = probe.Root.ActualWidth;
                    split = panels.EffectiveWidth(area);
                    return area > 1000 && IsSplit(probe.Sample());
                },
                "the split panel mounts at its resting width"));

            bool Near(double value, double expected) => Math.Abs(value - expected) < 0.5;
            bool Unpinned(PanelColumnSample s) => !s.PanelPinned && !s.ConversationPinned;
            bool IsSplit(PanelColumnSample s) =>
                Near(s.Divider, ChatConversationPanelState.DividerWidth) && Near(s.Panel, split) &&
                s.PanelShown && s.PanelTakesInput && s.ConversationShown && s.ResizeShown && Unpinned(s);
            // At rest, a panel that fills the area takes the star column and the panel column closes.
            bool IsExpanded(PanelColumnSample s) =>
                Near(s.Divider, 0) && Near(s.Panel, 0) && s.PanelShown && s.PanelTakesInput &&
                !s.ConversationShown && !s.ResizeShown && Unpinned(s);
            bool IsHidden(PanelColumnSample s) =>
                Near(s.Divider, 0) && Near(s.Panel, 0) && !s.PanelShown && s.ConversationShown && Unpinned(s);

            Func<PanelColumnSample, bool> resting = IsSplit;
            async Task StepAsync(string name, Action change, double from, double to,
                Func<PanelColumnSample, bool> settled, Func<PanelColumnSample, bool> moving)
            {
                var before = resting;
                resting = settled;
                var samples = new List<PanelColumnSample>();
                await ui.RunOnUIAsync(async () =>
                {
                    var probe = new PanelColumnProbe(surface!);
                    void Take(object? sender, object args) => samples.Add(probe.Sample());
                    CompositionTarget.Rendering += Take;
                    probe.Root.LayoutUpdated += Take;
                    try
                    {
                        change();
                        await WaitForRenderedConditionAsync(() => settled(probe.Sample()), name + " settles",
                            () => probe.Sample().ToString());
                    }
                    finally
                    {
                        CompositionTarget.Rendering -= Take;
                        probe.Root.LayoutUpdated -= Take;
                    }
                    samples.Add(probe.Sample());
                });
                Assert.True(settled(samples[^1]), $"{name}: {samples[^1]}");
                // Between its resting layouts, the column only travels between the two extents and the
                // divider never widens.
                Assert.All(samples.Where(s => !before(s) && !settled(s)), s =>
                {
                    Assert.InRange(s.Panel, Math.Min(from, to) - 0.5, Math.Max(from, to) + 0.5);
                    Assert.InRange(s.Divider, -0.5, ChatConversationPanelState.DividerWidth + 0.5);
                });
                if (animate)
                    Assert.Contains(samples, s => moving(s));
                else
                    Assert.DoesNotContain(samples, s => s.PanelPinned || s.ConversationPinned || !s.PanelTakesInput);
            }

            // Expanding pins the conversation while the panel grows over it; collapsing reverses that.
            await StepAsync("expand", panels.ToggleExpanded, split, area, IsExpanded,
                s => s.ConversationPinned && s.ConversationShown && !s.ResizeShown);
            await StepAsync("collapse", panels.ToggleExpanded, area, split, IsSplit,
                s => s.ConversationPinned && !s.ResizeShown);
            // Closing keeps the panel drawn at its width while the column hides it, without taking input.
            await StepAsync("close", panels.Hide, split, 0, IsHidden,
                s => s.PanelPinned && s.PanelShown && !s.PanelTakesInput);
            await StepAsync("open", panels.ToggleOpen, 0, split, IsSplit,
                s => s.PanelPinned && s.PanelShown && s.PanelTakesInput && !s.ResizeShown);
            // Closing from the expanded panel shrinks it from the full area.
            await StepAsync("expand again", panels.ToggleExpanded, split, area, IsExpanded,
                s => s.ConversationPinned && s.ConversationShown && !s.ResizeShown);
            await StepAsync("close expanded", panels.Hide, area, 0, IsHidden,
                s => s.PanelPinned && s.ConversationPinned && s.PanelShown && !s.PanelTakesInput);
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

    private sealed record PanelColumnSample(
        double Divider, double Panel, bool PanelShown, bool PanelTakesInput, bool ConversationShown,
        bool PanelPinned, bool ConversationPinned, bool ResizeShown);

    // Reads the panel host's columns and the motion-only state of the elements beside them.
    private sealed class PanelColumnProbe
    {
        private readonly Grid _panel;
        private readonly Border _conversation;
        private readonly Button _resize;
        private readonly FrameworkElement _panelSlot;
        private readonly FrameworkElement _conversationSlot;

        internal PanelColumnProbe(DependencyObject surface)
        {
            Root = (Grid)VisualTreeHelper.GetParent(FindControl<Border>(surface, "ConversationPanelDivider"));
            _panel = FindControl<Grid>(surface, "ConversationPanel");
            _conversation = FindControl<Border>(surface, "ChatConversation");
            _resize = FindControl<Button>(surface, "ConversationPanelResize");
            _panelSlot = RootChild(_panel);
            _conversationSlot = RootChild(_conversation);
        }

        internal Grid Root { get; }

        internal PanelColumnSample Sample() => new(
            Root.ColumnDefinitions[1].Width.Value, Root.ColumnDefinitions[2].Width.Value,
            _panel.Visibility == Visibility.Visible, _panel.IsHitTestVisible,
            _conversation.Visibility == Visibility.Visible,
            Pinned(_panelSlot), Pinned(_conversationSlot), _resize.Visibility == Visibility.Visible);

        private static bool Pinned(FrameworkElement slot) =>
            slot.ReadLocalValue(FrameworkElement.WidthProperty) != DependencyProperty.UnsetValue;

        // The host's slot for an element is its ancestor that sits directly in the root grid.
        private FrameworkElement RootChild(DependencyObject element)
        {
            while (VisualTreeHelper.GetParent(element) is { } parent && !ReferenceEquals(parent, Root))
                element = parent;
            return (FrameworkElement)element;
        }
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("HighContrast", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", true)]
    [InlineData("HighContrast", true)]
    public async Task PanelTabs_LongTitlesStayBoundedAndNativeOverflowScrollsWithoutDroppingTabs(
        string themeName, bool joinsCaptionRow)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Border? surface = null;
        ChatThemeProofScope? theme = null;
        var panels = new ChatConversationPanelState();
        var name = new string('w', 180) + ".txt";
        var entry = new ChatTimelineItem("tool", ChatTimelineItemKind.ToolCall, "read",
            ToolName: "read", ToolOutput: "Tool output");
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope(themeName);
                panels.Bind(this, "main", 1, 1, 1, [entry], () => true, subagentsCurrent: () => true);
                panels.OpenFiles(panels.Revision);
                panels.OpenFile(new("main", name), panels.Revision);
                if (joinsCaptionRow)
                    panels.SetTitleRow(56, endInset: 120, captionHeight: 48);
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<ReactorConversationPanelHost, ReactorConversationPanelHostProps>(new(
                    TextBlock("Conversation"), panels, Files: TextBlock("Files body"),
                    Subagents: TextBlock("Subagents body"),
                    LoadWorkspaceFile: (_, _) => Task.FromResult(
                        new ChatWorkspaceFileResult(ChatWorkspaceFileStatus.Loaded, "File body")))));
                surface = new Border { Child = host, Width = 1100, Height = 640, RequestedTheme = theme.ElementTheme };
                ui.Container.Children.Add(surface);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindOptional<TabView>(surface!, "ConversationPanelTabs") is { TabItems.Count: 2 } view &&
                    view.TabItems.Cast<TabViewItem>().All(item => item.ActualWidth is > 0 and <= 200.5),
                "long filename cannot grow its tab beyond 200 DIPs"));
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                Assert.Equal(joinsCaptionRow, panels.JoinsTitleRow);
                var panel = FindControl<Grid>(surface!, "ConversationPanel");
                Assert.Equal(
                    Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(panel.Background).Color,
                    Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(view.Background).Color);
                if (themeName != "HighContrast")
                    Assert.Equal(0, Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(view.Background).Color.A);
                var selected = Assert.IsType<TabViewItem>(view.SelectedItem);
                Assert.Equal(name, selected.Header);
                var title = Assert.Single(FindDescendants<TextBlock>(selected), text => text.Text == name);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                var primary = themeName == "HighContrast"
                    ? (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["ChatPanelTabSelectedTextBrush"]
                    : Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(
                        Assert.Single(FindDescendants<TextBlock>(surface!), text => text.Text == "Conversation").Foreground);
                Assert.Equal(primary.Color,
                    Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(title.Foreground).Color);
                Assert.Equal("\uE8A5", Assert.IsType<FontIconSource>(selected.IconSource).Glyph);
                var shell = Assert.Single(FindDescendants<Grid>(selected), grid => grid.Name == "TabContainer");
                Assert.True(shell.CornerRadius.TopLeft > 0 && shell.CornerRadius.BottomLeft > 0 &&
                    shell.CornerRadius.TopRight > 0 && shell.CornerRadius.BottomRight > 0);
                var viewport = Assert.Single(FindDescendants<ScrollContentPresenter>(view));
                var shellBounds = Bounds(shell, viewport);
                Assert.InRange(shell.ActualHeight, 31.5, 32.5);
                Assert.InRange(shellBounds.Top, 3.5, 4.5);
                Assert.InRange(viewport.ActualHeight - shellBounds.Bottom, 3.5, 4.5);
                Assert.True(Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(shell.Background).Color.A > 0);
                var inactive = Assert.Single(FindDescendants<Grid>((TabViewItem)view.TabItems[0]),
                    grid => grid.Name == "TabContainer");
                Assert.Equal(0, Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(inactive.Background).Color.A);
                panels.Open(ChatToolDetailRequest.Capture("main", 1, 1, entry), panels.Revision);
                panels.OpenSubagents(panels.Revision);
                panels.OpenDashboard(new("main", "main"));
                panels.Resize(280);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindOptional<TabView>(surface!, "ConversationPanelTabs") is { TabItems.Count: 5 } view &&
                    FindDescendants<ScrollViewer>(view).Any(scroll => scroll.ScrollableWidth > 0) &&
                    FindDescendants<RepeatButton>(view).Any(button =>
                        button.Name == "ScrollIncreaseButton" && button.ActualWidth > 0),
                "five bounded tabs overflow into the native horizontal scroll controls"));
            // The native ItemsStackPanel can virtualize offscreen tabs. Select each before measuring it.
            for (var index = 0; index < 5; index++)
            {
                var tabIndex = index;
                await ui.RunOnUIAsync(() =>
                    FindControl<TabView>(surface!, "ConversationPanelTabs").SelectedIndex = tabIndex);
                Rect? previous = null;
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    if (FindOptional<TabView>(surface!, "ConversationPanelTabs") is not { TabItems.Count: 5 } view ||
                        view.SelectedIndex != tabIndex ||
                        view.TabItems[tabIndex] is not TabViewItem { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } item)
                        return false;
                    var bounds = Bounds(item, view);
                    var stable = previous == bounds;
                    previous = bounds;
                    return stable;
                }, $"overflow tab {tabIndex} is realized and has stable bounds"));
                await ui.RunOnUIAsync(() =>
                {
                    var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                    Assert.InRange(Assert.IsType<TabViewItem>(view.TabItems[tabIndex]).ActualWidth, 99.5, 200.5);
                    Assert.Equal(5, panels.Tabs.Count);
                });
            }
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                var scroll = Assert.Single(FindDescendants<ScrollViewer>(view),
                    candidate => candidate.ScrollableWidth > 0);
                scroll.ChangeView(0, null, null, disableAnimation: true);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<RepeatButton>(FindControl<TabView>(surface!, "ConversationPanelTabs"))
                    .Any(button => button.Name == "ScrollIncreaseButton" && button.IsEnabled),
                "native overflow can scroll toward the hidden tabs"));
            await ui.RunOnUIAsync(() => Invoke(
                FindDescendants<RepeatButton>(FindControl<TabView>(surface!, "ConversationPanelTabs"))
                    .Single(button => button.Name == "ScrollIncreaseButton")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<ScrollViewer>(FindControl<TabView>(surface!, "ConversationPanelTabs"))
                    .Any(scroll => scroll.HorizontalOffset > 0),
                "native overflow arrow scrolls the tab strip"));
            await ui.RunOnUIAsync(() =>
            {
                var view = FindControl<TabView>(surface!, "ConversationPanelTabs");
                Assert.Equal(5, view.TabItems.Count);
                Assert.Equal(5, panels.Tabs.Count);
                view.SelectedItem = view.TabItems[0];
                Assert.Equal("files", panels.Selected);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                theme?.Dispose();
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
