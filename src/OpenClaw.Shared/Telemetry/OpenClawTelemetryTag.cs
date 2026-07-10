namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Explicit non-sensitive telemetry tag supplied by instrumentation call sites.
/// </summary>
public sealed class OpenClawTelemetryTag
{
    public OpenClawTelemetryTag(OpenClawTelemetryTagKey key, object? value)
        : this(key.ToTelemetryName(), value)
    {
    }

    private OpenClawTelemetryTag(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Telemetry tag key cannot be empty.", nameof(key));

        (Key, Value) = (key, value);
    }

    public string Key { get; }
    public object? Value { get; }

    public static OpenClawTelemetryTag String(OpenClawTelemetryTagKey key, string? value) =>
        new(key, value);

    public static OpenClawTelemetryTag String(string localKey, string? value) =>
        new(localKey, value);

    public static OpenClawTelemetryTag Bool(OpenClawTelemetryTagKey key, bool value) =>
        new(key, value);

    public static OpenClawTelemetryTag Bool(string localKey, bool value) =>
        new(localKey, value);

    public static OpenClawTelemetryTag Number(OpenClawTelemetryTagKey key, long value) =>
        new(key, value);

    public static OpenClawTelemetryTag Number(string localKey, long value) =>
        new(localKey, value);
}
