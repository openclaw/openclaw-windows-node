namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source guard for the closed speech-text ownership. This is the only
/// source-shape test in this area: it exists because the two regex sanitizers
/// removed here can be reintroduced inside a WinUI code-behind path that no
/// behavioural test reaches. Its retirement condition is recorded in
/// <c>docs/ARCHITECTURE.md</c>.
///
/// The behaviour it protects, that both speech entry points project the same
/// text, is covered by <see cref="ChatSpeechTextProjectionTests"/>.
/// </summary>
public sealed class ChatSpeechTextSourceTests
{
    private static string RepoRoot => TestRepositoryPaths.GetRepositoryRoot();

    private static string ReadTrayFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot, "src", "OpenClaw.Tray.WinUI" }
            .Concat(pathParts).ToArray()));

    [Fact]
    public void ChatSpeechOwners_DoNotReadmitTheRegexSanitizers()
    {
        Assert.DoesNotContain("SanitizeForSpeech", ReadTrayFile("Chat", "OpenClawChatCoordinator.cs"));
        Assert.DoesNotContain("StripMarkdownForSpeech", ReadTrayFile("Chat", "ReactorChatTimeline.cs"));
    }
}
