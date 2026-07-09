namespace OpenClaw.Shared;

public enum DeviceTokenReadStatus
{
    Resolved,
    Missing,
    Unreadable,
    Corrupt
}

public sealed record DeviceTokenReadResult(
    string? Token,
    DeviceTokenReadStatus Status,
    string? Detail = null)
{
    public static DeviceTokenReadResult Resolved(string token) =>
        new(token, DeviceTokenReadStatus.Resolved);

    public static DeviceTokenReadResult Missing(string? detail = null) =>
        new(null, DeviceTokenReadStatus.Missing, detail);

    public static DeviceTokenReadResult Unreadable(string detail) =>
        new(null, DeviceTokenReadStatus.Unreadable, detail);

    public static DeviceTokenReadResult Corrupt(string detail) =>
        new(null, DeviceTokenReadStatus.Corrupt, detail);
}
