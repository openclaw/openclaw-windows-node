using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Reasoning_UsesCompactSecondaryRampForMarkdownListsAndInlineCode(string theme)
    {
        const string threadId = "proof";
        const string thinking =
            "## Compact heading\n\nThinking paragraph.\n\n- First item\n- Second item\n\n`inline code`";
        var previousTheme = Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME");
        Environment.SetEnvironmentVariable("OPENCLAW_PROOF_THEME", theme);
        try
        {
            var provider = new ProofProvider();
            var snapshot = await provider.LoadAsync();
            snapshot = snapshot with
            {
                Threads = [snapshot.Threads[0] with { ReasoningLevel = "on" }],
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    [threadId] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = ImmutableList.Create(
                            new ChatTimelineItem("reasoning", ChatTimelineItemKind.Reasoning, thinking)),
                    },
                },
                CurrentHistoryThreads = new HashSet<string> { threadId },
            };
            provider.Publish(snapshot);
            OpenClawReactorChatRoot.SetThinkingVisible(true);

            await WithChatAsync(800, async (surface, _, _, _) =>
            {
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => FindDescendants<RichTextBlock>(surface).Any(text =>
                        CollectText(text).Contains("Thinking paragraph.", StringComparison.Ordinal)),
                    "compact reasoning Markdown"));
                await SettleAsync();
                await CaptureAsync(surface, "ReasoningReadingRamp");
                await ui.RunOnUIAsync(() =>
                {
                    var reasoning = Assert.Single(FindDescendants<RichTextBlock>(surface),
                        text => CollectText(text).Contains("Thinking paragraph.", StringComparison.Ordinal));
                    Assert.Equal(12, reasoning.FontSize);
                    Assert.Equal(18, reasoning.LineHeight);
                    Assert.Same(Application.Current.Resources["ChatSecondaryRichTextStyle"], reasoning.Style);

                    var markdownRoot = Assert.IsType<Border>(Ancestors(reasoning).OfType<Border>().First());
                    var markdownBlocks = FindDescendants<RichTextBlock>(markdownRoot)
                        .Where(text => !string.Equals(
                            text.FontFamily?.Source, "Consolas", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    Assert.Contains(markdownBlocks, text =>
                        CollectText(text).Contains("Compact heading", StringComparison.Ordinal));
                    Assert.Contains(markdownBlocks, text =>
                        CollectText(text).Contains("First item", StringComparison.Ordinal));
                    Assert.Contains(markdownBlocks, text =>
                        CollectText(text).Contains("Second item", StringComparison.Ordinal));
                    Assert.All(markdownBlocks, text =>
                    {
                        Assert.Equal(12, text.FontSize);
                        Assert.Equal(18, text.LineHeight);
                        Assert.Same(Application.Current.Resources["ChatSecondaryRichTextStyle"], text.Style);
                    });

                    var listMarkers = FindDescendants<TextBlock>(markdownRoot)
                        .Where(text => !string.IsNullOrWhiteSpace(text.Text))
                        .Where(text => !string.Equals(
                            text.FontFamily?.Source, "Consolas", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    Assert.All(listMarkers, text =>
                    {
                        Assert.Equal(12, text.FontSize);
                        Assert.Equal(18, text.LineHeight);
                        Assert.Same(Application.Current.Resources["ChatSecondaryTextStyle"], text.Style);
                    });
                });
            }, initialThreadId: threadId, renderProvider: provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_PROOF_THEME", previousTheme);
        }
    }
}
