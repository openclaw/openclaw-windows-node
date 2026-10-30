using System.Text.Json;
using OpenClaw.Shared.Usage;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private const string OperatorReadScope = "operator.read";

    // sessions.usage* are registered upstream with advertise:false, so hello-ok never lists
    // them. Support is learned from the first unknown-method reply and reset per connection.
    private volatile bool _sessionsUsageUnsupported;
    private volatile bool _sessionsUsageOverviewUnsupported;
    private volatile bool _sessionsUsageTimeseriesUnsupported;
    private volatile bool _sessionsUsageLogsUnsupported;

    /// <summary>
    /// Reads <c>sessions.usage</c>. Released Gateways (2026.10.7) reject the server-side overview
    /// parameters, so on that rejection the read is retried once without them and later reads on the
    /// same connection skip them. Those replies carry no <see cref="SessionsUsageReport.Overview"/>.
    /// A Gateway that still rejects a parameter has an older schema; it is treated like one without
    /// <c>sessions.usage</c>, so callers fall back to <c>usage.cost</c>.
    /// </summary>
    public async Task<UsageReadResult<SessionsUsageReport>> GetSessionsUsageAsync(
        SessionsUsageQuery query, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_sessionsUsageUnsupported)
            return UsageReadResult<SessionsUsageReport>.Unsupported();
        if (_sessionsUsageOverviewUnsupported && query.UsesServerOverview)
            query = query.WithoutServerOverview();
        var result = await ReadSessionsUsageAsync(query, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (query.UsesServerOverview && IsRejectedParameter(result))
        {
            _sessionsUsageOverviewUnsupported = true;
            _logger.Info("sessions.usage overview unsupported on gateway; using full rows");
            result = await ReadSessionsUsageAsync(query.WithoutServerOverview(), timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        if (!IsRejectedParameter(result))
            return result;
        _sessionsUsageUnsupported = true;
        _logger.Warn("sessions.usage parameters unsupported on gateway; using usage.cost");
        return UsageReadResult<SessionsUsageReport>.Unsupported();
    }

    private static bool IsRejectedParameter(UsageReadResult<SessionsUsageReport> result) =>
        result.Status == UsageReadStatus.Failed && IsUnexpectedParameterError("sessions.usage", result.Error);

    private Task<UsageReadResult<SessionsUsageReport>> ReadSessionsUsageAsync(
        SessionsUsageQuery query, int timeoutMs, CancellationToken cancellationToken) =>
        ReadUsageAsync(
            "sessions.usage", query.ToParameters(), timeoutMs, UsageJsonParser.ParseSessionsUsage,
            () => _sessionsUsageUnsupported = true, cancellationToken);

    // Same check upstream clients use for schema-rejected optional parameters.
    internal static bool IsUnexpectedParameterError(string method, string? message) =>
        message is not null
        && message.Contains($"invalid {method} params", StringComparison.Ordinal)
        && message.Contains("unexpected property", StringComparison.Ordinal);

    public async Task<UsageReadResult<SessionUsageTimeseries>> GetSessionUsageTimeseriesAsync(
        string key, string? agentId = null, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_sessionsUsageTimeseriesUnsupported)
            return UsageReadResult<SessionUsageTimeseries>.Unsupported();
        var parameters = new Dictionary<string, object?> { ["key"] = key };
        if (!string.IsNullOrWhiteSpace(agentId)) parameters["agentId"] = agentId;
        return await ReadUsageAsync(
            "sessions.usage.timeseries", parameters, timeoutMs, UsageJsonParser.ParseTimeseries,
            () => _sessionsUsageTimeseriesUnsupported = true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageReadResult<IReadOnlyList<SessionUsageLogEntry>>> GetSessionUsageLogsAsync(
        string key, string? agentId = null, int limit = 1000, int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_sessionsUsageLogsUnsupported)
            return UsageReadResult<IReadOnlyList<SessionUsageLogEntry>>.Unsupported();
        var parameters = new Dictionary<string, object?> { ["key"] = key, ["limit"] = Math.Clamp(limit, 1, 1000) };
        if (!string.IsNullOrWhiteSpace(agentId)) parameters["agentId"] = agentId;
        return await ReadUsageAsync(
            "sessions.usage.logs", parameters, timeoutMs, UsageJsonParser.ParseLogs,
            () => _sessionsUsageLogsUnsupported = true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Response-aware <c>usage.status</c> read for the Usage page. Independent of the tracked
    /// <see cref="RequestUsageStatusAsync"/> event path that feeds the tray menu.
    /// </summary>
    public Task<UsageReadResult<ProviderUsageSummary>> GetProviderUsageAsync(
        int timeoutMs = 30000, CancellationToken cancellationToken = default) =>
        ReadUsageAsync(
            "usage.status", null, timeoutMs, UsageJsonParser.ParseProviderUsage,
            static () => { }, cancellationToken);

    /// <summary>
    /// Response-aware <c>usage.cost</c> read, used only when <c>sessions.usage</c> is unsupported.
    /// Independent of the tracked <see cref="RequestUsageCostAsync"/> event path.
    /// </summary>
    public Task<UsageReadResult<SessionsUsageReport>> GetUsageCostSummaryAsync(
        int days, int timeoutMs = 30000, CancellationToken cancellationToken = default) =>
        ReadUsageAsync(
            "usage.cost", new { days = Math.Clamp(days, 1, 366) }, timeoutMs, UsageJsonParser.ParseCostSummary,
            static () => { }, cancellationToken);

    private async Task<UsageReadResult<T>> ReadUsageAsync<T>(
        string method, object? parameters, int timeoutMs, Func<JsonElement, T> parse, Action markUnsupported,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!IsConnected || !HasHandshakeSnapshot)
            return UsageReadResult<T>.Disconnected();
        try
        {
            var payload = await SendWizardRequestAsync(method, parameters, timeoutMs)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return UsageReadResult<T>.Ok(parse(payload));
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            markUnsupported();
            _logger.Warn($"{method} unsupported on gateway");
            return UsageReadResult<T>.Unsupported();
        }
        catch (InvalidOperationException ex) when (IsMissingScopeError(ex.Message, OperatorReadScope))
        {
            return UsageReadResult<T>.Forbidden(ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(HandshakePendingError, StringComparison.Ordinal))
        {
            return UsageReadResult<T>.Disconnected();
        }
        catch (GatewayConnectionLostException)
        {
            return UsageReadResult<T>.Disconnected();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client cancels pending requests when the socket errors or is disposed.
            return UsageReadResult<T>.Disconnected();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Log the method and error class only; the message can echo request filters.
            _logger.Warn($"{method} failed: {ex.GetType().Name}");
            return UsageReadResult<T>.Failed(ex.Message);
        }
    }
}
