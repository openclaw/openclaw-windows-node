using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorMarkdownListProofTests
{
    private const string LongText =
        "This list item contains enough words to wrap across several lines in a narrow chat bubble " +
        "while preserving the final words of the message.";

    private readonly UIThreadFixture _ui;

    public ReactorMarkdownListProofTests(UIThreadFixture ui) => _ui = ui;

    [Theory]
    [InlineData("- ", "\u2022 ")]
    [InlineData("7. ", "7. ")]
    public async Task ListContent_WrapsWithinRemainingWidthAndReflowsOnResize(string prefix, string marker)
    {
        await _ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        UIElement? root = null;
        double narrowHeight = 0;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl { Width = 240, VerticalAlignment = VerticalAlignment.Top };
                root = host.Reconciler.Mount(
                    ReactorChatTimeline.BuildSafeMarkdown(prefix + LongText), static () => { });
                host.Content = root;
                _ui.Container.Children.Add(host);
            });
            await _ui.YieldToRenderAsync();
            await _ui.RunOnUIAsync(() =>
            {
                _ui.Container.UpdateLayout();
                var row = Assert.Single(FindLogical<Grid>(root!), IsListRow);
                var markerBlock = Assert.IsType<TextBlock>(row.Children[0]);
                var content = Assert.IsType<RichTextBlock>(row.Children[1]);
                Assert.Equal(marker, markerBlock.Text);
                Assert.Equal(LongText, CollectText(content));
                Assert.Equal(TextWrapping.Wrap, content.TextWrapping);
                Assert.True(content.IsTextSelectionEnabled);
                Assert.Equal(1, Grid.GetColumn(content));
                Assert.InRange(content.ActualWidth, 1, 239);
                Assert.True(content.ActualWidth + markerBlock.ActualWidth + row.ColumnSpacing <= 241);
                narrowHeight = content.ActualHeight;
                Assert.True(narrowHeight > content.FontSize * 2);
            });
            await _ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(_ui.Container, "ReactorMarkdownListNarrow"));
            await _ui.RunOnUIAsync(() => { host!.Width = 600; });
            await _ui.YieldToRenderAsync();
            await _ui.RunOnUIAsync(() =>
            {
                _ui.Container.UpdateLayout();
                var row = Assert.Single(FindLogical<Grid>(root!), IsListRow);
                var content = Assert.IsType<RichTextBlock>(row.Children[1]);
                Assert.True(content.ActualHeight < narrowHeight);
                Assert.Equal(LongText, CollectText(content));
                Assert.InRange(content.ActualWidth, 241, 600);
            });
            await _ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(_ui.Container, "ReactorMarkdownListWide"));
        }
        finally
        {
            if (host is not null)
                await _ui.RunOnUIAsync(() =>
                {
                    _ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                });
            await _ui.RunOnUIAsync(() => themeScope?.Dispose());
        }
    }

    [Fact]
    public async Task NestedAndLooseLists_PreserveMarkersFormattingAndBlockContent()
    {
        await _ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl { Width = 280, VerticalAlignment = VerticalAlignment.Top };
                var root = host.Reconciler.Mount(ReactorChatTimeline.BuildSafeMarkdown(
                    "- **outer bold**\n\n  second paragraph\n\n  - nested item\n\n" +
                    "- final item\n\n  > quoted content"), static () => { });
                host.Content = root;
                _ui.Container.Children.Add(host);
            });
            await _ui.YieldToRenderAsync();
            await _ui.RunOnUIAsync(() =>
            {
                _ui.Container.UpdateLayout();
                var rows = FindLogical<Grid>(host!).Where(IsListRow).ToArray();
                Assert.Equal(3, rows.Length);
                Assert.All(rows, row =>
                {
                    Assert.Equal("\u2022 ", Assert.IsType<TextBlock>(row.Children[0]).Text);
                    Assert.Equal(1, Grid.GetColumn((FrameworkElement)row.Children[1]));
                    Assert.InRange(((FrameworkElement)row.Children[1]).ActualWidth, 1, 279);
                });
                var texts = FindLogical<RichTextBlock>(host!).ToArray();
                Assert.Contains(texts, text => CollectText(text) == "second paragraph");
                Assert.Contains(texts, text => CollectText(text) == "nested item");
                Assert.Contains(texts, text => CollectText(text) == "quoted content");
                var bold = Assert.Single(texts, text => CollectText(text) == "outer bold");
                Assert.Contains(GetRuns(bold), run => run.FontWeight.Weight > 400);
            });
            await _ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(_ui.Container, "ReactorMarkdownListNested"));
        }
        finally
        {
            if (host is not null)
                await _ui.RunOnUIAsync(() =>
                {
                    _ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                });
            await _ui.RunOnUIAsync(() => themeScope?.Dispose());
        }
    }

    private static bool IsListRow(Grid grid) =>
        grid.ColumnDefinitions.Count == 2 &&
        grid.ColumnDefinitions[0].Width.IsAuto &&
        grid.ColumnDefinitions[1].Width.IsStar &&
        grid.Children.Count == 2 &&
        grid.Children[0] is TextBlock;

    private static IEnumerable<Run> GetRuns(RichTextBlock text) =>
        text.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>());

    private static string CollectText(RichTextBlock text) =>
        string.Concat(GetRuns(text).Select(run => run.Text));
}
