using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class HttpUrlRiskEvaluatorTests
{
    [Fact]
    public void Evaluate_PublicHttpsHost_DoesNotRequireConfirmation()
    {
        var risk = HttpUrlRiskEvaluator.Evaluate("https://example.com/path?q=1");

        Assert.False(risk.RequiresConfirmation);
        Assert.Equal("https://example.com/", risk.CanonicalOrigin);
        Assert.Equal("example.com", risk.HostKey);
    }

    [Theory]
    [InlineData("http://example.com/", "HTTPS")]
    [InlineData("https://127.0.0.1:8080/", "loopback")]
    [InlineData("https://192.168.1.1/", "private")]
    [InlineData("https://router/", "no dot")]
    [InlineData("https://8.8.8.8/", "IP literal")]
    public void Evaluate_HighRiskUrls_RequireConfirmation(string url, string reasonFragment)
    {
        var risk = HttpUrlRiskEvaluator.Evaluate(url);

        Assert.True(risk.RequiresConfirmation);
        Assert.Contains(risk.Reasons, reason => reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }
}
