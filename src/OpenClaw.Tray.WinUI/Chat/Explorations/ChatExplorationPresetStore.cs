using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI;

namespace OpenClawTray.Chat.Explorations;

/// <summary>
/// 사용자가 패널에서 만든 ChatExplorationState 스냅샷을 이름 붙여 저장/불러오기.
/// %APPDATA%\OpenClawTray\chat-exploration-presets.json 에 영속화.
/// 빌트인 (Calm/Compact/Plain) 은 코드 (ChatVariationPresets), 커스텀은 JSON.
/// </summary>
public sealed record ChatExplorationPreset
{
    public string Name { get; init; } = "";

    /// <summary>
    /// When true, this preset is auto-applied on app startup. Only one preset
    /// can be the default at a time; the panel enforces this by clearing the
    /// flag on all other presets when one is marked.
    /// </summary>
    public bool IsDefault { get; init; }

    // Surface
    public string BackdropMode { get; init; } = "Acrylic";
    public bool   UsesHostBackdrop { get; init; }
    public string PreviewTheme { get; init; } = "System";
    public string Variation { get; init; } = "Calm";

    // Bubble / Layout
    public double BubbleCornerRadius { get; init; } = 16;
    public double Gutter { get; init; } = 64;
    public double MessageGap { get; init; } = 12;
    public string PaddingDensity { get; init; } = "Cozy";
    public string UserBubbleTone { get; init; } = "Secondary";
    public bool   ShowTimestamps { get; init; } = true;
    public bool   ShowAssistantBubbles { get; init; } = true;
    public bool   ShowToolCalls { get; init; } = true;
    public double BubbleMaxWidth { get; init; } = 560;
    public double BubbleSideMargin { get; init; } = 8;

    // Footer
    public bool ShowSenderName { get; init; } = false;
    public bool ShowModelName { get; init; } = false;
    public bool ShowTokens { get; init; } = true;
    public bool ShowContextPercent { get; init; } = true;

    // Avatar
    public bool   ShowAvatars { get; init; } = true;
    public string AvatarMode { get; init; } = "AgentOnly";

    // Composer
    public string ComposerLayout { get; init; } = "ThreeRow";
    public double ComposerCornerRadius { get; init; } = 8;
    public double ComposerIconSize { get; init; } = 16;
    public double SendButtonSize { get; init; } = 40;

    // Icons
    public string SendIconGlyph   { get; init; } = "\uE724";
    public bool   SendIconShow    { get; init; } = true;
    public string AttachIconGlyph { get; init; } = "\uE723";
    public bool   AttachIconShow  { get; init; } = true;
    public string VoiceIconGlyph  { get; init; } = "\uE720";
    public bool   VoiceIconShow   { get; init; } = true;
    public string MoreIconGlyph   { get; init; } = "\uE712";
    public bool   MoreIconShow    { get; init; } = true;
    public string StopIconGlyph   { get; init; } = "\uE71A";
    public bool   StopIconShow    { get; init; } = true;

    // Brushes (#RRGGBB or null)
    public string? AccentHex { get; init; }
    public string? UserBubbleHex { get; init; }
    public string? AssistantBubbleHex { get; init; }
    public string? SendButtonHex { get; init; }
}

