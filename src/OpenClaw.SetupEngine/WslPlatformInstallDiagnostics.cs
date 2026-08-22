using System.Text.Json;

namespace OpenClaw.SetupEngine;

internal sealed record GitHubApiQuota(int Limit, int Remaining, DateTimeOffset ResetsAt)
{
    public bool IsExhausted => Remaining <= 0;
    public int Used => Math.Max(0, Limit - Remaining);
}

internal static class WslPlatformInstallDiagnostics
{
    private const string RateLimitUrl = "https://api.github.com/rate_limit";
    private const string WslStoreProductId = "9P9TQF7MRM4R";

    public static string SelfInstallInstructions =>
        "Install WSL yourself, then run setup again:" + Environment.NewLine +
        $"  Microsoft Store: {WslInstallSupport.UpdateUrl}" + Environment.NewLine +
        $"  Or run: winget install --id {WslStoreProductId} --source msstore" + Environment.NewLine +
        "  Or, in elevated PowerShell: wsl --install --no-distribution" + Environment.NewLine +
        "Reboot if Windows asks for one.";

    public static string DescribeFailure(int exitCode, GitHubApiQuota? quota)
    {
        string reason = quota is { IsExhausted: true }
            ? $"The WSL installer may need GitHub, and this network's unauthenticated API quota " +
              $"is exhausted ({quota.Used}/{quota.Limit}) until {quota.ResetsAt.ToLocalTime():HH:mm}."
            : "The WSL download did not complete. A network, policy, or installer error may be blocking it.";

        return $"WSL platform install failed with exit code {exitCode}. {reason}" +
            Environment.NewLine + Environment.NewLine + SelfInstallInstructions;
    }

    public static async Task<GitHubApiQuota?> QueryGitHubQuotaAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await QueryGitHubQuotaAsync(http, ct);
    }

    internal static async Task<GitHubApiQuota?> QueryGitHubQuotaAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RateLimitUrl);
            request.Headers.UserAgent.ParseAdd("OpenClawSetup");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            JsonElement core = json.RootElement.GetProperty("resources").GetProperty("core");
            return new(
                core.GetProperty("limit").GetInt32(),
                core.GetProperty("remaining").GetInt32(),
                DateTimeOffset.FromUnixTimeSeconds(core.GetProperty("reset").GetInt64()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
