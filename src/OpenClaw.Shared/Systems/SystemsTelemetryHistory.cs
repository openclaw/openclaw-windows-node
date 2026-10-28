namespace OpenClaw.Shared.Systems;

public sealed record TelemetrySample(DateTimeOffset At, HostMeasurements Stats);

public readonly record struct TelemetryPoint(DateTimeOffset At, double Value);

/// <summary>
/// Per-machine measurement history for sparklines. A gap longer than the stale
/// window restarts the series so the chart never bridges missing reports.
/// </summary>
public sealed class SystemsTelemetryHistory
{
    public const int SampleLimit = 120;
    public static readonly TimeSpan GatewayStaleAfter = TimeSpan.FromSeconds(30);
    // Nodes publish once a minute; allow one missed report before marking their data stale.
    public static readonly TimeSpan NodeStaleAfter = TimeSpan.FromSeconds(120);

    private readonly Dictionary<string, List<TelemetrySample>> _history = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public static TimeSpan StaleAfter(string id) =>
        id == SystemsProjection.GatewayId ? GatewayStaleAfter : NodeStaleAfter;

    /// <summary>Records each row's newest measurement and forgets rows no longer listed.</summary>
    public void Record(IEnumerable<SystemRow> rows)
    {
        lock (_gate)
        {
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                current.Add(row.Id);
                if (row.Measurements is not { } stats || row.MeasuredAt is not { } at)
                {
                    if (row.Measurements is null)
                        _history.Remove(row.Id);
                    continue;
                }
                if (!_history.TryGetValue(row.Id, out var list))
                    _history[row.Id] = list = new List<TelemetrySample>();
                var last = list.Count > 0 ? list[^1] : null;
                // node.list can return the same report across several polls.
                if (last is not null && at <= last.At)
                    continue;
                if (last is not null && at - last.At > StaleAfter(row.Id))
                    list.Clear();
                list.Add(new TelemetrySample(at, stats));
                if (list.Count > SampleLimit)
                    list.RemoveRange(0, list.Count - SampleLimit);
            }
            foreach (var id in _history.Keys.Where(id => !current.Contains(id)).ToArray())
                _history.Remove(id);
        }
    }

    public IReadOnlyList<TelemetrySample> For(string id)
    {
        lock (_gate)
            return _history.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<TelemetrySample>();
    }

    public void Clear()
    {
        lock (_gate)
            _history.Clear();
    }

    /// <summary>Drops the history of every machine whose id matches.</summary>
    public void Forget(Func<string, bool> match)
    {
        lock (_gate)
        {
            foreach (var id in _history.Keys.Where(match).ToArray())
                _history.Remove(id);
        }
    }

    /// <summary>
    /// Values for one metric. A sample missing the metric restarts the series so a
    /// line never connects across a gap.
    /// </summary>
    /// <summary>
    /// Values with their sample times for one metric. A sample missing the metric restarts
    /// the series so a line never connects across a gap.
    /// </summary>
    public static IReadOnlyList<TelemetryPoint> TimedSeries(IReadOnlyList<TelemetrySample> history, Func<HostMeasurements, double?> read)
    {
        var values = new List<TelemetryPoint>(history.Count);
        foreach (var sample in history)
        {
            if (read(sample.Stats) is { } value && double.IsFinite(value))
                values.Add(new TelemetryPoint(sample.At, value));
            else
                values.Clear();
        }
        return values;
    }

    /// <summary>
    /// True when the shown measurements are a last-known report rather than live data:
    /// disconnected, offline, a failed refresh, or an old sample.
    /// </summary>
    public static bool IsLastKnown(SystemRow row, bool connected, bool refreshFailed, DateTimeOffset now) =>
        !connected ||
        row.Status != SystemStatus.Online ||
        refreshFailed ||
        (row.MeasuredAt is { } at && now - at > StaleAfter(row.Id));

    /// <summary>"Collecting history" shows while a live series has fewer than two points.</summary>
    public static bool IsCollecting(int sampleCount, bool lastKnown) => !lastKnown && sampleCount < 2;
}
