namespace OpenClawTray.Chat.Explorations;

/// <summary>
/// Snapshot bundle of every visual setting tuned by a single <see cref="ChatVariation"/>
/// preset. <see cref="Apply"/> writes the bundle back to <see cref="ChatExplorationState"/>
/// in one shot so subscribers re-render only once. Mirrors the <c>OnVisualChanged</c>
/// preset block in v2 <c>ChatExplorationPreview.xaml.cs</c>.
/// </summary>
public sealed record ChatVariationPreset(
    double BubbleCornerRadius,
    double Gutter,
    double MessageGap,
    ChatPaddingDensity PaddingDensity,
    double ComposerCornerRadius,
    double ComposerIconSize,
    double SendButtonSize,
    bool   ShowAvatars,
    bool   ShowTimestamps);

public static class ChatVariationPresets
{
    /// <summary>Acrylic backdrop, large rounded bubbles, cozy spacing.
    /// Matches the shipping default look (see ChatExplorationState code defaults).</summary>
    public static readonly ChatVariationPreset Calm = new(
        BubbleCornerRadius:   16,
        Gutter:               64,
        MessageGap:           12,
        PaddingDensity:       ChatPaddingDensity.Cozy,
        ComposerCornerRadius: 8,
        ComposerIconSize:     16,
        SendButtonSize:       40,
        ShowAvatars:          true,
        ShowTimestamps:       true);

    /// <summary>Acrylic look-alike, small bubbles, tight spacing.</summary>
    public static readonly ChatVariationPreset Compact = new(
        BubbleCornerRadius:   10,
        Gutter:               24,
        MessageGap:           6,
        PaddingDensity:       ChatPaddingDensity.Compact,
        ComposerCornerRadius: 6,
        ComposerIconSize:     12,
        SendButtonSize:       28,
        ShowAvatars:          true,
        ShowTimestamps:       false);

    /// <summary>Solid surface, no bubble fill, thin accent left stroke + larger typography.</summary>
    public static readonly ChatVariationPreset Plain = new(
        BubbleCornerRadius:   4,
        Gutter:               40,
        MessageGap:           18,
        PaddingDensity:       ChatPaddingDensity.Cozy,
        ComposerCornerRadius: 4,
        ComposerIconSize:     14,
        SendButtonSize:       32,
        ShowAvatars:          false,
        ShowTimestamps:       true);

    public static ChatVariationPreset For(ChatVariation variation) => variation switch
    {
        ChatVariation.Compact => Compact,
        ChatVariation.Plain   => Plain,
        _                     => Calm,
    };

    /// <summary>
    /// Apply <paramref name="variation"/>'s preset bundle to
    /// <see cref="ChatExplorationState"/>. Each setter raises <see cref="ChatExplorationState.Changed"/>
    /// individually — subscribers may receive several Changed events in sequence.
    /// (We accept the small re-render cost in exchange for keeping ChatExplorationState
    /// free of batching APIs.)
    /// </summary>
    public static void Apply(ChatVariation variation)
    {
        var p = For(variation);
        ChatExplorationState.Variation           = variation;
        ChatExplorationState.BubbleCornerRadius  = p.BubbleCornerRadius;
        ChatExplorationState.Gutter              = p.Gutter;
        ChatExplorationState.MessageGap          = p.MessageGap;
        ChatExplorationState.PaddingDensity      = p.PaddingDensity;
        ChatExplorationState.ComposerCornerRadius = p.ComposerCornerRadius;
        ChatExplorationState.ComposerIconSize    = p.ComposerIconSize;
        ChatExplorationState.SendButtonSize      = p.SendButtonSize;
        ChatExplorationState.ShowAvatars         = p.ShowAvatars;
        ChatExplorationState.ShowTimestamps      = p.ShowTimestamps;
    }
}
