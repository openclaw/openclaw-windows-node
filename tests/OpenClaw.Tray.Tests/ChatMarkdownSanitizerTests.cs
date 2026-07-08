using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for <see cref="ChatMarkdownSanitizer"/>. Covers the chat
/// rubber-duck HIGH 1 (image fetches) + MEDIUM 3 (clickable links from
/// untrusted Markdown) findings.
/// </summary>
public class ChatMarkdownSanitizerTests
{
    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ChatMarkdownSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, ChatMarkdownSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_PlainText_PassesThroughUnchanged()
    {
        const string plain = "hello world\n\nsecond paragraph.";
        Assert.Equal(plain, ChatMarkdownSanitizer.Sanitize(plain));
    }

    [Fact]
    public void Sanitize_InlineImage_BecomesPlaceholder()
    {
        var input = "before ![tracking pixel](https://attacker.example/p.png) after";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("before [Image: tracking pixel] after", result);
        // Critically: no http URL leaks into the rendered output.
        Assert.DoesNotContain("attacker.example", result);
    }

    [Fact]
    public void Sanitize_InlineImageWithEmptyAlt_StillRendersPlaceholder()
    {
        var input = "x ![](https://attacker.example/p.png) y";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("x [Image] y", result);
        Assert.DoesNotContain("attacker.example", result);
    }

    [Fact]
    public void Sanitize_InlineLink_BecomesTextWithVisibleUrl()
    {
        var input = "Click [here](https://example.com/path) please.";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("Click here (https://example.com/path) please.", result);
    }

    [Fact]
    public void Sanitize_InlineLinkWithTitle_StripsTitle()
    {
        var input = "[t](https://e.com \"a title\")";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("t (https://e.com)", result);
    }

    [Fact]
    public void Sanitize_AngleBracketUrl_IsUnwrapped()
    {
        var input = "[t](<https://e.com/x>)";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("t (https://e.com/x)", result);
    }

    [Fact]
    public void Sanitize_ReferenceLinkDefinition_StripsDefinition()
    {
        var input = "Hello\n[ref]: https://attacker.example\nWorld";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("ref: https://attacker.example", result);
        // The original link-definition syntax must not survive — without
        // the bracket-colon prefix a markdown renderer can't resolve [x][ref].
        Assert.DoesNotContain("[ref]:", result);
    }

    [Theory]
    [InlineData(" [ref]: https://attacker.example")]
    [InlineData("  [ref]: https://attacker.example")]
    [InlineData("   [ref]: https://attacker.example")]
    public void Sanitize_IndentedReferenceLinkDefinition_StripsDefinition(string definition)
    {
        var result = ChatMarkdownSanitizer.Sanitize($"Hello\n{definition}\nWorld");

        Assert.Contains("ref: https://attacker.example", result);
        Assert.DoesNotContain("[ref]:", result);
    }

    [Fact]
    public void Sanitize_InlineCode_ProtectsContents()
    {
        var input = "Use `[click](http://x)` to render a link.";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        // Inline-code body is preserved verbatim — the brackets inside
        // backticks must not be re-interpreted as a link.
        Assert.Equal("Use `[click](http://x)` to render a link.", result);
    }

    [Fact]
    public void Sanitize_FencedCodeBlock_ProtectsContents()
    {
        var input = "before\n```js\nconst u = '[x](http://y)';\n```\nafter";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("[x](http://y)", result);
        Assert.StartsWith("before\n```js", result);
    }

    [Fact]
    public void Sanitize_TildeFencedCodeBlock_ProtectsContents()
    {
        var input = "~~~\n[x](http://y)\n~~~\n";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("[x](http://y)", result);
    }

    [Fact]
    public void Sanitize_MultipleLinks_AllConverted()
    {
        var input = "[a](http://1) and [b](http://2) and ![img](http://3)";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("a (http://1) and b (http://2) and [Image: img]", result);
    }

