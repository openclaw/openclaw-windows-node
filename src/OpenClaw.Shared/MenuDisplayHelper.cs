namespace OpenClaw.Shared;

/// <summary>
/// Pure helper methods for tray menu display formatting.
/// </summary>
public static class MenuDisplayHelper
{
    public static string GetStatusIcon(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "✅",
        ConnectionStatus.Connecting => "🔄",
        ConnectionStatus.Error => "❌",
        _ => "⚪"
    };

    public static string GetChannelStatusIcon(string? status)
    {
        if (ChannelHealth.IsHealthyStatus(status)) return "🟢";
        if (ChannelHealth.IsIntermediateStatus(status)) return "🟡";
        if (string.IsNullOrEmpty(status)) return "⚪";
        return "🔴";
    }

    public static string TruncateText(string? text, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }

    public static string FormatProviderSummary(int count)
    {
        return $"{count} provider{(count == 1 ? "" : "s")} active";
    }

    public static string GetNextToggleValue(string? current)
    {
        return string.Equals(current, "on", StringComparison.OrdinalIgnoreCase) ? "off" : "on";
    }
}
