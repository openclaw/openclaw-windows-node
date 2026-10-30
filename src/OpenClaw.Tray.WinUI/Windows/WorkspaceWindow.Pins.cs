using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Controls;
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
        var visible = PinnedPages.Visible(stored);
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            // Unchanged pins keep their row (and focus); only added, removed or moved rows change.
            var start = NavView.MenuItems.IndexOf(HomeItem) + 1;
            var change = _pinRows.Reconcile(NavView.MenuItems, start,
                visible.Select(page => page.Tag).ToList(), tag => CreatePinItem(PinnedPages.Find(tag)!),
                FocusedElement, PinRowOwns);
            RepairFocusForRemovedPins(change);
            UpdatePinAvailability();
        }
        finally { _updating = wasUpdating; }
        if (Destination.Embedded) UpdateNavigationSelection();
    }

    private void UpdatePinAvailability()
    {
        // Native disabling can move focus synchronously. Capture ownership before any setter.
        var focused = FocusedElement();
        var focusedPinDisabled = false;
        foreach (var item in _pinRows.Rows.OfType<NavigationViewItem>())
        {
            if (item.Tag is not PinnedPageTag pin) continue;
            var available = PinnedPages.IsAvailable(pin.Tag, PinStatus);
            if (!available)
            {
                focusedPinDisabled |= focused is not null && PinRowOwns(item, focused);
                if (_chatFocus is not null && PinRowOwns(item, _chatFocus))
                    _chatFocus = PagesCustomizeButton;
            }
            item.IsEnabled = available;
        }
        if (focusedPinDisabled) FocusPinCustomizer();
    }

    private static NavigationViewItem CreatePinItem(PinnablePage page)
    {
        var label = LocalizationHelper.GetString(page.LabelKey);
        var icon = new ImageIcon
        {
            Source = new SvgImageSource(new Uri($"ms-appx:///Assets/SidebarIcons/{page.IconAsset}.svg")),
            Width = 22,
            Height = 22,
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // Same iconless-presenter layout as the Home row so icons and labels line up and the icon
        // always has an explicit size (an SVG has no intrinsic one).
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var item = new NavigationViewItem
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Children = { new Grid { Width = 24, Children = { icon } }, text },
            },
            Padding = new Thickness(0),
            Tag = new PinnedPageTag(page.Tag),
            SelectsOnInvoked = false,
        };
        item.Resources["NavigationViewItemContentPresenterMargin"] = new Thickness(0, -1, 8, -1);
        AutomationProperties.SetAutomationId(item, $"WorkspacePinnedPage:{page.Tag}");
        AutomationProperties.SetName(item, label);
        return item;
    }

    private object? FocusedElement() =>
        Root.XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) : null;

    // Focus may sit on a descendant of the row (for example its presenter), so walk up the visual tree.
    private static bool PinRowOwns(object row, object element)
    {
        for (var node = element as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, row)) return true;
        return false;
    }

    // Removed or newly disabled focus targets fall back without navigating.
    private void RepairFocusForRemovedPins(PinnedRowsChange change)
    {
        if (change.Removed.Count == 0) return;
        if (_chatFocus is not null && change.Removed.Any(row => PinRowOwns(row, _chatFocus)))
            _chatFocus = PagesCustomizeButton;
        if (change.FocusedRowRemoved) FocusPinCustomizer();
    }

    private void FocusPinCustomizer()
    {
        if (!PagesCustomizeButton.Focus(FocusState.Programmatic)) HomeItem.Focus(FocusState.Programmatic);
    }

    private void OnPinItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        // SelectedItem can synchronously raise ItemInvoked for a nonselecting pin.
        // A render's selection update is not another user activation.
        if (_updating) return;
        // Recheck the live state even if an activation was queued before the row became disabled.
        if (e.InvokedItemContainer?.Tag is PinnedPageTag candidate &&
            !PinnedPages.IsAvailable(candidate.Tag, PinStatus))
        {
            RebuildPins();
            return;
        }
        if (e.InvokedItemContainer is not null) _sidebarInvoked = true;
        // Home may already be highlighted while the retained host shows an unresolved/default thread.
        if (e.InvokedItemContainer == HomeItem)
        {
            NavigateHome();
            return;
        }
        if (e.InvokedItemContainer?.Tag is not PinnedPageTag pin) return;
        // Agent-scoped pages target the assistant selected in Chat now, never the last Settings agent.
        OpenSettings(PinnedPages.ResolveRoute(pin.Tag, _agentId), embedded: true);
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
        QueuePinnedPageHostPreparation();
    }

    private void QueuePinnedPageHostPreparation()
    {
        if (_settings is not null || _preparedSettings is not null) return;
        // Prepare only the XAML shell. Bind current state at invocation so preparation cannot
        // establish Settings agent identity, subscriptions, page activation or history early.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsClosed || Root.XamlRoot is null || _settings is not null || _preparedSettings is not null ||
                CurrentApp.SettingsStore?.Current.PinnedPagesFor(CurrentPinScope()).IsEmpty != false)
                return;
            try { _preparedSettings = new SettingsShellView(); }
            catch (Exception ex) { ReportError("prepare-pinned-page-host", ex); }
        }))
            Logger.Warn("[Workspace] Could not queue pinned-page host preparation while shutting down.");
    }
}
