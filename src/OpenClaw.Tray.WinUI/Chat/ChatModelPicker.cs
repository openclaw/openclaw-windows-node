using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Chat;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatModelPickerProps(
    Element Target,
    IReadOnlyList<ChatModelChoice> Choices,
    string? CurrentModel,
    string? CurrentProvider,
    bool Enabled,
    Action<ChatModelChoice> Select,
    double AvailableWidth);

[GenerateReactorWrapper(typeof(NativeChatModelPicker), AutoDiscover = false,
    RegisterAssembly = false)]
internal partial record NativeChatModelPickerElement;

/// <summary>Reactor owns the flyout lifetime; native controls own its search and selection UI.</summary>
internal sealed class ChatModelPicker : Component<ChatModelPickerProps>
{
    public override Element Render()
    {
        var props = Props;
        var anchor = UseRef<Button?>(null);
        var view = UseRef<NativeChatModelPicker?>(null);
        return Flyout(
            props.Target.OnMountAdd(control => anchor.Current = (Button)control),
            NativeChatModelPickerElement.NativeChatModelPicker()
                .Set(content =>
                {
                    view.Current = content;
                    content.Update(Props, choice =>
                    {
                        Props.Select(choice);
                        anchor.Current?.Flyout?.Hide();
                    });
                })
                .OnUnmount(control =>
                {
                    view.Current = null;
                    ((NativeChatModelPicker)control).Dispose();
                }))
            .Opened(() => view.Current?.RestoreCommittedSelection())
            .Set(flyout => flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedRight);
    }
}
