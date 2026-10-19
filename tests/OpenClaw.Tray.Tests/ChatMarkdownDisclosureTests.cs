using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatMarkdownDisclosureTests
{
    [Theory]
    [InlineData("<details><summary>Sources and evidence</summary>")]
    [InlineData("<details> <summary>Sources and evidence</summary>")]
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
    [InlineData("~~~html\n<details><summary>Example</summary>\nBody\n</details>\n~~~")]
    [InlineData("    <details><summary>Example</summary>\n    Body\n    </details>")]
    [InlineData("`<details><summary>Example</summary>`\nBody\n</details>")]
    [InlineData("``\n<details><summary>Example</summary>\nBody\n</details>\n``")]
    [InlineData("- ```html\n  <details><summary>Example</summary>\n  Body\n  </details>\n  ```")]
    [InlineData("> <details><summary>Example</summary>\n> Body\n> </details>")]
    [InlineData("<details onclick=\"evil()\"><summary>Example</summary>\nBody\n</details>")]
    [InlineData("<details><summary>Example</summary>\nBody")]
    [InlineData("<details><summary></summary>\nBody\n</details>")]
    [InlineData("<details>\n<summary>Outer</summary>\n<details><summary>Inner</summary>\nBody\n</details>")]
    public void UnsupportedCodeOrIncompleteWrappers_RemainLiteral(string text)
    {
        Assert.Equal(text, Assert.IsType<ChatMarkdownText>(
            Assert.Single(ChatMarkdownDisclosures.Parse(text))).Text);
    }

    [Fact]
    public void BodySanitization_DoesNotReintroduceRemoteImagesOrActiveLinks()
    {
        var input = "<details><summary>Sources</summary>\n" +
            "![tracking](https://example.invalid/pixel) [source](https://example.invalid)\n</details>";
        var parts = ChatMarkdownDisclosures.Parse(ChatMarkdownSanitizer.Sanitize(input));
        var body = Assert.IsType<ChatMarkdownText>(
            Assert.Single(Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(parts)).Body)).Text;
        Assert.Equal("[Image: tracking] source (https://example.invalid)", body);
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
    [InlineData("<details><summary>S</summary>\n\n> quoted\n</details>", "> quoted")]
    [InlineData("  <details><summary>S</summary>\n\n  - item\n  </details>", "- item")]
    [InlineData("  <details><summary>S</summary>\n\n  - item\n\n  </details>", "- item")]
    [InlineData("<details><summary>S</summary>\n\n- item\n</details>", "- item")]
    public void ClosingWrapperAfterQuoteOrListBody_ClosesTheOuterDisclosure(string input, string expectedBody)
    {
        var disclosure = Assert.IsType<ChatMarkdownDisclosure>(
            Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("S", disclosure.Summary);
        Assert.Equal(expectedBody, Assert.IsType<ChatMarkdownText>(Assert.Single(disclosure.Body)).Text.Trim());
    }

    [Fact]
    public void IndentedWrapperInsideListItem_DoesNotCloseOrOpenTheOuterDisclosure()
    {
        var input = "<details><summary>Outer</summary>\n\n- item\n  <details><summary>In list</summary>\n  </details>\n\nTail\n</details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("Outer", outer.Summary);
        var body = Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text;
        Assert.Contains("<details><summary>In list</summary>", body);
        Assert.EndsWith("Tail", body);
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
    public void IndentedOuterDisclosure_IgnoresWrapperPairAfterListMarker()
    {
        var input = "  <details><summary>Outer</summary>\n\n- <details><summary>In list</summary>\n  </details>\n\nTail\n  </details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Equal("Outer", outer.Summary);
        var body = Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text;
        Assert.Contains("- <details><summary>In list</summary>", body);
        Assert.EndsWith("Tail", body);
    }

    [Fact]
    public void IndentedOuterDisclosure_IgnoresWrapperPairInQuote()
    {
        var input = "  <details><summary>Outer</summary>\n\n> <details><summary>Quoted</summary>\n  </details>\n\nTail\n  </details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.EndsWith("Tail", Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text);
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

    [Fact]
    public void ProseMentioningDetailsTag_DoesNotSwallowTheRealClosingWrapper()
    {
        var input = "<details><summary>S</summary>\n\nUse a <details> tag for folds, and </details> to end it.\n\nBody\n\n</details>";
        var disclosure = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.Contains("Use a <details> tag", Assert.IsType<ChatMarkdownText>(Assert.Single(disclosure.Body)).Text);
        var unbalanced = "<details><summary>S</summary>\n\nUse a <details> tag.\n\n</details>";
        var second = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(unbalanced)));
        Assert.Contains("Use a <details> tag.", Assert.IsType<ChatMarkdownText>(Assert.Single(second.Body)).Text);
    }

    [Theory]
    [InlineData("-\t<details><summary>In list</summary>\n  </details>")]
    [InlineData("1.\t<details><summary>In list</summary>\n  </details>")]
    [InlineData(">\t<details><summary>Quoted</summary>\n  </details>")]
    public void IndentedOuterDisclosure_IgnoresWrapperPairAfterTabSeparatedMarker(string pair)
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

    [Fact]
    public void TopLevelClosingWrapper_EndsOuterDisclosureAfterDanglingListLocalOpener()
    {
        var input = "<details><summary>Outer</summary>\n\n- item\n  <details><summary>Dangling</summary>\n\nTail\n</details>";
        var outer = Assert.IsType<ChatMarkdownDisclosure>(Assert.Single(ChatMarkdownDisclosures.Parse(input)));
        Assert.EndsWith("Tail", Assert.IsType<ChatMarkdownText>(Assert.Single(outer.Body)).Text);
    }

    [Theory]
    [InlineData("- item\n  <details><summary>In list</summary>\n  Body\n  </details>")]
    [InlineData("> quoted\n<details><summary>After quote text</summary>\nBody\n</details>")]
    public void WrappersThatOnlyAppearInsideListOrQuoteText_RemainLiteral(string text)
    {
        Assert.Equal(text, Assert.IsType<ChatMarkdownText>(
            Assert.Single(ChatMarkdownDisclosures.Parse(text))).Text);
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
