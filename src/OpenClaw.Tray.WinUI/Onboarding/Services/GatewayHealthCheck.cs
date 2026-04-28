using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClawTray.Services;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Tests gateway reachability by hitting the /health HTTP endpoint.
/// Converts ws:// → http:// and wss:// → https:// for the health check.
/// </summary>
public static class GatewayHealthCheck
{
    private static readonly HttpClient s_client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public record TestResult(bool Success, string? Error = null);

    /// <summary>
    /// Builds the health check URI from a gateway WebSocket URL.
    /// Extracted for testability.
    /// </summary>
    public static bool TryBuildHealthUri(string gatewayUrl, out Uri? healthUri, out string error)
    {
        healthUri = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            error = "Gateway URL is empty";
            return false;
        }

        try
        {
            var builder = new UriBuilder(gatewayUrl);
            builder.Scheme = builder.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                _ => builder.Scheme
            };
            builder.Path = builder.Path.TrimEnd('/') + "/health";
            healthUri = builder.Uri;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid URL: {ex.Message}";
            return false;
        }
    }

    public static async Task<TestResult> TestAsync(string gatewayUrl, string? token)
    {
        if (!TryBuildHealthUri(gatewayUrl, out var healthUri, out var buildError))
            return new TestResult(false, buildError);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            using var response = await s_client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // SECURITY: Don't expose HTTP status details — they reveal gateway software info
                return new TestResult(false, "Gateway returned an error response");
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

            return ok
                ? new TestResult(true)
                : new TestResult(false, "Gateway responded but health check failed");
        }
        catch (TaskCanceledException)
        {
            return new TestResult(false, "Connection timed out (5s)");
        }
        catch (HttpRequestException ex)
        {
            // SECURITY: Log full exception, return generic message
            Logger.Warn($"[HealthCheck] Request failed: {ex.Message}");
            return new TestResult(false, "Cannot reach gateway — check URL and network");
        }
    }
}
