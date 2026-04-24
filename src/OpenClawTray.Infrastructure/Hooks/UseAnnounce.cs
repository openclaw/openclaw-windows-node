using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Handle returned by <see cref="UseAnnounceExtensions.UseAnnounce"/>.
/// Provides an imperative <see cref="Announce"/> method for screen reader
/// live-region announcements plus a zero-size <see cref="Region"/> element
/// that must be included somewhere in the component's rendered tree.
/// </summary>
public sealed class AnnounceHandle
{
    private TextBlock? _textBlock;

    /// <summary>
    /// A zero-size, invisible Reactor element that acts as the live-region anchor.
    /// Include this anywhere in your component tree (it renders nothing visible).
    /// </summary>
    public Element Region { get; }

    internal AnnounceHandle()
    {
        Region = new AnnounceRegionElement(this);
    }

    internal void SetTextBlock(TextBlock tb) => _textBlock = tb;

    /// <summary>
    /// Announces a message to screen readers (polite — queued after current speech).
    /// </summary>
    public void Announce(string message) => Announce(message, assertive: false);

    /// <summary>
    /// Announces a message to screen readers.
    /// </summary>
    /// <param name="message">The text to announce.</param>
    /// <param name="assertive">
    /// If true, interrupts current speech immediately.
    /// If false (default), queued after current speech finishes.
    /// </param>
    public void Announce(string message, bool assertive)
    {
        if (_textBlock is null) return;

        // Primary path: RaiseNotificationEvent (WinUI 1.4+, best Narrator/NVDA support).
        var peer = FrameworkElementAutomationPeer.FromElement(_textBlock);
        if (peer is not null)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                assertive
                    ? AutomationNotificationProcessing.ImportantAll
                    : AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "ReactorAnnounce");
            return;
        }

        // Fallback: update the live-region TextBlock text. Screen readers that
        // monitor LiveSetting changes will pick this up.
        _textBlock.Text = message;
    }
}

/// <summary>
/// Internal Reactor element that mounts a hidden TextBlock with LiveSetting for announcements.
/// </summary>
internal record AnnounceRegionElement(AnnounceHandle Handle) : Element;

/// <summary>
/// Extension methods for the UseAnnounce hook.
/// </summary>
public static class UseAnnounceExtensions
{
    /// <summary>
    /// Creates an <see cref="AnnounceHandle"/> for making screen reader announcements.
    /// The handle persists across re-renders.
    ///
    /// You must include <see cref="AnnounceHandle.Region"/> in your rendered tree:
    /// <code>
    /// var announce = UseAnnounce();
    /// return VStack(
    ///     announce.Region,
    ///     Button("Save", () => { Save(); announce.Announce("Document saved"); }),
    /// );
    /// </code>
    /// </summary>
    public static AnnounceHandle UseAnnounce(this RenderContext ctx)
    {
        var (handle, _) = ctx.UseState(new AnnounceHandle());
        return handle;
    }

    /// <summary>
    /// Creates an <see cref="AnnounceHandle"/> for making screen reader announcements.
    /// </summary>
    public static AnnounceHandle UseAnnounce(this Component component)
        => component.Context.UseAnnounce();
}
