namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Explicit non-sensitive telemetry tag supplied by instrumentation call sites.
/// </summary>
public sealed class OpenClawTelemetryTag
{
    private const int DefaultStringMaxLength = 128;

    public OpenClawTelemetryTag(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Telemetry tag key cannot be empty.", nameof(key));

        Key = key;
        Value = value;
    }

    public string Key { get; }
    public object? Value { get; }

    public static OpenClawTelemetryTag String(string key, string? value, int maxLength = DefaultStringMaxLength) =>
        new(key, BoundString(value, maxLength));

    public static OpenClawTelemetryTag Bool(string key, bool value) =>
        new(key, value);

    public static OpenClawTelemetryTag Number(string key, long value) =>
        new(key, value);

    private static string? BoundString(string? value, int maxLength)
    {
        if (value == null)
            return null;
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum string length must be positive.");

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
