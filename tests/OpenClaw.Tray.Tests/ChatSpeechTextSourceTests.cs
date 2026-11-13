namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source guard for the closed speech-text ownership: chat Markdown is
/// projected exactly once, by <c>OpenClaw.Shared.Markdown.ChatSpeechTextProjector</c>.
/// A second regex sanitizer anywhere on the read-aloud path is what used to
/// give auto-read and the Read aloud button different text (and glue
/// snake_case words together).
/// </summary>
public sealed class ChatSpeechTextSourceTests
{
    private static string RepoRoot => TestRepositoryPaths.GetRepositoryRoot();

    private static string ReadTrayFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot, "src", "OpenClaw.Tray.WinUI" }
            .Concat(pathParts).ToArray()));

    [Fact]
    public void Coordinator_ProjectsSpeechTextThroughTheSharedProjector()
    {
        var source = ReadTrayFile("Chat", "OpenClawChatCoordinator.cs");

        Assert.Contains("ChatSpeechTextProjector.Project(", source);
        Assert.DoesNotContain("SanitizeForSpeech", source);
    }

    [Fact]
    public void ReadAloudButton_DoesNotSanitizeBeforeTheCoordinator()
    {
        var source = ReadTrayFile("Chat", "ReactorChatTimeline.cs");

        Assert.DoesNotContain("StripMarkdownForSpeech", source);
        Assert.Contains("await readAloud(text);", source);
    }

    [Fact]
    public void SpeechTextOwners_DoNotStripPunctuationWithCharacterBlacklists()
    {
        // The projector parses Markdown; it must never rewrite user punctuation
        // character by character.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "OpenClaw.Shared", "Markdown", "ChatSpeechTextProjector.cs"));

        Assert.DoesNotContain(@"[~|<>{}[\]\\*]", source);
        Assert.DoesNotContain(@"\p{Cs}", source);
    }
}
