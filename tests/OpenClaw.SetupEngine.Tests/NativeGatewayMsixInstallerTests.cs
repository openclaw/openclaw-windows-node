namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewayMsixInstallerTests
{
    [Fact]
    public async Task Open_HandsTheFixedStoreListingToWindowsExactlyOnce()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await new NativeGatewayMsixInstaller().OpenAsync((uri, ct) =>
        {
            Assert.Equal("https://apps.microsoft.com/detail/9nv70lv3d6xc?hl=en-US&gl=US", uri.AbsoluteUri);
            Assert.Equal(cts.Token, ct);
            calls++;
            return Task.FromResult(true);
        }, cts.Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ShellDeclinesLaunch_ProvidesStoreLinkAndRetryGuidance()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().OpenAsync((_, _) => Task.FromResult(false), CancellationToken.None));
        Assert.Contains(NativeGatewayMsixInstaller.StoreUri.AbsoluteUri, error.Message);
        Assert.Contains("retry native setup", error.Message);
    }

    [Fact]
    public async Task ShellFailure_IsPropagated()
    {
        var failure = new InvalidOperationException("URI handler failed");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().OpenAsync((_, _) => throw failure, CancellationToken.None));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task Cancellation_DoesNotOpenStore()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativeGatewayMsixInstaller().OpenAsync((_, _) =>
                throw new Xunit.Sdk.XunitException("The Store must not be opened."), cts.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledHandoff_DoesNotReportSuccess(bool unresponsive)
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativeGatewayMsixInstaller().OpenAsync((_, _) =>
            {
                cts.Cancel();
                return unresponsive ? new TaskCompletionSource<bool>().Task : Task.FromResult(true);
            }, cts.Token));
    }
}