public static class ChatExplorationPresetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FilePath
    {
        get
        {
            // OPENCLAW_TRAY_DATA_DIR override (used by tests).
            var dir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                dir = Path.Combine(appData, "OpenClawTray");
            }
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "chat-exploration-presets.json");
        }
    }

    public static List<ChatExplorationPreset> LoadAll()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new List<ChatExplorationPreset>();
            var json = File.ReadAllText(path);
            var arr = JsonSerializer.Deserialize<List<ChatExplorationPreset>>(json, JsonOpts);
            return arr ?? new List<ChatExplorationPreset>();
        }
        catch { return new List<ChatExplorationPreset>(); }
    }

    public static void SaveAll(IEnumerable<ChatExplorationPreset> presets)
    {
        try
        {
            var json = JsonSerializer.Serialize(presets.ToList(), JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Marks the preset with <paramref name="name"/> as the new default and
    /// clears the flag on every other preset, then persists. Pass null/empty
    /// to clear the default entirely.
    /// </summary>
    public static List<ChatExplorationPreset> SetDefault(string? name)
    {
        var all = LoadAll();
        var updated = all.Select(p => p with { IsDefault = !string.IsNullOrEmpty(name) && p.Name == name }).ToList();
        SaveAll(updated);
        return updated;
    }

    /// <summary>
    /// Loads the persisted preset marked <see cref="ChatExplorationPreset.IsDefault"/>
    /// and applies it to <see cref="ChatExplorationState"/>. Best-effort — any
    /// IO/parse failure is swallowed and the in-memory defaults remain.
    /// </summary>
    public static void ApplyDefaultIfPresent()
    {
        try
        {
            var def = LoadAll().FirstOrDefault(p => p.IsDefault);
            if (def is not null) Apply(def);
        }
        catch { /* best-effort */ }
    }

    public static ChatExplorationPreset Capture(string name) => new()
    {
        Name = name,
        BackdropMode = ChatExplorationState.BackdropMode.ToString(),
        UsesHostBackdrop = ChatExplorationState.UsesHostBackdrop,
        PreviewTheme = ChatExplorationState.PreviewTheme.ToString(),
        Variation = ChatExplorationState.Variation.ToString(),

        BubbleCornerRadius = ChatExplorationState.BubbleCornerRadius,
        Gutter = ChatExplorationState.Gutter,
        MessageGap = ChatExplorationState.MessageGap,
        PaddingDensity = ChatExplorationState.PaddingDensity.ToString(),
        UserBubbleTone = ChatExplorationState.UserBubbleTone.ToString(),
        ShowTimestamps = ChatExplorationState.ShowTimestamps,
        ShowAssistantBubbles = ChatExplorationState.ShowAssistantBubbles,
        ShowToolCalls = ChatExplorationState.ShowToolCalls,
        BubbleMaxWidth = ChatExplorationState.BubbleMaxWidth,
        BubbleSideMargin = ChatExplorationState.BubbleSideMargin,

        ShowSenderName = ChatExplorationState.ShowSenderName,
        ShowModelName = ChatExplorationState.ShowModelName,
        ShowTokens = ChatExplorationState.ShowTokens,
        ShowContextPercent = ChatExplorationState.ShowContextPercent,

        ShowAvatars = ChatExplorationState.ShowAvatars,
        AvatarMode = ChatExplorationState.AvatarMode.ToString(),

        ComposerLayout = ChatExplorationState.ComposerLayout.ToString(),
        ComposerCornerRadius = ChatExplorationState.ComposerCornerRadius,
        ComposerIconSize = ChatExplorationState.ComposerIconSize,
        SendButtonSize = ChatExplorationState.SendButtonSize,

        SendIconGlyph = ChatExplorationState.SendIconGlyph,
        SendIconShow = ChatExplorationState.SendIconShow,
        AttachIconGlyph = ChatExplorationState.AttachIconGlyph,
        AttachIconShow = ChatExplorationState.AttachIconShow,
        VoiceIconGlyph = ChatExplorationState.VoiceIconGlyph,
        VoiceIconShow = ChatExplorationState.VoiceIconShow,
        MoreIconGlyph = ChatExplorationState.MoreIconGlyph,
        MoreIconShow = ChatExplorationState.MoreIconShow,
        StopIconGlyph = ChatExplorationState.StopIconGlyph,
        StopIconShow = ChatExplorationState.StopIconShow,

        AccentHex = BrushToHex(ChatExplorationState.AccentBrushOverride),
        UserBubbleHex = BrushToHex(ChatExplorationState.UserBubbleBrushOverride),
        AssistantBubbleHex = BrushToHex(ChatExplorationState.AssistantBubbleBrushOverride),
        SendButtonHex = BrushToHex(ChatExplorationState.SendButtonBrushOverride),
    };

    public static void Apply(ChatExplorationPreset p)
    {
        if (Enum.TryParse<ChatBackdropMode>(p.BackdropMode, out var bm)) ChatExplorationState.BackdropMode = bm;
        ChatExplorationState.UsesHostBackdrop = p.UsesHostBackdrop;
        if (Enum.TryParse<ChatPreviewTheme>(p.PreviewTheme, out var pt)) ChatExplorationState.PreviewTheme = pt;
        if (Enum.TryParse<ChatVariation>(p.Variation, out var v)) ChatExplorationState.Variation = v;

        ChatExplorationState.BubbleCornerRadius = p.BubbleCornerRadius;
        ChatExplorationState.Gutter = p.Gutter;
        ChatExplorationState.MessageGap = p.MessageGap;
        if (Enum.TryParse<ChatPaddingDensity>(p.PaddingDensity, out var pd)) ChatExplorationState.PaddingDensity = pd;
        if (Enum.TryParse<ChatUserBubbleTone>(p.UserBubbleTone, out var ut)) ChatExplorationState.UserBubbleTone = ut;
        ChatExplorationState.ShowTimestamps = p.ShowTimestamps;
        ChatExplorationState.ShowAssistantBubbles = p.ShowAssistantBubbles;
        ChatExplorationState.ShowToolCalls = p.ShowToolCalls;
        ChatExplorationState.BubbleMaxWidth = p.BubbleMaxWidth;
        ChatExplorationState.BubbleSideMargin = p.BubbleSideMargin;

        ChatExplorationState.ShowSenderName = p.ShowSenderName;
        ChatExplorationState.ShowModelName = p.ShowModelName;
        ChatExplorationState.ShowTokens = p.ShowTokens;
        ChatExplorationState.ShowContextPercent = p.ShowContextPercent;

        ChatExplorationState.ShowAvatars = p.ShowAvatars;
        if (Enum.TryParse<ChatAvatarMode>(p.AvatarMode, out var am)) ChatExplorationState.AvatarMode = am;

        if (Enum.TryParse<ChatComposerLayout>(p.ComposerLayout, out var cl)) ChatExplorationState.ComposerLayout = cl;
        ChatExplorationState.ComposerCornerRadius = p.ComposerCornerRadius;
        ChatExplorationState.ComposerIconSize = p.ComposerIconSize;
        ChatExplorationState.SendButtonSize = p.SendButtonSize;

        ChatExplorationState.SendIconGlyph = p.SendIconGlyph;
        ChatExplorationState.SendIconShow = p.SendIconShow;
        ChatExplorationState.AttachIconGlyph = p.AttachIconGlyph;
        ChatExplorationState.AttachIconShow = p.AttachIconShow;
        ChatExplorationState.VoiceIconGlyph = p.VoiceIconGlyph;
        ChatExplorationState.VoiceIconShow = p.VoiceIconShow;
        ChatExplorationState.MoreIconGlyph = p.MoreIconGlyph;
        ChatExplorationState.MoreIconShow = p.MoreIconShow;
        ChatExplorationState.StopIconGlyph = p.StopIconGlyph;
        ChatExplorationState.StopIconShow = p.StopIconShow;

        ChatExplorationState.AccentBrushOverride = HexToBrush(p.AccentHex);
        ChatExplorationState.UserBubbleBrushOverride = HexToBrush(p.UserBubbleHex);
        ChatExplorationState.AssistantBubbleBrushOverride = HexToBrush(p.AssistantBubbleHex);
        ChatExplorationState.SendButtonBrushOverride = HexToBrush(p.SendButtonHex);
    }

    public static string? BrushToHex(Brush? b) =>
        b is SolidColorBrush scb ? $"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}" : null;

    public static Brush? HexToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        try
        {
            byte r  = Convert.ToByte(s.Substring(0, 2), 16);
            byte g  = Convert.ToByte(s.Substring(2, 2), 16);
            byte bl = Convert.ToByte(s.Substring(4, 2), 16);
            return new SolidColorBrush(Color.FromArgb(0xFF, r, g, bl));
        }
        catch { return null; }
    }
}
