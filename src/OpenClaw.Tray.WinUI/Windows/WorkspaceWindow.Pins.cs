using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Windows;

/// <summary>Tag on a pinned-page sidebar item. Distinct from workspace route and session tags.</summary>
internal sealed record PinnedPageTag(string Tag);

// Local, per-Gateway page shortcuts. Pins route through OnSettingsWorkspaceRoute, the same single
// Workspace history path as every other Settings entry, and persist through the store without
// SettingsSaved so they never trigger a capability reload or node reconnect.
public sealed partial class WorkspaceWindow
{
    private readonly PinnedPageRows _pinRows = new();
    private string? _pickerScope;
    private bool _pinsWired;

    private static string CurrentPinScope() => PinnedPages.ScopeKey(CurrentApp.Registry?.ActiveGatewayId);

    private void WirePins()
    {
        if (_pinsWired) return;
        _pinsWired = true;
        NavView.ItemInvoked += OnPinItemInvoked;
        ToolTipService.SetToolTip(PagesCustomizeButton, AutomationProperties.GetName(PagesCustomizeButton));
        if (CurrentApp.SettingsStore is { } store) store.Changed += OnPinStoreChanged;
        if (CurrentApp.Registry is { } registry) registry.Changed += OnPinRegistryChanged;
        RebuildPins();
    }

    private void UnwirePins()
    {
        if (!_pinsWired) return;
        _pinsWired = false;
        NavView.ItemInvoked -= OnPinItemInvoked;
        if (CurrentApp.SettingsStore is { } store) store.Changed -= OnPinStoreChanged;
        if (CurrentApp.Registry is { } registry) registry.Changed -= OnPinRegistryChanged;
    }

    private void OnPinStoreChanged(object? sender, SettingsChangedEventArgs e) => RunOnPinThread(RebuildPins);

    private void OnPinRegistryChanged(object? sender, GatewayRegistryChangedEventArgs e) =>
        RunOnPinThread(() =>
        {
            if (_pickerScope is not null && _pickerScope != CurrentPinScope()) PagesFlyout.Hide();
            RebuildPins();
        });

    private void RunOnPinThread(Action action) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed) action();
        });

    private ConnectionStatus PinStatus => _state.Status;

    private void RebuildPins()
    {
        var stored = CurrentApp.SettingsStore?.Current.PinnedPagesFor(CurrentPinScope()) ?? [];
        var visible = PinnedPages.Visible(stored, PinStatus);
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            // Unchanged pins keep their row (and focus); only added, removed or moved rows change.
            var start = NavView.MenuItems.IndexOf(PagesHeader) + 1;
            var removed = _pinRows.Reconcile(NavView.MenuItems, start,
                visible.Select(page => page.Tag).ToList(), tag => CreatePinItem(PinnedPages.Find(tag)!));
            RepairFocusForRemovedPins(removed);
            PagesEmpty.Content = Text("PagesEmpty");
            PagesEmpty.Visibility = stored.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updating = wasUpdating; }
    }

    private static NavigationViewItem CreatePinItem(PinnablePage page)
    {
        var label = LocalizationHelper.GetString(page.LabelKey);
        var item = new NavigationViewItem
        {
            Content = label,
            Tag = new PinnedPageTag(page.Tag),
            SelectsOnInvoked = false,
            Icon = new ImageIcon { Source = new SvgImageSource(new Uri($"ms-appx:///Assets/SidebarIcons/{page.IconAsset}.svg")) },
        };
        AutomationProperties.SetAccessibilityView(item.Icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(item, $"WorkspacePinnedPage:{page.Tag}");
        AutomationProperties.SetName(item, label);
        return item;
    }

    // A focus target that genuinely disappeared falls back to the customize button, the pins' owner control.
    private void RepairFocusForRemovedPins(IReadOnlyList<object> removed)
    {
        if (removed.Count == 0) return;
        if (_chatFocus is not null && removed.Contains(_chatFocus))
            _chatFocus = PagesCustomizeButton;
        if (Root.XamlRoot is { } root &&
            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is { } focused && removed.Contains(focused))
            PagesCustomizeButton.Focus(FocusState.Programmatic);
    }

    private void OnPinItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.Tag is not PinnedPageTag pin) return;
        if (!PinnedPages.IsAvailable(pin.Tag, PinStatus))
        {
            RebuildPins();
            return;
        }
        // Agent-scoped pages target the assistant selected in Chat now, never the last Settings agent.
        OnSettingsWorkspaceRoute(PinnedPages.ResolveRoute(pin.Tag, _agentId));
    }

    private void OnPagesFlyoutOpening(object sender, object e)
    {
        _pickerScope = CurrentPinScope();
        var pinned = CurrentApp.SettingsStore?.Current.PinnedPagesFor(_pickerScope) ?? [];
        PagesFlyout.Items.Clear();
        foreach (var page in PinnedPages.Catalog)
        {
            var label = LocalizationHelper.GetString(page.LabelKey);
            var toggle = new ToggleMenuFlyoutItem { Text = label, IsChecked = pinned.Contains(page.Tag), Tag = page.Tag };
            AutomationProperties.SetAutomationId(toggle, $"WorkspacePagesToggle:{page.Tag}");
            toggle.Click += OnPagePinToggled;
            PagesFlyout.Items.Add(toggle);
        }
    }

    private void OnPagesFlyoutClosed(object sender, object e) => _pickerScope = null;

    private void OnPagePinToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string tag } toggle ||
            CurrentApp.SettingsStore is not { } store ||
            _pickerScope is not { } scope)
            return;
        // A Gateway switch while the picker is open must never write the old selection into the new scope.
        if (scope != CurrentPinScope())
        {
            PagesFlyout.Hide();
            RebuildPins();
            return;
        }
        try
        {
            store.Update(store.CreateOrigin(), editor => editor.SetPagePinned(scope, tag, toggle.IsChecked));
        }
        catch (Exception ex)
        {
            // The store rolled back; show the real persisted state instead of a success-shaped check.
            Logger.Warn($"[Workspace] Pin save failed ({ex.GetType().Name}); reverted picker state.");
            toggle.IsChecked = store.Current.PinnedPagesFor(scope).Contains(tag);
            ReportError("pin-page", ex);
        }
        RebuildPins();
    }
}
