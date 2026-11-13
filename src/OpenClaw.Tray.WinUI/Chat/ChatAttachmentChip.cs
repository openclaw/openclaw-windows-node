using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Windows.Devices.Input;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// One-shot request to focus a chip's remove button after a neighboring chip was removed.
/// <see cref="Token"/> distinguishes repeated requests for the same chip.
/// </summary>
internal sealed record ChatAttachmentFocusRequest(string Key, long Token, FocusState State);

/// <param name="OnRemove">Receives the remove button's focus state at activation time.</param>
/// <param name="FocusRequest">Set only for the chip that should take focus.</param>
/// <param name="Thumbnail">Image preview; when set the attachment renders as a square tile.</param>
internal sealed record ChatAttachmentChipProps(
    string FileName,
    string RemoveLabel,
    Action<FocusState> OnRemove,
    double MaxWidth,
    ChatAttachmentFocusRequest? FocusRequest = null,
    ImageBrush? Thumbnail = null);

/// <summary>
/// Pending attachment rendered in the composer's attachment rail. Files are compact pills that
/// size to their name up to <see cref="ChatAttachmentChipProps.MaxWidth"/> and grow taller with
/// Windows text scaling. Images with a decodable preview (<see cref="ThumbnailFor"/>) are square
/// tiles with the remove button as a badge on the top-right corner. In both forms the remove
/// button keeps layout and keyboard/UIA access at all times and becomes visible on pointer hover,
/// keyboard focus, or when touch input is present. Callers key each chip with
/// <see cref="KeyFor"/> so per-chip state never moves to another attachment.
/// Usage:
/// <code>
/// Component&lt;ChatAttachmentChip, ChatAttachmentChipProps&gt;(
///         new(name, label, onRemove, maxWidth, Thumbnail: ChatAttachmentChip.ThumbnailFor(attachment)))
///     .WithKey(ChatAttachmentChip.KeyFor(attachment));
/// </code>
/// </summary>
internal sealed class ChatAttachmentChip : Component<ChatAttachmentChipProps>
{
    private static readonly ConditionalWeakTable<ChatAttachment, string> s_keys = new();
    private static readonly ConditionalWeakTable<ChatAttachment, ThumbnailSlot> s_thumbnails = new();
    private static long s_nextKey;

    private sealed record ThumbnailSlot(ImageBrush? Brush);

    // Queried once per process: pills re-render on every composer render, including each keystroke.
    private static readonly Lazy<bool> TouchPresent = new(() => new TouchCapabilities().TouchPresent != 0);

    // Everything in the pill except the name: 8/2 padding, 1/1 border, glyph, name gap, 2px gap and remove target.
    private const double ChatAttachmentChipChrome =
        8 + 2 + 2 + ChatVisuals.AttachmentGlyphSize + ChatVisuals.AttachmentChipGap + 2 + ChatVisuals.AttachmentRemoveSize;

    /// <summary>Stable reconciliation key for one pending attachment instance.</summary>
    internal static string KeyFor(ChatAttachment attachment) =>
        s_keys.GetValue(attachment, static _ =>
            $"chat-attachment-{Interlocked.Increment(ref s_nextKey)}");

    /// <summary>
    /// Cached tile preview for an image attachment, or null when it is not a raster image or does
    /// not decode (those render as file pills). Decodes once per attachment, at thumbnail size.
    /// </summary>
    internal static ImageBrush? ThumbnailFor(ChatAttachment attachment) =>
        s_thumbnails.GetValue(attachment, static candidate => new ThumbnailSlot(DecodeThumbnail(candidate))).Brush;

    private static ImageBrush? DecodeThumbnail(ChatAttachment attachment)
    {
        if (!attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || attachment.MimeType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase)
            || !ChatImagePreviewCache.TryDecodeBoundedBase64(
                attachment.Content, ChatImagePreviewCache.MaximumPreviewBytes, out var bytes))
        {
            return null;
        }

