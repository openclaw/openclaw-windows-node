using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit tests for <see cref="OpenClawChatDataProvider.RepairContentBlockSeams"/>.
/// Anthropic gateway concatenates multiple text content-blocks (pre-tool
/// intent + post-tool reflection) into one chat.message.text with no
/// whitespace between them. These tests lock in the client-side seam
/// repair so the rendered bubble has proper paragraph breaks.
///
/// Positive fixtures are taken from real Sonnet 4.5 / Opus 4.7 captures
/// in the user screenshots that motivated this fix.
/// </summary>
public class ContentBlockSeamRepairTests
{
    [Theory]
    [InlineData(
        "**Step 1: List all files in C:\\Windows\\System32**The command was blocked by the exec policy.",
        "**Step 1: List all files in C:\\Windows\\System32**\n\nThe command was blocked by the exec policy.")]
    [InlineData(
        "Let me try with PowerShell:The C:\\temp directory doesn't exist.",
        "Let me try with PowerShell:\n\nThe C:\\temp directory doesn't exist.")]
    [InlineData(
        "I ran the command on your Windows node.Looks like there's a problem.",
        "I ran the command on your Windows node.\n\nLooks like there's a problem.")]
    [InlineData(
        "Want me to verify the deletion works?Got it - less exploring, more doing.",
        "Want me to verify the deletion works?\n\nGot it - less exploring, more doing.")]
    [InlineData(
        "All done!Next, let's review the output.",
        "All done!\n\nNext, let's review the output.")]
    public void RepairsKnownSeams(string input, string expected)
    {
        Assert.Equal(expected, OpenClawChatDataProvider.RepairContentBlockSeams(input));
    }

    [Theory]
    [InlineData("**bold**word")]                                    // inline emphasis, lowercase after
    [InlineData("Run cmd at C:\\temp for testing.")]                // drive-letter path
    [InlineData("See https://example.com/path for details.")]       // URL after colon
    [InlineData("Meet at 10:30am tomorrow afternoon.")]             // time-of-day
    [InlineData("The U.S.A. is large.")]                            // single-letter abbreviations
    [InlineData("Use e.g. foo or bar in this case.")]               // lower-case after period
    [InlineData("Normal prose. With proper spacing!")]              // already spaced
    [InlineData("First sentence.\n\nSecond sentence.")]             // already paragraph-broken
    [InlineData("")]
    [InlineData("hi")]
    // Identifier / member-access patterns must NOT be split:
    [InlineData("Call Path.Combine(a, b) here.")]                   // static method
    [InlineData("Use System.IO.File.ReadAllText for that.")]        // chained namespace
    [InlineData("The obj.Method() returns null.")]                  // instance method
    [InlineData("Check db.Server01 for the value.")]                // identifier with digits
    [InlineData("Store it in MyVar.OtherVar")]                      // Pascal.Pascal at EOS
    [InlineData("Look at config.json:line for context")]            // file:line ref (no Pascal after)
    [InlineData("the field is x.Baz")]                              // bare identifier at EOS — must not split
    [InlineData("stored in obj.Foo")]                               // ditto
    [InlineData("set this to obj.Bar")]                             // ditto
    public void DoesNotInjectFalsePositives(string input)
    {
        Assert.Equal(input, OpenClawChatDataProvider.RepairContentBlockSeams(input));
    }

    [Fact]
    public void NullInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, OpenClawChatDataProvider.RepairContentBlockSeams(null));
    }

    [Fact]
    public void RepairIsIdempotent()
    {
        const string raw = "Done with the file.Next step is to verify.";
        var once = OpenClawChatDataProvider.RepairContentBlockSeams(raw);
        var twice = OpenClawChatDataProvider.RepairContentBlockSeams(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void FencedCodeBlocksAreNotModified()
    {
        // Inside the fence we have a `.X` seam and a `:X` seam that
        // would otherwise match; both must be preserved verbatim because
        // they're real JSON/code, not glued content-block boundaries.
        const string input =
            "Here is the config.Run this:\n" +
            "```json\n" +
            "{\"foo\":\"bar.Baz\",\"url\":\"https://example.com/Path\"}\n" +
            "```\n" +
            "All set.Anything else?";

        const string expected =
            "Here is the config.\n\nRun this:\n" +
            "```json\n" +
            "{\"foo\":\"bar.Baz\",\"url\":\"https://example.com/Path\"}\n" +
            "```\n" +
            "All set.\n\nAnything else?";

        Assert.Equal(expected, OpenClawChatDataProvider.RepairContentBlockSeams(input));
    }

    [Fact]
    public void UnclosedFenceProtectsTailFromMutation()
    {
        // An unclosed fence is rare but we should fail safe: don't inject
        // newlines inside what the renderer will treat as code.
        const string input =
            "Output below.Looking at it:\n" +
            "```\n" +
            "first.Second\n" +
            "third.Fourth";

        const string expected =
            "Output below.\n\nLooking at it:\n" +
            "```\n" +
            "first.Second\n" +
            "third.Fourth";

        Assert.Equal(expected, OpenClawChatDataProvider.RepairContentBlockSeams(input));
    }

    [Fact]
    public void MultipleConsecutiveSeamsAllRepaired()
    {
        const string input = "Step one done.Step two done.Step three done.All finished!";
        const string expected = "Step one done.\n\nStep two done.\n\nStep three done.\n\nAll finished!";
        Assert.Equal(expected, OpenClawChatDataProvider.RepairContentBlockSeams(input));
    }
}
