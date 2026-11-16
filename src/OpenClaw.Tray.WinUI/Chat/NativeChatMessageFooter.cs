using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Chat;

// Keeps footer transitions outside Reactor's exact-type layout-control pool.
// Usage: NativeChatMessageFooterElement.NativeChatMessageFooter(content).OpacityTransition(duration).
internal sealed class NativeChatMessageFooter : ContentControl
{
    public NativeChatMessageFooter()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
    }
}

[GenerateReactorWrapper(typeof(NativeChatMessageFooter), AutoDiscover = false, RegisterAssembly = false)]
[WrapContent(nameof(ContentControl.Content))]
internal sealed partial record NativeChatMessageFooterElement;
