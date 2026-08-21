using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardLinkServiceTests
{
    [Fact]
    public async Task BuildAsync_SharedCredential_AppendsCredentialWithoutRevalidation()
    {
        var revalidationCalls = 0;
        var service = CreateService((_, _) =>
        {
            revalidationCalls++;
            return Task.FromResult(true);
        });

        var result = await service.BuildAsync(Request());

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal(0, revalidationCalls);
    }

    [Fact]
    public async Task BuildAsync_TrustedTailscaleGateway_OmitsSharedCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(true));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.True(result.TrustTailscaleAuth);
        Assert.DoesNotContain("token=", result.Url);
    }

    [Fact]
    public async Task BuildAsync_FailedRevalidation_FallsBackToSharedCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.False(result.TrustTailscaleAuth);
        Assert.Contains("token=shared-token", result.Url);
    }

    [Fact]
    public async Task BuildAsync_TokenFreeBootstrapRequest_PreservesDashboardUrl()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            tailscaleGatewayId: null));

        Assert.True(result.Success);
        Assert.NotNull(result.Url);
        Assert.DoesNotContain("token=", result.Url);
    }

    [Fact]
    public async Task BuildAsync_TailscaleRequestWithoutApprovedBrowserCredential_FailsClosed()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            tailscaleGatewayId: "gateway-1"));

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal("localized:DashboardLink_NoBrowserCompatibleCredential", result.Error);
    }

    [Fact]
    public async Task BuildAsync_RevalidationException_PreservesFallbackAndSanitizesDiagnostic()
    {
        var service = CreateService((_, _) =>
            throw new InvalidOperationException("sensitive probe detail"));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal("localized:DashboardLink_TailscaleRevalidationFailed", result.RevalidationError);
        Assert.DoesNotContain("sensitive", result.RevalidationError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BuildAsync_AppendRequestedWithoutCredential_FailsClosed(
        string? browserCredential)
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            browserCredential: browserCredential,
            tailscaleGatewayId: "gateway-1"));

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal("localized:DashboardLink_NoBrowserCompatibleCredential", result.Error);
    }

    [Fact]
    public async Task BuildAsync_CallerCancellation_PropagatesWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService((_, cancellationToken) =>
            Task.FromCanceled<bool>(cancellationToken));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"), cts.Token));
    }

    [Fact]
    public async Task BuildAsync_NonCallerCancellation_PreservesFallbackAndSanitizesDiagnostic()
    {
        var service = CreateService((_, _) =>
            Task.FromException<bool>(new OperationCanceledException("sensitive probe detail")));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal("localized:DashboardLink_TailscaleRevalidationFailed", result.RevalidationError);
        Assert.DoesNotContain("sensitive", result.RevalidationError);
    }

    private static GatewayDashboardLinkService CreateService(
        Func<string, CancellationToken, Task<bool>> revalidate) =>
        new(revalidate, key => $"localized:{key}");

    private static GatewayDashboardLinkRequest Request(
        bool appendBrowserCredential = true,
        string? browserCredential = "shared-token",
        string? tailscaleGatewayId = null) => new(
            "https://gateway.example.test",
            "/settings/profile",
            browserCredential,
            appendBrowserCredential,
            tailscaleGatewayId);
}
