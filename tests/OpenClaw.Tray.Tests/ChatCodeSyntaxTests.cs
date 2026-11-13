using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatCodeSyntaxTests
{
    private static (string Text, ChatCodeRole Role)[] Roles(string code, string? language) =>
        ChatCodeSyntax.Classify(code, language)
            .Select(span => (code.Substring(span.Start, span.Length), span.Role))
            .ToArray();

    [Fact]
    public void TypeScript_TypeLiteral_MatchesWebHighlightRoles()
    {
        var roles = Roles("type SharedFolder = { path: string; access: \"read\" | \"readwrite\" };", "ts");

        Assert.Equal(
            [
                ("type", ChatCodeRole.Keyword),
                ("SharedFolder", ChatCodeRole.Title),
                ("path", ChatCodeRole.Attribute),
                ("string", ChatCodeRole.Type),
                ("access", ChatCodeRole.Attribute),
                ("\"read\"", ChatCodeRole.String),
                ("\"readwrite\"", ChatCodeRole.String),
            ],
            roles);
    }

    [Fact]
    public void TypeScript_FunctionsAndCalls_AreTitlesButMembersAndParametersAreNot()
    {
        var roles = Roles(
            "function toGatewayEntry(folders: SharedFolder[]) {\n  return { ask: \"off\", paths: folders.map(f => glob(f.path)) };\n}",
            "typescript");

        Assert.Contains(("function", ChatCodeRole.Keyword), roles);
        Assert.Contains(("toGatewayEntry", ChatCodeRole.Title), roles);
        Assert.Contains(("return", ChatCodeRole.Keyword), roles);
        Assert.Contains(("ask", ChatCodeRole.Attribute), roles);
        Assert.Contains(("paths", ChatCodeRole.Attribute), roles);
        Assert.Contains(("map", ChatCodeRole.Title), roles);
        Assert.Contains(("glob", ChatCodeRole.Title), roles);
        Assert.DoesNotContain(roles, role => role.Text is "folders" or "path" or "f");
    }

    [Fact]
    public void Json_KeysLiteralsAndNumbers_UseDistinctRoles()
    {
        Assert.Equal(
            [
                ("\"a\"", ChatCodeRole.Attribute),
                ("1.5", ChatCodeRole.Number),
                ("\"b\"", ChatCodeRole.Attribute),
                ("true", ChatCodeRole.Number),
                ("\"c\"", ChatCodeRole.Attribute),
                ("\"x\"", ChatCodeRole.String),
            ],
            Roles("{\"a\": 1.5, \"b\": true, \"c\": \"x\"}", "json"));
    }

    [Fact]
    public void Python_CommentsDefinitionsAndPrefixedStrings_AreColored()
    {
        var roles = Roles("def greet(name):  # say hi\n    return f'hi {name}' if name else None", "py");

        Assert.Contains(("def", ChatCodeRole.Keyword), roles);
        Assert.Contains(("greet", ChatCodeRole.Title), roles);
        Assert.Contains(("# say hi", ChatCodeRole.Comment), roles);
        Assert.Contains(("'hi {name}'", ChatCodeRole.String), roles);
        Assert.Contains(("None", ChatCodeRole.Number), roles);
    }

    [Fact]
    public void PowerShell_CmdletsVariablesAndComments_AreColored()
    {
        Assert.Equal(
            [
                ("Get-ChildItem", ChatCodeRole.Type),
                ("$env:TEMP", ChatCodeRole.Number),
                ("# list", ChatCodeRole.Comment),
            ],
            Roles("Get-ChildItem -Path $env:TEMP # list", "pwsh"));
    }

    [Fact]
    public void Shell_HashInsideWordIsNotAComment()
    {
        var roles = Roles("echo \"$HOME\" a#b # done", "bash");

        Assert.Contains(("echo", ChatCodeRole.Type), roles);
        Assert.Contains(("\"$HOME\"", ChatCodeRole.String), roles);
        Assert.Contains(("# done", ChatCodeRole.Comment), roles);
        Assert.DoesNotContain(roles, role => role.Text.StartsWith("#b", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("note: it's fine", "yaml")]
    [InlineData("fn first<'a>(value: &'a str) -> &'a str { value }", "rust")]
    public void Apostrophes_ThatAreNotStringDelimiters_DoNotOpenStrings(string code, string language)
    {
        Assert.DoesNotContain(Roles(code, language), role => role.Role == ChatCodeRole.String);
    }

    [Fact]
    public void Markup_TagNamesAttributesAndValues_AreColored()
    {
        Assert.Equal(
            [
                ("<!-- c -->", ChatCodeRole.Comment),
                ("Button", ChatCodeRole.Attribute),
                ("Content", ChatCodeRole.Attribute),
                ("\"Go\"", ChatCodeRole.String),
            ],
            Roles("<!-- c --><Button Content=\"Go\" />", "xaml"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("brainfuck")]
    public void UnknownOrMissingLanguages_StayPlain(string? language)
    {
        Assert.Empty(ChatCodeSyntax.Classify("const x = \"y\";", language));
    }

    [Fact]
    public void OversizedCode_StaysPlain()
    {
        var code = new string('x', ChatCodeSyntax.MaxHighlightedLength) + " = 1";
        Assert.Empty(ChatCodeSyntax.Classify(code, "js"));
    }

    [Fact]
    public void TokenDenseCode_StaysPlainInsteadOfCreatingThousandsOfColoredRanges()
    {
        var dense = "[" + string.Join(",", Enumerable.Repeat("0", ChatCodeSyntax.MaxHighlightedSpans + 1)) + "]";
        Assert.True(dense.Length < ChatCodeSyntax.MaxHighlightedLength);
        Assert.Empty(ChatCodeSyntax.Classify(dense, "json"));
        Assert.Equal(
            ChatCodeSyntax.MaxHighlightedSpans,
            ChatCodeSyntax.Classify(string.Join(",", Enumerable.Repeat("0", ChatCodeSyntax.MaxHighlightedSpans)), "json").Count);
    }

    [Fact]
    public void UnterminatedString_StopsBeforeCrLf()
    {
        var span = Assert.Single(ChatCodeSyntax.Classify("x = \"open\r\ny", "ts"), span => span.Role == ChatCodeRole.String);
        Assert.Equal("\"open", "x = \"open\r\ny".Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("cs")]
    [InlineData("python")]
    [InlineData("powershell")]
    [InlineData("bash")]
    [InlineData("yaml")]
    [InlineData("toml")]
    [InlineData("sql")]
    [InlineData("css")]
    [InlineData("html")]
    [InlineData("go")]
    public void Spans_AreOrderedNonOverlappingAndInBounds(string language)
    {
        const string code =
            "/* block */ // line\r\n# hash\n-- dash\n<a b=\"c\">\n[section]\nkey = 'v'\n" +
            "fn main() { let s = \"unterminated\n  x: 0x1F; $var ${brace} @media .5 }\n\"\"\"triple\"\"\" `tick";
        var position = 0;
        foreach (var span in ChatCodeSyntax.Classify(code, language))
        {
            Assert.True(span.Length > 0);
            Assert.True(span.Start >= position, $"{language}: span at {span.Start} overlaps {position}");
            position = span.Start + span.Length;
            Assert.True(position <= code.Length);
        }
    }
}
