namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Supplies distinct ranges and gateway timestamps for synthetic Usage proof.
/// </summary>
public sealed class UsagePeriodFixture
{
    public const string Name = "usage-period-legacy";
    public const string SwitchName = "usage-period-switch";
    public enum GatewayMode { Legacy, A, B }

    private int _costRequestCount;
    private int _matchingSevenEnabled;
    public GatewayMode Mode { get; }
    public string ScenarioName => Mode == GatewayMode.Legacy ? Name : SwitchName;

    public UsagePeriodFixture(GatewayMode mode = GatewayMode.Legacy) => Mode = mode;

    public void EnableMatchingSevenDays() => Interlocked.Exchange(ref _matchingSevenEnabled, 1);

    public object Cost(int requestedDays)
    {
        var days = Mode switch
        {
            GatewayMode.A => 7,
            GatewayMode.B => requestedDays == 7 && Volatile.Read(ref _matchingSevenEnabled) != 0 ? 7 : 30,
            _ => Interlocked.Increment(ref _costRequestCount) == 1 ? 7 : 30
        };
        var totalCost = Mode == GatewayMode.B ? days == 7 ? 70.00 : 300.00 : days == 7 ? 7.00 : 30.00;
        var totalTokens = days * 100L;
        return new
        {
            updatedAt = Mode switch
            {
                GatewayMode.A => new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                GatewayMode.B => new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                _ => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            days,
            daily = new[] { new { date = "2026-10-28", totalCost } },
            totals = new
            {
                input = totalTokens,
                output = 0,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens,
                totalCost,
                missingCostEntries = 0,
            },
        };
    }
}
