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
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
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
            await _ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(proofSurface!, "ReactorMarkdownTable"));
            await _ui.PauseAsync("GFM table and literal pipe fence at 320 px");

            await _ui.RunOnUIAsync(() =>
            {
                var table = Assert.Single(
                    FindLogical<Grid>(root!),
                    grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 6);
                var cells = table.Children.OfType<RichTextBlock>().ToArray();

                Assert.Equal(12, cells.Length);
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
                    FindLogical<RichTextBlock>(root!),
                    text => CollectText(text).Contains("literal | pipe", StringComparison.Ordinal));
                Assert.Contains("---|---", CollectText(code), StringComparison.Ordinal);
                Assert.Single(
                    FindLogical<Grid>(root!),
                    grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 6);
                Assert.InRange(host!.ActualWidth, 1, 320);
            });
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
