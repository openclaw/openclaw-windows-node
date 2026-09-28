namespace OpenClawTray.Helpers;

/// <summary>Keeps browser-owned authentication alive across native hide/show events.</summary>
public sealed class PairedChatNavigation
{
    private string? _lastUrl;
    public bool ShouldNavigate(string url, bool force = false) => force || !string.Equals(url, _lastUrl, StringComparison.Ordinal);
    public void RecordNavigation(string url) => _lastUrl = url;

    public static string ResolveHandoff(string chatUrl, string? handoff)
    {
        if (!Uri.TryCreate(chatUrl, UriKind.Absolute, out var chat) ||
            !Uri.TryCreate(handoff, UriKind.Absolute, out var candidate) ||
            chat.Scheme != candidate.Scheme || chat.Authority != candidate.Authority ||
            !string.IsNullOrEmpty(candidate.UserInfo)) return chatUrl;
        var fields = candidate.Fragment.TrimStart('#').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToArray();
        if (!fields.Any(pair => pair[0] == "bootstrapToken" && pair[1].Length > 0)) return chatUrl;
        var gateway = fields.FirstOrDefault(pair => pair[0] == "gatewayUrl");
        if (gateway is not null)
        {
            if (!Uri.TryCreate(Uri.UnescapeDataString(gateway[1]), UriKind.Absolute, out var ws) ||
                ws.Authority != chat.Authority ||
                ws.Scheme != (chat.Scheme == "https" ? "wss" : "ws")) return chatUrl;
        }
        return candidate.AbsoluteUri;
    }
}
