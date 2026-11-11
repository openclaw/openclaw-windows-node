// Tests CDP endpoint publication with real temporary files, without launching Edge or a Gateway.
// Run with --filter FullyQualifiedName~GatewaySearchBrowserProofTests.
using System.Text;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Protects the browser startup boundary while its endpoint file is still being published.</summary>
public sealed class GatewaySearchBrowserProofTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openclaw-browser-port-{Guid.NewGuid():N}");
    private string PortFile => Path.Combine(_directory, "DevToolsActivePort");

    public GatewaySearchBrowserProofTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task EndpointLockedByPublisher_WaitsForRelease()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        Task<Uri> pending;
        await using (var writer = new FileStream(PortFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("49152\n/devtools/browser/test-browser\n"), timeout.Token);
            await writer.FlushAsync(timeout.Token);
            pending = GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => false, timeout.Token);
            if (pending.IsCompleted) await pending;
            Assert.False(pending.IsCompleted);
        }

        var endpoint = await pending;
        Assert.Equal("ws://127.0.0.1:49152/devtools/browser/test-browser", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("49152\n")]
    public async Task UnpublishedEndpoint_WaitsForBothLines(string? initialContent)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var published = new ManualResetEventSlim();
        if (initialContent is not null) await File.WriteAllTextAsync(PortFile, initialContent, timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        var pending = GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () =>
        {
            if (Interlocked.Increment(ref observations) == 2)
            {
                waiting.TrySetResult();
                published.Wait(timeout.Token);
            }
            return false;
        }, timeout.Token);
        await waiting.Task.WaitAsync(timeout.Token);
        Assert.False(pending.IsCompleted);

        try { await File.WriteAllTextAsync(PortFile, "49152\n/devtools/browser/test-browser", timeout.Token); }
        finally { published.Set(); }

        Assert.Equal("ws://127.0.0.1:49152/devtools/browser/test-browser", (await pending).AbsoluteUri);
    }

    [Fact]
    public async Task LockedEndpoint_RemainsBoundByStartupCancellation()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await using var writer = new FileStream(PortFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var pending = GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => false, timeout.Token);

        timeout.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task BrowserExit_StopsWaitingForPublication()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => true, timeout.Token));

        Assert.Equal("The owned browser exited before CDP was ready.", error.Message);
    }

    [Fact]
    public async Task PublishedInvalidPath_IsNotTreatedAsPending()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await File.WriteAllTextAsync(PortFile, "49152\n/not-a-browser-endpoint", timeout.Token);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => false, timeout.Token));
    }

    [Fact]
    public async Task PublishedInvalidPort_IsNotTreatedAsPending()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await File.WriteAllTextAsync(PortFile, "not-a-port\n/devtools/browser/test-browser", timeout.Token);

        await Assert.ThrowsAsync<FormatException>(() =>
            GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => false, timeout.Token));
    }

    [Fact]
    public async Task UnrelatedAccessFailure_IsNotTreatedAsPending()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        Directory.CreateDirectory(PortFile);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            GatewaySearchBrowserProof.WaitForEndpointAsync(PortFile, () => false, timeout.Token));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
