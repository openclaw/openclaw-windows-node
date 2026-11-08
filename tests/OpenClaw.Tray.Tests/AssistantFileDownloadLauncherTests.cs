using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class AssistantFileDownloadLauncherTests
{
    private static readonly Uri TicketedUri =
        new("http://127.0.0.1:18789/api/chat/media/outgoing/s/a/full?mediaTicket=ticket-1");

    [Fact]
    public async Task DownloadAsync_ResolvedUri_LaunchesItAndReportsStarted()
    {
        Uri? launched = null;

        var status = await AssistantFileDownloadLauncher.DownloadAsync(
            _ => Task.FromResult<Uri?>(TicketedUri),
            uri =>
            {
                launched = uri;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(AssistantFileDownloadStatus.Started, status);
        Assert.Equal(TicketedUri, launched);
    }

    [Fact]
    public async Task DownloadAsync_UnresolvedUri_ReportsUnavailableWithoutLaunching()
    {
        var launches = 0;

        var status = await AssistantFileDownloadLauncher.DownloadAsync(
            _ => Task.FromResult<Uri?>(null),
            _ =>
            {
                launches++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(AssistantFileDownloadStatus.Unavailable, status);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task DownloadAsync_ResolverThrows_ReportsUnavailableWithoutLaunching()
    {
        var launches = 0;

        var status = await AssistantFileDownloadLauncher.DownloadAsync(
            _ => throw new InvalidOperationException("gateway gone"),
            _ =>
            {
                launches++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(AssistantFileDownloadStatus.Unavailable, status);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task DownloadAsync_BrowserRejectsLaunch_ReportsFailed()
    {
        var status = await AssistantFileDownloadLauncher.DownloadAsync(
            _ => Task.FromResult<Uri?>(TicketedUri),
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.Equal(AssistantFileDownloadStatus.Failed, status);
    }

    [Fact]
    public async Task DownloadAsync_LaunchThrows_ReportsFailed()
    {
        var status = await AssistantFileDownloadLauncher.DownloadAsync(
            _ => Task.FromResult<Uri?>(TicketedUri),
            _ => throw new InvalidOperationException("no browser"),
            CancellationToken.None);

        Assert.Equal(AssistantFileDownloadStatus.Failed, status);
    }

    [Fact]
    public void FileDownloadPresentationPath_NeverReceivesTicketedUris()
    {
        // assistant-media-resolver: the renderer never receives tickets or URLs. The
        // bridge launches the browser and presentation code only sees the typed status.
        var chatDirectory = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat");
        foreach (var file in new[]
                 {
                     "ChatAssistantMediaRenderer.cs",
                     "ChatTimelinePresentationContext.cs",
                     "ReactorChatTimeline.cs",
                     "OpenClawReactorChatRoot.cs",
                     "OpenClawChatDataProvider.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(chatDirectory, file));
            Assert.DoesNotContain("ResolveAssistantFileDownloadUriAsync", source);
            Assert.DoesNotContain("LaunchUriAsync", source);
            Assert.DoesNotContain("Task<Uri", source);
        }

        var bridge = File.ReadAllText(Path.Combine(chatDirectory, "IChatGatewayBridge.cs"));
        Assert.Contains("Task<AssistantFileDownloadStatus> DownloadAssistantFileAsync(", bridge);
        Assert.Contains("AssistantFileDownloadLauncher.DownloadAsync(", bridge);
        Assert.DoesNotContain("Task<Uri", bridge);
    }
}
