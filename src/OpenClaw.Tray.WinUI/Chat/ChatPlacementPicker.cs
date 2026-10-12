using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatPlacementPickerProps(
    Element Target, ChatPlacementController Controller, bool EnvironmentPicker, double AvailableWidth);

[GenerateReactorWrapper(typeof(NativeChatPlacementPicker), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeChatPlacementPickerElement;

internal sealed class ChatPlacementPicker : Component<ChatPlacementPickerProps>
{
    public override Element Render()
    {
        var anchor = UseRef<Button?>(null);
        var view = UseRef<NativeChatPlacementPicker?>(null);
        return Flyout(
            Props.Target.OnMountAdd(control => anchor.Current = (Button)control),
            NativeChatPlacementPickerElement.NativeChatPlacementPicker()
                .Set(content =>
                {
                    view.Current = content;
                    content.Update(Props, () => anchor.Current?.Flyout?.Hide());
                })
                .OnUnmount(control => ((NativeChatPlacementPicker)control).Dispose()))
            .Opened(() => view.Current?.RestoreSelection())
            .Set(flyout => flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft);
    }
}
