// <summary>
// Reactor component that hosts the native chat-model picker flyout. It gives the native control current
// choices and selection state while Reactor owns the flyout lifecycle and forwards committed choices to its host.
// The picker never changes a model until an explicitly selectable item is activated.
// Usage:
//   var props = new ChatModelPickerProps(target, choices, model, provider, true, Select, width);
//   // Reactor supplies props when it mounts ChatModelPicker in the composer tree.
//   Element flyout = new ChatModelPicker().Render();
// </summary>

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Chat;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// Supplies the anchor, available choices, committed selection and callback for <see cref="ChatModelPicker"/>.
/// </summary>
internal sealed record ChatModelPickerProps(
    Element Target,
    IReadOnlyList<ChatModelChoice> Choices,
    string? CurrentModel,
    string? CurrentProvider,
    bool Enabled,
    Action<ChatModelChoice> Select,
    double AvailableWidth);

/// <summary>Provides the generated Reactor wrapper for <see cref="NativeChatModelPicker"/>.</summary>
[GenerateReactorWrapper(typeof(NativeChatModelPicker), AutoDiscover = false,
    RegisterAssembly = false)]
internal partial record NativeChatModelPickerElement;

/// <summary>
/// Renders the chat model flyout and delegates native search and explicit selection handling to its control.
/// </summary>
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
                    }, () =>
                    {
                        // Direct settings navigation, like the voice readiness dialogs; the composer host has no navigation callback.
                        anchor.Current?.Flyout?.Hide();
                        (Application.Current as IAppCommands)?.Navigate("model");
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
