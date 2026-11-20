using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Mounted WinUI proof for the production assistant Markdown renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorMarkdownTableProofTests
{
    private const string Markdown =
        "Metric | Value\n" +
        "---|---\n" +
        "Price | $205.27\n" +
        "Forward P/E | 13.40x (cheapest)\n" +
        "PEG Ratio | 0.79\n" +
        "Dividend Yield | 3.47%\n" +
        "Beta | 0.49 (lowest volatility)\n\n" +
        "```text\n" +
        "literal | pipe\n" +
        "---|---\n" +
        "```\n";

    private readonly UIThreadFixture _ui;

    public ReactorMarkdownTableProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task AssistantMarkdown_RendersTableAndLiteralFenceAtNarrowWidth()
    {
        await _ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Border? proofSurface = null;
        UIElement? root = null;
        Element? element = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl
                {
                    Width = 320,
                    RequestedTheme = ElementTheme.Light,
                };
                element = ReactorChatTimeline.BuildSafeMarkdown(Markdown);
                root = host.Reconciler.Mount(element, static () => { });
                host.Content = root;
                proofSurface = new Border
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Child = host,
                    Padding = new Thickness(16),
                    RequestedTheme = ElementTheme.Light,
                    VerticalAlignment = VerticalAlignment.Top,
                    Width = 352,
                };
                _ui.Container.Children.Add(proofSurface);
            });
            await _ui.YieldToRenderAsync();

            await _ui.RunOnUIAsync(() =>
            {
                // A dispatcher yield is not a layout barrier; capture is optional in CI.
                _ui.Container.UpdateLayout();
                var table = Assert.Single(
                    FindDescendants<Grid>(root!),
                    grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 6);
                var cells = table.Children.SelectMany(FindDescendants<RichTextBlock>).ToArray();

                Assert.Equal(12, cells.Length);
                Assert.Equal(ChatVisuals.TableRadius, Assert.IsType<Border>(VisualTreeHelper.GetParent(table)).CornerRadius.TopLeft);
                Assert.All(cells, cell => Assert.True(cell.IsTextSelectionEnabled));
                Assert.All(cells, cell => Assert.Equal(TextWrapping.Wrap, cell.TextWrapping));
                Assert.All(cells, cell => Assert.InRange(cell.ActualWidth, 1, 160));
                Assert.True(GetRuns(cells[0]).All(run => run.FontWeight.Weight > 400));
                Assert.True(GetRuns(cells[1]).All(run => run.FontWeight.Weight > 400));
                Assert.True(GetRuns(cells[2]).All(run => run.FontWeight.Weight <= 400));
                Assert.True(GetRuns(cells[3]).All(run => run.FontWeight.Weight <= 400));
                Assert.Equal(
                    [
                        "Metric",
                        "Value",
                        "Price",
                        "$205.27",
                        "Forward P/E",
                        "13.40x (cheapest)",
                        "PEG Ratio",
                        "0.79",
                        "Dividend Yield",
                        "3.47%",
                        "Beta",
                        "0.49 (lowest volatility)",
                    ],
                    cells.Select(CollectText).ToArray());

                var code = Assert.Single(
                    FindDescendants<RichTextBlock>(root!),
                    text => CollectText(text).Contains("literal | pipe", StringComparison.Ordinal));
                Assert.Contains("---|---", CollectText(code), StringComparison.Ordinal);
                Assert.Single(FindDescendants<Border>(root!),
                    border => border.CornerRadius.TopLeft == ChatVisuals.TableRadius);
                Assert.Equal(13, code.FontSize);
                Assert.Equal(18, code.LineHeight);
                Assert.Single(
                    FindDescendants<Grid>(root!),
                    grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 6);
                Assert.InRange(host!.ActualWidth, 1, 320);
            });
            await _ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(proofSurface!, "ReactorMarkdownTable"));
            await _ui.PauseAsync("GFM table and literal pipe fence at 320 px");
        }
        finally
        {
            if (host is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    _ui.Container.Children.Remove(proofSurface);
                    proofSurface!.Child = null;
                    host.Content = null;
                    host.Dispose();
                    themeScope?.Dispose();
                });
            }
        }
    }

    [Fact]
    public async Task AssistantMarkdown_StylesTablesNestedInListsAndQuotes()
    {
        const string markdown =
            "- List table\n\n" +
            "  Metric | Value\n" +
            "  ---|---\n" +
            "  nested-list | 1\n\n" +
            "- Metric | Value\n" +
            "  ---|---\n" +
            "  table-only | This long cell value must wrap within the list content column.\n\n" +
            "> Quote table\n>\n" +
            "> Metric | Value\n" +
            "> ---|---\n" +
            "> nested-quote | 2\n";

        await _ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UIElement? root = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl
                {
                    Width = 320,
                    RequestedTheme = ElementTheme.Light,
                };
                var element = ReactorChatTimeline.BuildSafeMarkdown(markdown);
                root = host.Reconciler.Mount(element, static () => { });
                host.Content = root;
                _ui.Container.Children.Add(host);
            });
            await _ui.YieldToRenderAsync();

            await _ui.RunOnUIAsync(() =>
            {
                _ui.Container.UpdateLayout();
                var tables = FindDescendants<Grid>(root!)
                    .Where(grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 2)
                    .ToArray();

                var grids = string.Join("; ", FindDescendants<Grid>(root!).Select(grid =>
                    $"{grid.RowDefinitions.Count}x{grid.ColumnDefinitions.Count}: " +
                    string.Join("|", grid.Children.SelectMany(FindDescendants<RichTextBlock>).Select(CollectText))));
                Assert.True(tables.Length == 3, $"Expected 3 nested tables, found {tables.Length}. Grids: {grids}");
                var tableTexts = tables.Select(table => string.Join("|", table.Children
                    .SelectMany(FindDescendants<RichTextBlock>)
                    .Select(CollectText))).ToArray();
                Assert.Contains(tableTexts, text => text.Contains("nested-list", StringComparison.Ordinal));
                Assert.Contains(tableTexts, text => text.Contains("nested-quote", StringComparison.Ordinal));
                var tableOnly = Assert.Single(tables, table => table.Children
                    .SelectMany(FindDescendants<RichTextBlock>)
                    .Select(CollectText)
                    .Any(text => text.Contains("table-only", StringComparison.Ordinal)));
                var tableOnlyFrame = Assert.IsType<Border>(VisualTreeHelper.GetParent(tableOnly));
                Assert.Equal(1, Grid.GetColumn(tableOnlyFrame));
                Assert.InRange(tableOnlyFrame.ActualWidth, 1, host!.Width);
                var tableOnlyCells = tableOnly.Children.SelectMany(FindDescendants<RichTextBlock>).ToArray();
                Assert.All(tableOnlyCells, cell => Assert.Equal(TextWrapping.Wrap, cell.TextWrapping));
                foreach (var table in tables)
                {
                    var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(table));
                    Assert.Equal(ChatVisuals.TableRadius, frame.CornerRadius.TopLeft);
                }
            });
        }
        finally
        {
            if (host is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    _ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                    themeScope?.Dispose();
                });
            }
        }
    }

    private static string CollectText(RichTextBlock richTextBlock)
    {
        var text = new System.Text.StringBuilder();
        foreach (var run in GetRuns(richTextBlock))
            text.Append(run.Text);
        return text.ToString();
    }

    private static IEnumerable<Run> GetRuns(RichTextBlock richTextBlock) =>
        richTextBlock.Blocks
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>());
}