    [Fact]
    public void Sanitize_UnclosedLinkSyntax_LeftAlone()
    {
        // Intentionally malformed — no closing paren. Don't crash, don't
        // mangle adjacent text.
        var input = "see [text](http://no-close-paren";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_OversizedInlineImage_EscapesMarkdownOpening()
    {
        var input = "before ![" + new string('a', 1100) + "](https://attacker.example/p.png) after";
        var result = ChatMarkdownSanitizer.Sanitize(input);

        Assert.StartsWith("before \\!\\[", result);
        Assert.DoesNotContain("![", result);
    }

    [Fact]
    public void Sanitize_OversizedInlineLink_EscapesMarkdownOpening()
    {
        var input = "before [" + new string('a', 1100) + "](https://attacker.example/) after";
        var result = ChatMarkdownSanitizer.Sanitize(input);

        Assert.StartsWith("before \\[", result);
        Assert.DoesNotContain("before [", result);
    }

    [Fact]
    public void Sanitize_BracketsWithoutLink_LeftAlone()
    {
        var input = "use a [TODO] marker";
        Assert.Equal(input, ChatMarkdownSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_DoesNotMisinterpretImageAdjacentToLink()
    {
        var input = "![alt](http://img.example) plus [text](http://link.example)";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal("[Image: alt] plus text (http://link.example)", result);
    }

    [Fact]
    public void Sanitize_NormalAssistantProse_Preserved()
    {
        // Realistic assistant reply with lists, bold, code spans — none
        // of which contain URL syntax.
        var input = "**Steps**:\n\n1. Open `Settings.cs`\n2. Edit the value\n";
        Assert.Equal(input, ChatMarkdownSanitizer.Sanitize(input));
    }

    [Fact]
    public void SanitizeAndSplitStrongEmphasis_BoldText_BecomesStrongSegment()
    {
        var segments = ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis("For **Boston**: pack **rain gear**.");

        Assert.Collection(segments,
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal("For ", segment.Text);
            },
            segment =>
            {
                Assert.True(segment.IsStrong);
                Assert.Equal("Boston", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal(": pack ", segment.Text);
            },
            segment =>
            {
                Assert.True(segment.IsStrong);
                Assert.Equal("rain gear", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal(".", segment.Text);
            });
    }

    [Fact]
    public void SanitizeAndSplitStrongEmphasis_CodeSpansStayLiteral()
    {
        var segments = ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis("Use `**literal**` then **bold**.");

        Assert.Collection(segments,
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal("Use `**literal**` then ", segment.Text);
            },
            segment =>
            {
                Assert.True(segment.IsStrong);
                Assert.Equal("bold", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal(".", segment.Text);
            });
    }

    [Fact]
    public void SanitizeAndSplitStrongEmphasis_CodeSpanCanPrecedeClosingDelimiter()
    {
        var segments = ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis("Use **`code`** now.");

        Assert.Collection(segments,
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal("Use ", segment.Text);
            },
            segment =>
            {
                Assert.True(segment.IsStrong);
                Assert.Equal("`code`", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal(" now.", segment.Text);
            });
    }

    [Fact]
    public void SanitizeAndSplitStrongEmphasis_LinksRemainInertText()
    {
        var segments = ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis("Read **[docs](https://example.com)** now.");

        Assert.Collection(segments,
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal("Read ", segment.Text);
            },
            segment =>
            {
                Assert.True(segment.IsStrong);
                Assert.Equal("docs (https://example.com)", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsStrong);
                Assert.Equal(" now.", segment.Text);
            });
    }

    // ── chat rubber-duck round 2 LOW 3: code-block coverage gaps ──

    [Fact]
    public void Sanitize_IndentedCodeBlock_PreservesContent()
    {
        // 4-space-indented code block — link syntax inside MUST survive
        // verbatim (the rendered code block displays it as text).
        var input = "before\n\n    [x](http://y) inside indented block\n\nafter";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("    [x](http://y) inside indented block", result);
    }

    [Fact]
    public void Sanitize_TabIndentedCodeBlock_PreservesContent()
    {
        var input = "before\n\n\t[x](http://y) inside tab-indented\nafter";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("\t[x](http://y) inside tab-indented", result);
    }

    [Fact]
    public void Sanitize_FencedCodeBlockWithLeadingSpaces_PreservesContent()
    {
        // CommonMark allows up to 3 leading spaces before a fence; the
        // sanitizer must recognize this so link syntax inside the fence
        // is not mutated.
        var input = "before\n   ```js\nconst u = '[x](http://y)';\n   ```\nafter";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("[x](http://y)", result);
    }

    [Fact]
    public void Sanitize_IndentedCodeBlock_PreservesLinkSyntax()
    {
        // Variant of the above with a longer block — confirms multiple
        // indented lines are all passed through.
        var input = "    line1 [a](http://1)\n    line2 ![img](http://2)\n    line3 plain\n";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("[a](http://1)", result);
        Assert.Contains("![img](http://2)", result);
        Assert.DoesNotContain("[Image", result);
    }

    // ── chat rubber-duck round 2 MEDIUM 3: LinkBuilder hook safety ──
    //
    // These exercise the pure plain-text helper that the WinUI
    // ChatTimeline wires into MarkdownOptions.LinkBuilder. The
    // contract: no matter what shape the parser produces (autolink,
    // bare URL, full ``[text](url)`` after sanitization, nested image
    // inside a link), the LinkBuilder collapses it to inert text
    // without constructing a NavigateUri-bearing hyperlink.

    [Fact]
    public void FlattenLinkToInertText_BareUrl_ProducesUriOnly()
    {
        // md4c parses bare URLs as autolinks — the display text and
        // navigate URI are equal.
        const string url = "https://example.com/path";
        var text = ChatMarkdownSanitizer.FlattenLinkToInertText(url, url);
        Assert.Equal(url, text);
    }

    [Fact]
    public void FlattenLinkToInertText_AngleBracketAutolink_ProducesUriOnly()
    {
        // ``<https://example.com>`` parses to an autolink whose display
        // text is just the URL — must not double-render.
        const string url = "https://example.com";
        var text = ChatMarkdownSanitizer.FlattenLinkToInertText(url, url);
        Assert.Equal(url, text);
        Assert.DoesNotContain("(", text);
    }

    [Fact]
    public void FlattenLinkToInertText_DistinctDisplayAndUri_BothVisible()
    {
        var text = ChatMarkdownSanitizer.FlattenLinkToInertText("click here", "https://evil.example/");
        Assert.Equal("click here (https://evil.example/)", text);
    }

    [Fact]
    public void FlattenLinkToInertText_EmptyDisplay_FallsBackToUri()
    {
        var text = ChatMarkdownSanitizer.FlattenLinkToInertText("", "https://example.com");
        Assert.Equal("https://example.com", text);
    }

    [Fact]
    public void FlattenLinkToInertText_NullInputs_NeverThrow()
    {
        Assert.Equal(string.Empty, ChatMarkdownSanitizer.FlattenLinkToInertText(null, null));
        Assert.Equal("https://x", ChatMarkdownSanitizer.FlattenLinkToInertText(null, "https://x"));
        Assert.Equal("text", ChatMarkdownSanitizer.FlattenLinkToInertText("text", null));
    }

    [Fact]
    public void Sanitize_NestedImageInLink_BothFlattened()
    {
        // ``[![alt](http://img)](http://other)`` — outer link wraps an
        // image. Sanitizer must produce text+text with no clickable
        // link surviving and no image-fetch syntax remaining.
        var input = "[![alt](http://img)](http://other)";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.DoesNotContain("](http://img)", result);
        Assert.DoesNotContain("](http://other)", result);
        Assert.DoesNotContain("![alt]", result);
        // The outer link's destination must remain visible-but-inert.
        Assert.Contains("http://other", result);
    }

    [Fact]
    public void Sanitize_RawHtmlImg_TreatedAsText()
    {
        // Raw HTML is passed through as text for the timeline's explicit
        // inert HtmlBlock renderer. Sanitizer must not crash on HTML and
        // must not synthesize any [Image: ...] placeholder for raw HTML.
        var input = "<img src=\"https://attacker.example/p.png\" alt=\"x\">";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Equal(input, result);
        Assert.DoesNotContain("[Image", result);
    }

    [Theory]
    [InlineData("<script>fetch('https://attacker.example')</script>")]
    [InlineData("<a href=\"https://attacker.example\">click</a>")]
    [InlineData("<iframe src=\"https://attacker.example\"></iframe>")]
    [InlineData("<svg><image href=\"https://attacker.example/p.png\" /></svg>")]
    [InlineData("<img src=\"https://attacker.example/p.png\" onerror=\"alert(1)\">")]
    public void FlattenRawHtmlBlockToInertText_PreservesDangerousHtmlAsLiteralText(string html)
    {
        var result = ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(html);

        Assert.Equal(html, result);
    }

    [Fact]
    public void FlattenRawHtmlBlockToInertText_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(null));
    }
}
