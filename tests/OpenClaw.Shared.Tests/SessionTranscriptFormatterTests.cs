using System.Collections.Generic;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionTranscriptFormatterTests
{
    [Fact]
    public void Format_IncludesHeaderAndMessages()
    {
        var history = new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "uuid-1",
            Messages = new List<ChatMessageInfo>
            {
                new() { Role = "user", Text = "hello", Ts = 1735732800000 },
                new() { Role = "assistant", Text = "hi there" },
            },
        };

        var text = SessionTranscriptFormatter.Format(history);

        Assert.Contains("OpenClaw session transcript", text);
        Assert.Contains("Session: main", text);
        Assert.Contains("Session ID: uuid-1", text);
        Assert.Contains("Messages: 2", text);
        Assert.Contains("[user", text);
        Assert.Contains("hello", text);
        Assert.Contains("[assistant]", text);
        Assert.Contains("hi there", text);
    }

    [Fact]
    public void Format_HandlesEmptyTranscript()
    {
        var history = new ChatHistoryInfo { SessionKey = "main" };
        var text = SessionTranscriptFormatter.Format(history);
        Assert.Contains("Messages: 0", text);
    }

    [Fact]
    public void Format_TimestampOnlyWhenPresent()
    {
        var history = new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new List<ChatMessageInfo> { new() { Role = "assistant", Text = "x", Ts = 0 } },
        };

        var text = SessionTranscriptFormatter.Format(history);
        Assert.Contains("[assistant]", text); // no " · timestamp" suffix when Ts == 0
    }

    [Fact]
    public void FormatMarkdown_MapsRolesSkipsBlankAndStartsWithTitle()
    {
        var history = new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new List<ChatMessageInfo>
            {
                new() { Role = "user", Text = "hello", Ts = 1735732800000 },
                new() { Role = "assistant", Text = "   " },
                new() { Role = "tool", Text = "ran command" },
            },
        };

        var md = SessionTranscriptFormatter.FormatMarkdown(history, "My title");

        Assert.NotNull(md);
        Assert.StartsWith("# My title\n\n## User", md);
        Assert.DoesNotContain("## Assistant", md);
        Assert.Contains("## Tool", md);
        Assert.Contains("ran command", md);
        Assert.Contains("(2025-01-01T12:00:00.0000000Z)", md);
    }

    [Fact]
    public void FormatMarkdown_AllBlankHistoryReturnsNull()
    {
        var history = new ChatHistoryInfo
        {
            Messages = new List<ChatMessageInfo> { new() { Role = "user", Text = "  " } },
        };

        Assert.Null(SessionTranscriptFormatter.FormatMarkdown(history, "t"));
    }

    [Fact]
    public void SuggestFileName_IsFilesystemSafe()
    {
        var name = SessionTranscriptFormatter.SuggestFileName("agent:main:wa/bob");

        Assert.StartsWith("openclaw-transcript-", name);
        Assert.EndsWith(".txt", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
    }

    [Fact]
    public void SuggestFileName_FallsBackForBlankKey()
    {
        var name = SessionTranscriptFormatter.SuggestFileName(null);
        Assert.Contains("session", name);
    }
}
