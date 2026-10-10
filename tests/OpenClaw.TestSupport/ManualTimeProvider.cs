namespace OpenClaw.TestSupport;

/// <summary>A manually advanced clock for deterministic one-shot deadline and polling tests.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private long _ticks;
    public event Action<TimeSpan>? TimerScheduled;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_timers)
        {
            timer.Change(dueTime, period);
            _timers.Add(timer);
        }
        TimerScheduled?.Invoke(dueTime);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        Timer[] timers;
        lock (_timers)
        {
            _ticks += elapsed.Ticks;
            timers = _timers.ToArray();
        }
        foreach (var timer in timers)
            timer.FireIfDue();
    }

    private sealed class Timer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("Only one-shot timers are supported.");
            lock (clock._timers)
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
            return true;
        }
        public void FireIfDue()
        {
            lock (clock._timers)
            {
                if (clock._ticks < _due)
                    return;
                _due = long.MaxValue;
            }
            callback(state);
        }
        public void Dispose()
        {
            lock (clock._timers)
            {
                _due = long.MaxValue;
                clock._timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
