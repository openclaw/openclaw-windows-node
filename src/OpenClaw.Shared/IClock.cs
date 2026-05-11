namespace OpenClaw.Shared;

/// <summary>
/// Time abstraction for testability. Replaces DateTime.UtcNow.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Production clock using <see cref="DateTime.UtcNow"/>.
/// </summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
