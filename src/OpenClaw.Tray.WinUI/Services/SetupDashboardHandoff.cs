namespace OpenClawTray.Services;

/// <summary>Restart/IPC carries only an opaque pending-record handle, never a verification receipt.</summary>
internal static class SetupDashboardHandoff
{
    internal const string NativePrefix = "ai-v3:";
    internal const string Route = "setup-dashboard";

    public static bool IsHandoffArgument(string? value) =>
        value?.StartsWith("ai-v", StringComparison.Ordinal) == true;

    public static string? ParseHandle(string? value) =>
        value is { Length: 70 } && value.StartsWith(NativePrefix, StringComparison.Ordinal) &&
        value.AsSpan(NativePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0 ? value : null;
}
