using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.FunctionalUI.Hosting;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatTimelineThemeResourceProofTests
{
    private readonly UIThreadFixture _ui;

    public ChatTimelineThemeResourceProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ToolBurst_RendersWithTestHostBorderStyle()
    {
        var props = BuildProps(
        [
            new ChatTimelineItem("user-1", ChatTimelineItemKind.User, "Check status"),
            new ChatTimelineItem("assistant-1", ChatTimelineItemKind.Assistant, "Checking."),
            new ChatTimelineItem(
                "tool-1",
                ChatTimelineItemKind.ToolCall,
                "{}",
                ToolName: "session_status",
                ToolResult: ChatToolCallStatus.Success,
                ToolOutput: "ok"),
        ]);

        var host = await MountAsync(props);

        await _ui.RunOnUIAsync(() =>
        {
            var style = Assert.IsType<Style>(
                Application.Current.Resources["ChatToolCardBorderStyle"]);
            Assert.Equal(typeof(Border), style.TargetType);
            Assert.Contains(
                FindDescendants<Border>(host),
                border => ReferenceEquals(border.Style, style));
            host.Dispose();
        });
    }

    [Fact]
    public async Task CompactionEntry_RendersWithTestHostBorderStyle()
    {
        const string entryId = "compaction-1";
        var metadata = new Dictionary<string, ChatEntryMetadata>(StringComparer.Ordinal)
        {
            [entryId] = new(
                Timestamp: null,
                Model: null,
                OpenClawKind: "compaction",
                CompactionTokensBefore: 42_000,
                CompactionTokensAfter: 12_000),
        };
        var props = BuildProps(
            [new ChatTimelineItem(entryId, ChatTimelineItemKind.Status, "Context compacted")],
            metadata);

        var host = await MountAsync(props);

        await _ui.RunOnUIAsync(() =>
        {
            var style = Assert.IsType<Style>(
                Application.Current.Resources["ChatCompactionCardStyle"]);
            Assert.Equal(typeof(Border), style.TargetType);
            Assert.Contains(
                FindDescendants<Border>(host),
                border => ReferenceEquals(border.Style, style));
            host.Dispose();
        });
    }

    private async Task<FunctionalHostControl> MountAsync(OpenClawChatTimelineProps props)
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            host = new FunctionalHostControl
            {
                Width = 860,
                Height = 560,
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        for (var pass = 0; pass < 4; pass++)
        {
            await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
            await _ui.YieldToRenderAsync();
            await Task.Delay(40);
        }

        return host!;
    }

    private static OpenClawChatTimelineProps BuildProps(
        IReadOnlyList<ChatTimelineItem> entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata = null) =>
        new(
            SessionId: "theme-resource-proof",
            Entries: entries,
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            EntryMetadata: metadata,
            ShowToolCalls: true);
}
