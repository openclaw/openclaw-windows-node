using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
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
internal sealed record ChatAttachmentChipProps(
    string FileName,
    string RemoveLabel,
    Action<FocusState> OnRemove,
    double MaxWidth,
    ChatAttachmentFocusRequest? FocusRequest = null);

/// <summary>
/// Compact pending-attachment pill rendered in the composer's attachment rail.
/// The pill sizes to its file name up to <see cref="ChatAttachmentChipProps.MaxWidth"/> and
/// grows taller with Windows text scaling. Its remove button keeps layout and keyboard/UIA
/// access at all times and becomes visible on pointer hover, keyboard focus, or when touch
/// input is present. Callers key each chip with <see cref="KeyFor"/> so per-chip state never
/// moves to another attachment.
/// Usage:
/// <code>
/// Component&lt;ChatAttachmentChip, ChatAttachmentChipProps&gt;(new(name, label, onRemove, maxWidth))
///     .WithKey(ChatAttachmentChip.KeyFor(attachment));
/// </code>
/// </summary>
internal sealed class ChatAttachmentChip : Component<ChatAttachmentChipProps>
{
    private static readonly ConditionalWeakTable<ChatAttachment, string> s_keys = new();
    private static long s_nextKey;

    /// <summary>Stable reconciliation key for one pending attachment instance.</summary>
    internal static string KeyFor(ChatAttachment attachment) =>
        s_keys.GetValue(attachment, static _ =>
            $"chat-attachment-{Interlocked.Increment(ref s_nextKey)}");

    public override Element Render()
    {
        var (hovered, setHovered) = UseState(false);
        var (focused, setFocused) = UseState(false);
        var removeButton = UseRef<Button?>(null);
        var handledFocusToken = UseRef(Props.FocusRequest?.Token ?? 0L);
        var touch = new TouchCapabilities().TouchPresent != 0;

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
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Width(ChatVisuals.AttachmentRemoveSize).Height(ChatVisuals.AttachmentRemoveSize)
            .MinWidth(ChatVisuals.AttachmentRemoveSize).MinHeight(ChatVisuals.AttachmentRemoveSize)
            .Padding(0).BorderThickness(0)
            .CornerRadius(ChatVisuals.AttachmentRemoveSize / 2)
            .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
            .AutomationName(Props.RemoveLabel)
            .ToolTip(Props.RemoveLabel)
            .VAlign(VerticalAlignment.Center)
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

        return Border(Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                glyph.Grid(column: 0),
                name.Margin(ChatVisuals.AttachmentChipGap, 0, 0, 0).Grid(column: 1),
                remove.Margin(2, 0, 0, 0).Grid(column: 2)))
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
            .OnGotFocus((_, _) => setFocused(true))
            .OnLostFocus((_, _) => setFocused(false));
    }
}
