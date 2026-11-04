using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Supplies the Invoke provider missing from the pinned Toolkit card peer.</summary>
public sealed partial class SetupActionCard : SettingsCard
{
    public event RoutedEventHandler? InvokeRequested;

    public SetupActionCard() =>
        Click += (_, args) => InvokeRequested?.Invoke(this, args);

    protected override AutomationPeer OnCreateAutomationPeer() => new ActionCardAutomationPeer(this);

    private sealed partial class ActionCardAutomationPeer(SetupActionCard owner)
        : SettingsCardAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke()
        {
            if (!owner.IsEnabled || !owner.IsClickEnabled)
                throw new ElementNotEnabledException();
            if (!owner.DispatcherQueue.TryEnqueue(() =>
                owner.InvokeRequested?.Invoke(owner, new RoutedEventArgs())))
                throw new ObjectDisposedException(nameof(SetupActionCard));
        }
    }
}
