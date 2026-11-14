using OpenClaw.Shared;
using OpenClaw.Shared.Markdown;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Behaviour of the one owner of chat speech text. Both speech entry points must
/// hand the TTS engine the same projected text, and that text must never carry a
/// URL or an inline credential. These assertions drive the real projector
/// through the coordinator's replaceable speak seam, so they survive renames and
/// internal refactoring instead of pinning source text.
/// </summary>
public sealed class ChatSpeechTextProjectionTests
{
    private const string Markdown =
        "## Deploy\n\n"
        + "Connect to http://localhost:18789/?token=FAKE123 now.\n\n"
        + "Use `api_key=FAKE-SECRET-1234` and `user_id` here.\n\n"
        + "```bash\nopenclaw start\n```\n\n"
        + "Done!";

    [Fact]
    public async Task BothSpeechEntryPoints_HandTheEngineTheSameProjectedText()
    {
        using var run = new CoordinatorRun();

        await run.Coordinator.SpeakResponseAsync(Markdown);
        await run.Coordinator.SpeakChatTextAsync(Markdown);

        Assert.Equal(2, run.Spoken.Count);
        Assert.Equal(run.Spoken[0], run.Spoken[1]);
        Assert.Equal(ChatSpeechTextProjector.Project(Markdown), run.Spoken[0]);
    }

    [Fact]
    public async Task SpokenText_KeepsSentenceBoundariesAndDropsSecretsAndUrls()
    {
        using var run = new CoordinatorRun();

        await run.Coordinator.SpeakResponseAsync(Markdown);

        var text = Assert.Single(run.Spoken);
        Assert.Equal("Deploy. Connect to now. Use and user id here. Done!", text);
        Assert.DoesNotContain("FAKE", text);
        Assert.DoesNotContain("http", text);
        Assert.DoesNotContain("openclaw start", text);
    }

    [Fact]
    public async Task ReplyWithNothingSpeakable_DoesNotCallTheEngine()
    {
        using var run = new CoordinatorRun();

        await run.Coordinator.SpeakResponseAsync("```\nopenclaw start\n```");

        Assert.Empty(run.Spoken);
    }

    /// <summary>
    /// Isolated settings plus a coordinator whose speak seam records the exact
    /// text the engine would receive.
    /// </summary>
    private sealed class CoordinatorRun : IDisposable
    {
        private readonly string _directory;

        public CoordinatorRun()
        {
            _directory = Directory.CreateTempSubdirectory("openclaw-speech-").FullName;
            var settings = new SettingsManager(_directory) { NodeTtsEnabled = true };
            Coordinator = new OpenClawChatCoordinator(
                settings,
                () => null,
                NullLogger.Instance,
                null,
                () => null,
                (args, _) =>
                {
                    Spoken.Add(args.Text);
                    return Task.CompletedTask;
                },
                () => { });
        }

        public OpenClawChatCoordinator Coordinator { get; }

        public List<string> Spoken { get; } = new();

        public void Dispose()
        {
            Coordinator.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
