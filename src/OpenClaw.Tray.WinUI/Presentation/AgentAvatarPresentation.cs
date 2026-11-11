// Agent avatar presentation shared by the workspace sidebar, chat welcome, and transcript.
//
// Mirrors the Control UI identity rules so every client shows the same agent:
// the roster label wins, the Gateway identity (agent.identity.get) fills gaps,
// and an agent without an image or text avatar gets the deterministic generated face.
//
// Usage:
//   var agent = AgentAvatarPresentation.Compose(id, latestSessionKey, rosterName,
//       rosterIdentityName, rosterEmoji, rosterAvatar, rosterAvatarUrl, hydratedIdentity);
//   // Render agent.AvatarUrl, else agent.Emoji, else the generated face:
//   var face = AgentAvatarPresentation.Face(agent.Id);
using OpenClawTray.Chat;

namespace OpenClawTray.Presentation;

internal readonly record struct AgentAvatarColor(byte R, byte G, byte B);

/// <summary>Feature indexes and palette for the generated agent face (32x32 view box).</summary>
internal sealed record AgentAvatarFace(
    int Eye, int Mouth, AgentAvatarColor Background, AgentAvatarColor Pale, AgentAvatarColor Ink);

internal static class AgentAvatarPresentation
{
    // The Gateway's built-in default text avatar; clients render the generated face instead.
    private const string DefaultAssistantAvatar = "A";
    private const int MaxTextAvatarLength = 8;
    private const int FeatureVariants = 4;
    private static readonly int[] Hues = [8, 32, 48, 82, 142, 174, 202, 232, 272, 322];

    /// <summary>
    /// Combines the roster entry with the agent's resolved Gateway identity.
    /// A resolved identity owns image absence, so stale roster images cannot return.
    /// </summary>
    internal static WorkspaceAgent Compose(string id, string? latestSessionKey,
        string? rosterName, string? rosterIdentityName, string? rosterEmoji,
        string? rosterAvatar, string? rosterAvatarUrl, ChatAgentIdentity? hydrated)
    {
        if (hydrated is not null && !string.Equals(hydrated.AgentId, id, StringComparison.Ordinal))
            hydrated = null;
        var name = Normalize(rosterName) ?? Normalize(rosterIdentityName) ?? Normalize(hydrated?.DisplayName) ?? id;
        var text = TextAvatar(rosterEmoji) ?? TextAvatar(rosterAvatar) ?? TextAvatar(hydrated?.Emoji);
        var image = hydrated is not null
            ? ImageUrl(hydrated.AvatarUrl)
            : ImageUrl(rosterAvatarUrl) ?? ImageUrl(rosterAvatar);
        return new WorkspaceAgent(id, name, latestSessionKey, text, image);
    }

    /// <summary>Returns a short emoji or text avatar, or null for paths, URLs, and the default.</summary>
    internal static string? TextAvatar(string? value)
    {
        var trimmed = Normalize(value);
        if (trimmed is null || trimmed == DefaultAssistantAvatar || trimmed.Length > MaxTextAvatarLength)
            return null;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || ch is '\\' or '/' or '.' or ':' || IsInvisibleFormatting(ch))
                return null;
        }
        return trimmed;
    }

    /// <summary>Returns an image Windows can load: an inline image or a public HTTPS URL.</summary>
    internal static string? ImageUrl(string? value)
    {
        var trimmed = Normalize(value);
        if (trimmed is null)
            return null;
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? trimmed : null;
    }

    /// <summary>FNV-1a over UTF-16 code units, matching the Control UI seed.</summary>
    internal static uint Seed(string agentId)
    {
        unchecked
        {
            var hash = 0x811c9dc5u;
            foreach (var unit in agentId)
            {
                hash ^= unit;
                hash *= 0x01000193u;
            }
            return hash;
        }
    }

    internal static AgentAvatarFace Face(string agentId)
    {
        var seed = Seed(agentId);
        var hue = Hues[seed % (uint)Hues.Length];
        // Separate digits let palette and expression vary independently.
        var eyeSeed = seed / (uint)Hues.Length;
        var mouthSeed = eyeSeed / FeatureVariants;
        var pale = Hsl(hue, 0.65, 0.90);
        var background = mouthSeed / FeatureVariants % 2 == 1 ? pale : Hsl(hue, 0.58, 0.62);
        return new AgentAvatarFace((int)(eyeSeed % FeatureVariants), (int)(mouthSeed % FeatureVariants),
            background, pale, Hsl(hue, 0.55, 0.18));
    }

    private static AgentAvatarColor Hsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var sector = hue / 60;
        var secondary = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = sector switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        var match = lightness - chroma / 2;
        return new AgentAvatarColor(Channel(r + match), Channel(g + match), Channel(b + match));
    }

    private static byte Channel(double value) =>
        (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);

    private static bool IsInvisibleFormatting(char ch) =>
        ch is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2060' and <= '\u206F' or '\uFEFF';

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
