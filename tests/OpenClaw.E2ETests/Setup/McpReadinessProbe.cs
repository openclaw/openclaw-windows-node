// Observes one token-authenticated MCP readiness attempt without recording private input.
using System.Net;
using System.Net.Http.Headers;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// Call <c>await probe.PollAsync(processExitCode)</c> from the fixture's bounded loop.
/// The fixture retains its deadline, artifact capture, and MCP-client lifetime.
/// </summary>
internal sealed class McpReadinessProbe(HttpClient http, string tokenPath, Uri healthEndpoint)
{
    public string? Token { get; private set; }
    public string LastObservation { get; private set; } = "No MCP readiness poll completed.";

    /// <summary>Returns readiness, recording only fixed descriptions, status codes, and exception types.</summary>
    public async Task<bool> PollAsync(int? processExitCode)
    {
        if (processExitCode is int exitCode)
        {
            LastObservation = $"Tray process exited before MCP server became ready (exit code {exitCode}).";
            return false;
        }

        var phase = "MCP token acquisition";
        try
        {
            if (Token is null)
            {
                if (!File.Exists(tokenPath))
                {
                    LastObservation = "MCP token file is missing.";
                    return false;
                }

                var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                if (token.Length == 0)
                {
                    LastObservation = "MCP token file is empty.";
                    return false;
                }

                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Token = token;
            }

            phase = "MCP HTTP readiness transport";
            using var response = await http.GetAsync(healthEndpoint);
            LastObservation = $"MCP HTTP readiness returned HTTP {(int)response.StatusCode}.";
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            HttpRequestException or OperationCanceledException or FormatException)
        {
            LastObservation = $"{phase} failed ({ex.GetType().Name}).";
            return false;
        }
    }
}
