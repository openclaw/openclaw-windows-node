using System.Net;

namespace OpenClaw.SetupEngine.Tests;

public class WslPlatformInstallDiagnosticsTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(10, false)]
    public void DescribeFailure_ExplainsCauseAndRecovery(int remaining, bool exhausted)
    {
        var quota = new GitHubApiQuota(60, remaining, DateTimeOffset.UtcNow.AddMinutes(10));

        string message = WslPlatformInstallDiagnostics.DescribeFailure(1, quota);

        Assert.Contains("exit code 1", message);
        Assert.Equal(exhausted, message.Contains("quota is exhausted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(WslInstallSupport.UpdateUrl, message);
        Assert.Contains("winget install --id 9P9TQF7MRM4R --source msstore", message);
        Assert.Contains("wsl --install --no-distribution", message);
    }

    [Fact]
    public void DescribeFailure_UnknownQuota_DoesNotClaimRateLimit()
    {
        string message = WslPlatformInstallDiagnostics.DescribeFailure(5, quota: null);

        Assert.Contains("network, policy, or installer error", message);
        Assert.DoesNotContain("quota is exhausted", message);
    }

    [Fact]
    public async Task QueryGitHubQuota_TimeoutIsBestEffort()
    {
        using var http = new HttpClient(new DelayedHandler()) { Timeout = TimeSpan.FromMilliseconds(20) };

        Assert.Null(await WslPlatformInstallDiagnostics.QueryGitHubQuotaAsync(http, CancellationToken.None));
    }

    [Fact]
    public async Task QueryGitHubQuota_CallerCancellationPropagates()
    {
        using var http = new HttpClient(new DelayedHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WslPlatformInstallDiagnostics.QueryGitHubQuotaAsync(http, cts.Token));
    }

    [Fact]
    public void EnsureWslPlatform_IsRetryable() => Assert.True(new EnsureWslPlatformStep().CanRetry);

    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
