using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatMarkdownDisclosureTests
{
    [Theory]
    [InlineData("<details><summary>Sources and evidence</summary>")]
    [InlineData("<details>\n<summary>Sources and evidence</summary>")]
    [InlineData("<DETAILS>\r\n<SUMMARY>Sources and evidence</SUMMARY>")]
    public void CommonWrappers_KeepBodyMarkdownAndSurroundingOrder(string header)
    {
        var parts = ChatMarkdownDisclosures.Parse(
            $"Before\n\n{header}\n\nPaths:\n\n- `app\\dist\\plugin.json`\n- **purpose**\n\n</details>\n\nAfter");
        Assert.Collection(parts,
            part => Assert.Equal("Before", Assert.IsType<ChatMarkdownText>(part).Text),
            part =>
            {
                var disclosure = Assert.IsType<ChatMarkdownDisclosure>(part);
                Assert.Equal("Sources and evidence", disclosure.Summary);
                Assert.False(disclosure.InitiallyExpanded);
                Assert.Equal("Paths:\n\n- `app\\dist\\plugin.json`\n- **purpose**",
                    Assert.IsType<ChatMarkdownText>(Assert.Single(disclosure.Body)).Text);
            },
            part => Assert.Equal("After", Assert.IsType<ChatMarkdownText>(part).Text));
    }

    [Fact]
    public void NestedOpenDisclosure_PreservesCodeAndMarkdownBlocks()
    {
        var parts = ChatMarkdownDisclosures.Parse(
            "<details open>\n<summary>Outer</summary>\nParagraph\n\n" +
            "<details><summary>Inner</summary>\n```html\n</details>\n```\n\n" +
            "| Key | Value |\n| --- | --- |\n| A | B |\n</details>\n\nEnd\n</details>");
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(parts));
        Assert.True(outer.InitiallyExpanded);
        Assert.Collection(outer.Body,
            part => Assert.Equal("Paragraph", Assert.IsType<ChatMarkdownText>(part).Text),
            part =>
            {
                var inner = Assert.IsType<ChatMarkdownDisclosure>(part);
                Assert.Equal("Inner", inner.Summary);
                Assert.Contains("```html\n</details>\n```",
                    Assert.IsType<ChatMarkdownText>(Assert.Single(inner.Body)).Text);
            },
            part => Assert.Equal("End", Assert.IsType<ChatMarkdownText>(part).Text));
    }

    [Theory]
    [InlineData("```html\n<details><summary>Example</summary>\nBody\n</details>\n```")]
    [InlineData("<details onclick=\"evil()\"><summary>Example</summary>\nBody\n</details>")]
    [InlineData("<details><summary></summary>\nBody\n</details>")]
    [InlineData("<details>\n<summary>Outer</summary>\n<details><summary>Inner</summary>\nBody\n</details>")]
    public void UnsupportedCodeOrIncompleteWrappers_RemainLiteral(string text)
    {
        Assert.Equal(text, Assert.IsType<ChatMarkdownText>(
            Assert.Single(ChatMarkdownDisclosures.Parse(text))).Text);
    }

    [Fact]
    public void UnsupportedNestedWrapper_DoesNotCloseItsParentOrActivateItsChildren()
    {
        var input = "<details><summary>Outer</summary>\n\n" +
            "<details onclick=\"evil()\"><summary>Unsupported</summary>\n\n" +
            "<details><summary>Opaque inner</summary>\n\nInner body\n\n</details>\n\n" +
            "</details>\n\nFinal evidence\n\n</details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(
            Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        var body = Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text;
        Assert.Contains("<details onclick=\"evil()\">", body);
        Assert.EndsWith("Final evidence", body);
    }

    [Theory]
    [InlineData("<details><summary>S</summary>\n> quoted\n</details>", "> quoted")]
    [InlineData("  <details><summary>S</summary>\n\n  - item\n  </details>", "- item")]
    public void ClosingWrapperAfterQuoteOrListBody_ClosesTheOuterDisclosure(string input, string expectedBody)
    {
        var disclosure = Assert.IsType<ChatMarkdownDisclosure>(
            Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("S", disclosure.Summary);
        Assert.Equal(expectedBody, Assert.IsType<ChatMarkdownText>(Assert.Single(disclosure.Body)).Text.Trim());
    }

    [Fact]
    public void IndentedOuterDisclosure_IgnoresListLocalWrapperPair()
    {
        var input = "  <details><summary>Outer</summary>\n\n- item\n  <details><summary>In list</summary>\n  </details>\n\nTail\n  </details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("Outer", outer.Summary);
        var body = Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text;
        Assert.Contains("<details><summary>In list</summary>", body);
        Assert.EndsWith("Tail", body);
    }

    [Fact]
    public void IndentedOuterDisclosure_IgnoresSameLinePairInListContent()
    {
        var input = "  <details><summary>Outer</summary>\n\n- item\n  <details><summary>Literal</summary>text</details>\n  </details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("Outer", outer.Summary);
        Assert.Contains("<details><summary>Literal</summary>text</details>",
            Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text);
    }

    [Fact]
    public void IndentedCodeBlockSummary_IsNotAHeader()
    {
        var input = "<details>\n\n    <summary>Literal code</summary>\n\nBody\n\n</details>";
        Assert.Equal(input, Assert.IsType<ChatMarkdownText>(
            Assert.Single(ChatMarkdownDisclosures.Parse(input))).Text);
    }

    [Theory]
    [InlineData("- <details><summary>In list</summary>\n  </details>")]
    [InlineData("-\t<details><summary>In list</summary>\n  </details>")]
    [InlineData("1.\t<details><summary>In list</summary>\n  </details>")]
    [InlineData(">\t<details><summary>Quoted</summary>\n  </details>")]
    public void IndentedOuterDisclosure_IgnoresWrapperPairAfterContainerMarker(string pair)
    {
        var input = $"  <details><summary>Outer</summary>\n\n{pair}\n\nTail\n  </details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("Outer", outer.Summary);
        Assert.EndsWith("Tail", Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text);
    }

    [Fact]
    public void LiteralOneLineWrapperPair_DoesNotBlockALaterValidDisclosure()
    {
        var input = "<details><summary>Literal</summary>text</details>\n\n<details><summary>Real</summary>\n\nBody\n\n</details>";
        Assert.Collection(ChatMarkdownDisclosures.Parse(input),
            part => Assert.Equal("<details><summary>Literal</summary>text</details>",
                Assert.IsType<ChatMarkdownText>(part).Text),
            part =>
            {
                var real = Assert.IsType<ChatMarkdownDisclosure>(part);
                Assert.Equal("Real", real.Summary);
                Assert.Equal("Body", Assert.IsType<ChatMarkdownText>(Assert.Single(real.Body)).Text);
            });
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(5000)]
    public void SiblingDisclosures_InspectAtMostOneTokenPerWrapper(int count)
    {
        var input = string.Concat(Enumerable.Range(0, count).Select(index =>
            $"<details><summary>{index}</summary>\n{index}\n</details>\n\n"));
        Assert.True(input.Length <= OpenClaw.Shared.Markdown.ChatMarkdownAstBuilder.MaxInputBytes);

        var parts = ChatMarkdownDisclosures.Parse(input, out var partTokenVisits);

        Assert.Equal(count, parts.Count);
        for (var index = 0; index < count; index++)
        {
            var disclosure = Assert.IsType<ChatMarkdownDisclosure>(parts[index]);
            Assert.Equal(index.ToString(), disclosure.Summary);
            Assert.False(disclosure.InitiallyExpanded);
            Assert.Equal(index.ToString(), Assert.IsType<ChatMarkdownText>(
                Assert.Single(disclosure.Body)).Text);
        }
        // Each sibling has two wrapper tokens. Growing breadth must not rescan
        // unrelated siblings while building a body, even under the input cap.
        Assert.InRange(partTokenVisits, count, 2 * count);
    }

    [Fact]
    public void UnterminatedSummaries_SearchOnlyTheSummaryWindow()
    {
        const int count = 10_000;
        var input = string.Concat(Enumerable.Repeat("<details><summary>x\n", count));
        Assert.True(input.Length <= OpenClaw.Shared.Markdown.ChatMarkdownAstBuilder.MaxInputBytes);

        var parts = ChatMarkdownDisclosures.Parse(input, out _, out var summarySearchChars);

        Assert.Equal(input, Assert.IsType<ChatMarkdownText>(Assert.Single(parts)).Text);
        // Each opener may search at most its own summary window, never the rest of the message.
        Assert.InRange(summarySearchChars, 1, (long)count * (4096 + "</summary>".Length));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void SummaryLengthLimit_IsUnchangedByTheBoundedSearch(int length, bool disclosure)
    {
        var summary = new string('s', length);
        var input = $"<details><summary>{summary}</summary>\n\nBody\n\n</details>";

        var part = Assert.Single(ChatMarkdownDisclosures.Parse(input));

        if (disclosure)
            Assert.Equal(summary, Assert.IsType<ChatMarkdownDisclosure>(part).Summary);
        else
            Assert.Equal(input, Assert.IsType<ChatMarkdownText>(part).Text);
    }

    [Fact]
    public void DisclosureDepth_IsBoundedWithoutDiscardingDeepContent()
    {
        var input = string.Concat(Enumerable.Repeat("<details><summary>Nested</summary>\n\n", 20)) +
            "Deep evidence\n\n" + string.Concat(Enumerable.Repeat("</details>\n\n", 20));
        var parts = ChatMarkdownDisclosures.Parse(input);
        var depth = 0;
        while (parts is [ChatMarkdownDisclosure disclosure])
        {
            depth++;
            parts = disclosure.Body;
        }
        Assert.Equal(16, depth);
        var fallback = Assert.IsType<ChatMarkdownText>(Assert.Single(parts)).Text;
        Assert.Contains("<details>", fallback);
        Assert.Contains("Deep evidence", fallback);
    }
}