        return ChatAttachmentBitmapDecoder.TryDecode(bytes, ChatVisuals.AttachmentThumbnailDecodeSize) is { } bitmap
            ? new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill }
            : null;
    }

    public override Element Render()
    {
        var (hovered, setHovered) = UseState(false);
        var (focused, setFocused) = UseState(false);
        var removeButton = UseRef<Button?>(null);
        var handledFocusToken = UseRef(Props.FocusRequest?.Token ?? 0L);
        var touch = TouchPresent.Value;
        var thumbnail = Props.Thumbnail;

        var glyph = TextBlock(FluentIconCatalog.Document)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(ChatVisuals.AttachmentGlyphSize)
            .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw)
            .Set(text => text.IsTextScaleFactorEnabled = false);
        var name = TextBlock(Props.FileName)
            .FontSize(12)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .Foreground(Theme.Ref("ChatTextBrush"))
            .VAlign(VerticalAlignment.Center);
        var remove = Button(
                TextBlock(FluentIconCatalog.Exit)
                    .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .FontSize(10)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .AccessibilityView(AccessibilityView.Raw)
                    .Set(text => text.IsTextScaleFactorEnabled = false),
                () => Props.OnRemove(removeButton.Current?.FocusState ?? FocusState.Unfocused))
            .Resources(resources =>
            {
                ChatVisuals.ToolbarButtonResources(resources);
                // Over an image the badge needs an opaque disc; subtle fills would let the picture show through.
                if (thumbnail is not null)
                    resources.Set("ButtonBackground", Theme.Ref("ChatAttachmentBadgeBrush"))
                        .Set("ButtonBackgroundPointerOver", Theme.Ref("ChatAttachmentBadgeHoverBrush"))
                        .Set("ButtonBackgroundPressed", Theme.Ref("ChatAttachmentBadgeHoverBrush"))
                        .Set("ButtonBorderBrush", Theme.Ref("ChatStrokeBrush"))
                        .Set("ButtonBorderBrushPointerOver", Theme.Ref("ChatStrokeBrush"))
                        .Set("ButtonBorderBrushPressed", Theme.Ref("ChatStrokeBrush"));
            })
            .Width(ChatVisuals.AttachmentRemoveSize).Height(ChatVisuals.AttachmentRemoveSize)
            .MinWidth(ChatVisuals.AttachmentRemoveSize).MinHeight(ChatVisuals.AttachmentRemoveSize)
            .Padding(0).BorderThickness(thumbnail is null ? 0 : 1)
            .CornerRadius(ChatVisuals.AttachmentRemoveSize / 2)
            .Foreground(Theme.Ref(thumbnail is null ? "ChatSecondaryTextBrush" : "ChatTextBrush"))
            .AutomationName(Props.RemoveLabel)
            .ToolTip(Props.RemoveLabel)
            // Opacity keeps the target in layout and in the keyboard/UIA tree while hidden.
            .Opacity(hovered || focused || touch ? 1 : 0)
            .Set(button =>
            {
                removeButton.Current = button;
                ComposerAutomationVisibility.Prepare(button);
                var request = Props.FocusRequest;
                var token = request?.Token ?? 0L;
                if (handledFocusToken.Current == token)
                    return;
                handledFocusToken.Current = token;
                if (request is not null)
                    button.DispatcherQueue.TryEnqueue(() => button.Focus(request.State));
            })
            .OnUnmount(control => ComposerAutomationVisibility.Detach((FrameworkElement)control));

        if (thumbnail is not null)
        {
            // The badge sits on the tile's top-right corner inside the cell, so no negative margin is
            // clipped by the rail's scroller.
            var inset = ChatVisuals.AttachmentThumbnailBadgeInset;
            return Grid([GridSize.Auto], [GridSize.Auto],
                    Border(Empty())
                        .Size(ChatVisuals.AttachmentThumbnailSize, ChatVisuals.AttachmentThumbnailSize)
                        .Margin(0, inset, inset, 0)
                        .CornerRadius(ChatVisuals.SurfaceRadius)
                        .BorderThickness(1)
                        .BorderBrush(Theme.Ref("ChatStrokeBrush"))
                        .Background(thumbnail)
                        .AccessibilityView(AccessibilityView.Raw),
                    remove.HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Top))
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .HAlign(HorizontalAlignment.Left)
                .ToolTip(Props.FileName)
                .AutomationId("ChatAttachmentImage")
                .OnPointerEntered((_, _) => setHovered(true))
                .OnPointerExited((_, _) => setHovered(false))
                .OnGotFocus((_, args) => setFocused(
                    args.OriginalSource is not Control { FocusState: FocusState.Pointer }))
                .OnLostFocus((_, _) => setFocused(false));
        }

        return Border(Grid(
                // Auto columns keep the pill's measured width equal to its content. A star name column
                // let flex measurement squeeze short names into an ellipsis; the name's MaxWidth trims instead.
                [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                glyph.Grid(column: 0),
                name.MaxWidth(Math.Max(0, Props.MaxWidth - ChatAttachmentChipChrome))
                    .Margin(ChatVisuals.AttachmentChipGap, 0, 0, 0).Grid(column: 1),
                remove.VAlign(VerticalAlignment.Center).Margin(2, 0, 0, 0).Grid(column: 2)))
            // A minimum, not a fixed height, so scaled file names grow the pill instead of clipping.
            .MinHeight(ChatVisuals.AttachmentChipHeight)
            .MaxWidth(Props.MaxWidth)
            .Padding(8, 0, 2, 0)
            .CornerRadius(ChatVisuals.AttachmentChipHeight / 2)
            .BorderThickness(1)
            .Background(Theme.Ref("ChatAttachmentChipBrush"))
            .BorderBrush(Theme.Ref("ChatAttachmentChipStrokeBrush"))
            .HAlign(HorizontalAlignment.Left)
            .ToolTip(Props.FileName)
            .AutomationId("ChatAttachmentChip")
            .OnPointerEntered((_, _) => setHovered(true))
            .OnPointerExited((_, _) => setHovered(false))
            // Pointer focus (for example a mouse removal handing focus to this pill) is not a reveal
            // reason: hover already covers the mouse, and the button would stay visible after it leaves.
            .OnGotFocus((_, args) => setFocused(
                args.OriginalSource is not Control { FocusState: FocusState.Pointer }))
            .OnLostFocus((_, _) => setFocused(false));
    }
}
