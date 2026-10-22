using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Controls;
using OpenClawTray.Presentation;

namespace OpenClawTray.Chat;

// Keep Reactor's child reconciliation outside the badge's XAML-owned content.
internal sealed class NativeChatWelcomeAvatar : Control
{
    private readonly AgentIdentityBadge _badge = new();

    public NativeChatWelcomeAvatar() =>
        Template = (ControlTemplate)Application.Current.Resources["ChatWelcomeAvatarTemplate"];

    internal void Update(WorkspaceAgent agent, double size) =>
        _badge.Initialize(agent, avatarOnly: true, pictureSize: size);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("AvatarRoot")).Content = _badge;
    }
}
