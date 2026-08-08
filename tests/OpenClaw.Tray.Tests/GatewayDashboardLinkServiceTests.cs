using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardLinkServiceTests
{
    [Fact]
    public async Task BuildAsync_UntrustedGateway_AppendsApprovedBrowserCredential()
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
    public async Task BuildAsync_TrustedTailscaleGateway_OmitsBrowserCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(true));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.True(result.TrustTailscaleAuth);
        Assert.DoesNotContain("token=", result.Url);
    }

    [Fact]
    public async Task BuildAsync_FailedRevalidation_FallsBackToApprovedBrowserCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.False(result.TrustTailscaleAuth);
        Assert.Contains("token=shared-token", result.Url);
    }

    [Fact]
    public async Task BuildAsync_FailedRevalidationWithoutFallback_FailsClosed()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            tailscaleGatewayId: "gateway-1"));

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal(GatewayDashboardLinkService.TailscaleFallbackUnavailable, result.Error);
    }

    [Fact]
    public async Task BuildAsync_RevalidationException_PreservesFallbackAndDiagnostic()
    {
        var service = CreateService((_, _) => throw new InvalidOperationException("probe failed"));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal("probe failed", result.RevalidationError);
    }

    private static GatewayDashboardLinkService CreateService(
        Func<string, CancellationToken, Task<bool>> revalidate) => new(revalidate);

    private static GatewayDashboardLinkRequest Request(
        bool appendBrowserCredential = true,
        string? tailscaleGatewayId = null) => new(
            "https://gateway.example.test",
            "/settings/profile",
            "shared-token",
            appendBrowserCredential,
            tailscaleGatewayId);
}
