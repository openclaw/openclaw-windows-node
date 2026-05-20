using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit tests for <see cref="OpenClawChatDataProvider"/>'s
/// history-flattened-tool-output heuristics. Locks in the recognition of
/// gateway-flattened ``--help`` dumps, exec terminator markers, and system
/// control notes so we don't regress when adding new tool families.
/// </summary>
public class FlattenedToolOutputDetectionTests
{
    [Theory]
    [InlineData("Process exited with code 0.")]
    [InlineData("Output:\nfoo\nProcess exited with code 1.")]
    [InlineData("Command still running (session dawn-reef, pid 14912). Use process for follow-up.")]
    [InlineData("Exec completed (oceanic, code 0)")]
    public void DetectsExecTerminatorMarkers(string text)
    {
        // Padded so we clear the 40-char minimum even on the shorter literals.
        text = text.PadRight(50, '.');
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text),
            $"Expected to detect: {text}");
    }

    [Theory]
    [InlineData("\\\\wsl.localhost\\Ubuntu\\home\\user\\foo.wav -rw-r--r-- 1 user user 12K May 7 12:00 foo")]
    [InlineData("/usr/lib/node_modules/openclaw/dist/foo.js exists")]
    [InlineData("/home/user/.bashrc not modified                       ")]
    [InlineData("/var/log/syslog rotated yesterday at 03:14 UTC          ")]
    [InlineData("/etc/passwd permissions checked: 0644 (-rw-r--r--)        ")]
    [InlineData("/tmp/openclaw-foo.lock present, age 14m, owner regisb     ")]
    public void DetectsSystemPathOpenings(string text)
    {
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text),
            $"Expected to detect: {text}");
    }

    [Theory]
    [InlineData("OpenClaw 2026.4.23 (a979721) — iMessage green bubble energy, but for everyone.\nUsage: openclaw nodes invoke")]
    [InlineData("OpenClaw v1.5.0 — gateway test build")]
    [InlineData("openclaw nodes invoke --node main --command canvas.eval")]
    public void DetectsOpenClawCliBanner(string text)
    {
        text = text.PadRight(50, '.');
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text),
            $"Expected to detect: {text}");
    }

    [Fact]
    public void DetectsCliHelpLayout_UsagePlusOptions()
    {
        // ``Usage:`` + ``Options:`` together is a strong --help signal even
        // without the OpenClaw banner.
        var text = "Usage: somecli [options]\nDoes things.\nOptions: --foo bar";
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text));
    }

    [Fact]
    public void DetectsCliHelpLayout_UsagePlusCommands()
    {
        var text = "Usage: somecli <verb>\nCommands:\n  list\n  show\n  delete";
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text));
    }

    [Fact]
    public void DetectsDenseFlagListings()
    {
        // Long enough (>= 200 chars) and with >= 5 ``--flag`` tokens.
        var text =
            "Lorem ipsum dolor sit amet consectetur adipiscing elit. " +
            "Some prose here that mentions various options including " +
            "--alpha --beta --gamma --delta --epsilon --zeta " +
            "and a bit more padding text to clear the 200 char minimum threshold easily.";
        Assert.True(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text));
    }

    [Theory]
    [InlineData("That sentence is unclear — could you clarify what you'd like to know?")]
    [InlineData("On your current Windows node, supported capability groups are app, browser, camera.")]
    [InlineData("Hi! Yes I can help with that. What time would you like to schedule the meeting for?")]
    [InlineData("Done — I spoke \"Hello World\".")]
    public void DoesNotMatchNormalProse(string text)
    {
        Assert.False(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput(text),
            $"Expected NOT to match: {text}");
    }

    [Fact]
    public void DoesNotMatchShortText()
    {
        // Even with a marker, ``< 40 chars`` is too short to be a real tool dump.
        Assert.False(OpenClawChatDataProvider.LooksLikeFlattenedToolOutput("Process exited"));
    }

    [Theory]
    [InlineData("System (untrusted): Tool reported success.", true)]
    [InlineData("System: Reset session", true)]
    [InlineData("Hello there", false)]
    [InlineData("system note", false)]
    public void DetectsSystemControlNotes(string text, bool expected)
    {
        Assert.Equal(expected, OpenClawChatDataProvider.LooksLikeSystemControlNote(text));
    }

    // ── chat rubber-duck round 2 MEDIUM 2: prefix tightening ──

    [Theory]
    [InlineData("System (untrusted): Exec completed (oceanic, code 0) :: ok")]
    [InlineData("System (untrusted): An async command you ran earlier has completed")]
    [InlineData("System (untrusted): exec result for tool_call_42 follows")]
    [InlineData("System (untrusted): Tool reported success.")]
    [InlineData("System: Reset session")]
    [InlineData("System: Process exited with code 0")]
    [InlineData("System: Command still running (session foo, pid 1)")]
    [InlineData("System (untrusted): tool_call_abc started")]
    public void LooksLikeSystemControlNote_OnRealSystemNote_ReturnsTrue(string text)
    {
        Assert.True(OpenClawChatDataProvider.LooksLikeSystemControlNote(text));
    }

    [Theory]
    // Plain user prose that happens to start with the magic prefix —
    // MUST NOT be reclassified as a dim system control entry.
    [InlineData("System (untrusted): hello world")]
    [InlineData("System: hello world")]
    [InlineData("System (untrusted): I think this is fine")]
    [InlineData("System: my notes for today")]
    // Prefix without any structural marker.
    [InlineData("System (untrusted): just chatting")]
    public void LooksLikeSystemControlNote_OnPlainUserMessageWithSystemPrefix_ReturnsFalse(string text)
    {
        Assert.False(OpenClawChatDataProvider.LooksLikeSystemControlNote(text));
    }

    // ── chat rubber-duck round 2 LOW 4: TruncateChatEvent coverage ──

    [Fact]
    public void TruncateChatEvent_ChatModelChangedEvent_TruncatesModelField()
    {
        var huge = new string('m', 400_000);
        var truncated = (ChatModelChangedEvent)OpenClawChatDataProvider.TruncateChatEvent(
            new ChatModelChangedEvent(huge));
        Assert.True(truncated.Model.Length < huge.Length);
    }

    [Fact]
    public void TruncateChatEvent_ChatIntentEvent_TruncatesIntentField()
    {
        var huge = new string('i', 400_000);
        var truncated = (ChatIntentEvent)OpenClawChatDataProvider.TruncateChatEvent(
            new ChatIntentEvent(huge));
        Assert.True(truncated.Intent.Length < huge.Length);
    }

    [Fact]
    public void TruncateChatEvent_ChatPermissionRequestEvent_TruncatesAllTextFields()
    {
        var huge = new string('p', 400_000);
        var truncated = (ChatPermissionRequestEvent)OpenClawChatDataProvider.TruncateChatEvent(
            new ChatPermissionRequestEvent("req-1", huge, huge, huge));
        Assert.True(truncated.PermissionKind.Length < huge.Length);
        Assert.True(truncated.ToolName.Length < huge.Length);
        Assert.True(truncated.Detail.Length < huge.Length);
        Assert.Equal("req-1", truncated.RequestId);
    }

    [Fact]
    public void TruncateChatEvent_ChatToolStartEvent_TruncatesLabelAndToolName()
    {
        var hugeLabel = new string('l', 400_000);
        var hugeName = new string('n', 400_000);

        var truncated = (ChatToolStartEvent)OpenClawChatDataProvider.TruncateChatEvent(
            new ChatToolStartEvent(hugeLabel, hugeName));

        Assert.True(truncated.Text.Length < hugeLabel.Length);
        Assert.True(truncated.ToolName.Length < hugeName.Length);
        Assert.EndsWith("bytes truncated]", truncated.Text);
        Assert.EndsWith("bytes truncated]", truncated.ToolName);
    }

    [Fact]
    public void TruncateChatEvent_AllRenderedTextFieldsRespectByteCap()
    {
        var huge = new string('x', 400_000);

        AssertCapped(((ChatUserMessageEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatUserMessageEvent(huge))).Text);
        AssertCapped(((ChatThinkingEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatThinkingEvent(huge))).Text);
        AssertCapped(((ChatReasoningEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatReasoningEvent(huge))).Text);
        AssertCapped(((ChatReasoningDeltaEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatReasoningDeltaEvent(huge))).Text);

        var message = (ChatMessageEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatMessageEvent(huge, huge));
        AssertCapped(message.Text);
        AssertCapped(message.ReasoningText!);

        AssertCapped(((ChatMessageDeltaEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatMessageDeltaEvent(huge))).Text);
        AssertCapped(((ChatToolStartEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatToolStartEvent(huge, huge))).Text);
        AssertCapped(((ChatToolStartEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatToolStartEvent(huge, huge))).ToolName);
        AssertCapped(((ChatToolOutputEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatToolOutputEvent(huge))).Text);
        AssertCapped(((ChatToolErrorEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatToolErrorEvent(huge))).Text);
        AssertCapped(((ChatStatusEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatStatusEvent(huge, ChatTone.Dim))).Text);
        AssertCapped(((ChatErrorEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatErrorEvent(huge))).Text);
        AssertCapped(((ChatRestoredEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatRestoredEvent(huge))).Text);
        AssertCapped(((ChatRawEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatRawEvent("raw", huge))).Text!);
        AssertCapped(((ChatModelChangedEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatModelChangedEvent(huge))).Model);
        AssertCapped(((ChatIntentEvent)OpenClawChatDataProvider.TruncateChatEvent(new ChatIntentEvent(huge))).Intent);

        var permission = (ChatPermissionRequestEvent)OpenClawChatDataProvider.TruncateChatEvent(
            new ChatPermissionRequestEvent("req-1", huge, huge, huge));
        AssertCapped(permission.PermissionKind);
        AssertCapped(permission.ToolName);
        AssertCapped(permission.Detail);

        static void AssertCapped(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes,
                $"Expected <= {OpenClawChatDataProvider.MaxEntryTextBytes} bytes but got {bytes}.");
            Assert.EndsWith("bytes truncated]", text);
        }
    }

    [Theory]
    [InlineData("Command still running (session foo, pid 1)", "bash")]
    [InlineData("Process exited with code 0", "bash")]
    [InlineData("Exec completed (oceanic, code 0)", "exec")]
    [InlineData("OpenClaw 2026.4.23 — Usage: openclaw help", "exec")]
    [InlineData("  1. using System;\n  2. using System.IO;\n  3. namespace Foo;", "view")]
    [InlineData("src/Main.cs:42:    var x = 1;", "grep")]
    [InlineData("commit abc123\nAuthor: User\nDate: Mon Jan 1", "git")]
    [InlineData("diff --git a/foo.cs b/foo.cs", "git")]
    public void ClassifiesKindCorrectly(string text, string expected)
    {
        Assert.Equal(expected, OpenClawChatDataProvider.ClassifyFlattenedToolOutput(text));
    }

    [Theory]
    [InlineData("(no output)")]
    [InlineData("At line:1 char:128\nSomething PowerShell error happened")]
    [InlineData("{\n  \"providers\": [\n    { \"id\": \"elevenlabs\" }\n  ]\n}")]
    public void ToolresultRoleAlwaysClassified(string text)
    {
        // ``toolresult`` role sidesteps the heuristic — but the kind label
        // must still produce a recognized tool type so the chip header reads
        // sensibly. Default fallthrough is "exec".
        var kind = OpenClawChatDataProvider.ClassifyFlattenedToolOutput(text);
        Assert.False(string.IsNullOrEmpty(kind), $"Empty kind for: {text}");
    }

    // ── Additional classifier edge cases ──

    [Theory]
    [InlineData("Cloning into 'repo'...\nremote: Enumerating objects: 100", "exec")]
    [InlineData("On branch main\nYour branch is up to date with 'origin/main'.", "exec")]
    [InlineData("fatal: not a git repository", "exec")]
    [InlineData("src/foo.cs\nsrc/bar.cs\nsrc/baz.cs", "exec")]
    [InlineData("  1. first line\n  2. second line\n  3. third line", "view")]
    [InlineData("--- a/src/main.cs\n+++ b/src/main.cs\n@@ -1,3 +1,4 @@", "exec")]
    public void ClassifiesAdditionalKindsCorrectly(string text, string expected)
    {
        Assert.Equal(expected, OpenClawChatDataProvider.ClassifyFlattenedToolOutput(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    public void ClassifyFlattenedToolOutput_EmptyOrTiny_ReturnsExecDefault(string text)
    {
        // Even empty/tiny text should return a non-empty kind (default "exec")
        var kind = OpenClawChatDataProvider.ClassifyFlattenedToolOutput(text);
        Assert.False(string.IsNullOrEmpty(kind));
    }
}
