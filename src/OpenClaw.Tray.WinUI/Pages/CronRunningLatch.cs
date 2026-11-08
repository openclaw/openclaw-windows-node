namespace OpenClawTray.Pages;

public sealed class CronRunTokenRegistry
{
    private readonly Dictionary<string, RunToken> _tokens = new(StringComparer.Ordinal);

    public object BeginRun(string jobId)
    {
        var token = new RunToken();
        _tokens[jobId] = token;
        return token;
    }

    public bool IsCurrentRun(string jobId, object token)
        => _tokens.TryGetValue(jobId, out var currentToken)
           && ReferenceEquals(currentToken, token);

    public void SetRunId(string jobId, object token, string? runId)
    {
        if (IsCurrentRun(jobId, token))
            _tokens[jobId].RunId = string.IsNullOrWhiteSpace(runId) ? null : runId;
    }

    public bool MatchesRunId(string jobId, string? runId)
    {
        return !string.IsNullOrWhiteSpace(runId)
            && _tokens.TryGetValue(jobId, out var currentToken)
            && string.Equals(currentToken.RunId, runId, StringComparison.Ordinal);
    }

    public void ClearRun(string jobId)
        => _tokens.Remove(jobId);

    private sealed class RunToken
    {
        public string? RunId { get; set; }
    }
}

/// <summary>
/// A cron row is latched as running when its scheduled time passes before
/// lastRunAtMs moves. The gateway can later advance nextRunAtMs with
/// runningAtMs still zero. That row is no longer running.
/// </summary>
public static class CronRunningLatch
{
    public static bool ShouldClearBecauseNextRunMoved(
        long runningAtMs,
        long nextRunAtMs,
        long previousNextRunAtMs)
        => runningAtMs <= 0
           && previousNextRunAtMs > 0
           && nextRunAtMs > previousNextRunAtMs;
}
