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
    double AvailableWidth,
    Element? Reasoning = null);

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
            ScrollViewer(VStack(0,
                NativeChatModelPickerElement.NativeChatModelPicker()
                .Set(content =>
                {
                    view.Current = content;
                    content.Update(Props, choice =>
                    {
                        Props.Select(choice);
                        anchor.Current?.Flyout?.Hide();
                    }, null);
                })
                .OnUnmount(control =>
                {
                    view.Current = null;
                    ((NativeChatModelPicker)control).Dispose();
                }),
                props.Reasoning is { } reasoning
                    ? VStack(0,
                        Border(null).Height(1).Background(Theme.Ref("ChatStrokeBrush")).Margin(0, 1, 0, 1),
                        reasoning.Margin(4, 2, 4, 4))
                    : Empty()))
                .MaxHeight(520)
                .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled))
            .Opened(() => view.Current?.RestoreCommittedSelection())
            .Set(ChatVisuals.StylePicker);
    }
}
