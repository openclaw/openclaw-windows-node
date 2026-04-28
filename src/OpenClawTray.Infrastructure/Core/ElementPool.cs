using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Per-CLR-type pool (cap 32) that recycles unmounted WinUI FrameworkElement instances.
/// V1: pools only non-interactive controls (no event handlers to worry about).
/// </summary>
public sealed class ElementPool : IDisposable
{
    /// <summary>
    /// Tracks UIElements that have had GetElementVisual() called on them.
    /// These elements permanently lose the ability to use XAML implicit transition APIs
    /// (OpacityTransition, ScaleTransition, etc.), so they must not be pooled — a future
    /// user of the element might need those APIs.
    /// </summary>
    private static readonly ConditionalWeakTable<UIElement, object> _compositorTainted = new();

    /// <summary>
    /// Marks a UIElement as having been accessed via GetElementVisual().
    /// Called by reconciler code that touches the composition Visual.
    /// </summary>
    internal static void MarkCompositorTainted(UIElement element)
    {
        _compositorTainted.AddOrUpdate(element, true);
    }

    internal static bool IsCompositorTainted(UIElement element)
    {
        return _compositorTainted.TryGetValue(element, out _);
    }

    private const int MaxPerType = 32;

    /// <summary>
    /// When false, TryRent always returns null and Return is a no-op.
    /// Useful for scenarios like the live previewer where recycled controls
    /// with stale property state can cause visual glitches.
    /// </summary>
    public bool Enabled { get; set; } = true;

    private static readonly HashSet<Type> PoolableTypes = new()
    {
        typeof(TextBlock),
        typeof(WinUI.RichTextBlock),
        typeof(WinUI.StackPanel),
        typeof(WinUI.Grid),
        typeof(WinUI.Border),
        typeof(WinUI.ScrollViewer),
        typeof(WinUI.Canvas),
        typeof(WinUI.Viewbox),
        typeof(WinUI.ProgressBar),
        typeof(WinUI.ProgressRing),
        typeof(WinUI.Image),
        typeof(WinUI.InfoBadge),
        // Interactive controls — safe to pool because the Tag-based event pattern
        // reads the current element from Tag at invocation time, so recycled controls
        // automatically dispatch to the new element's callbacks after SetElementTag.
        typeof(WinUI.Button),
        typeof(TextBox),
        typeof(WinUI.ToggleSwitch),
    };

    private readonly Dictionary<Type, Stack<FrameworkElement>> _pools = new();

    // A scratch panel used to force WinUI to fully process parent detachment.
    // Adding then removing from this panel ensures WinUI's internal parent
    // tracking is cleared before the element goes into the pool.
    private WinUI.StackPanel? _scratchPanel;

    /// <summary>
    /// Force WinUI to fully release an element's internal parent state by
    /// round-tripping it through a scratch panel. Returns false if the element
    /// is broken (can't be re-parented) and should not be pooled.
    /// </summary>
    private bool ForceDetach(FrameworkElement element)
    {
        try
        {
            _scratchPanel ??= new WinUI.StackPanel();
            _scratchPanel.Children.Add(element);
            _scratchPanel.Children.Remove(element);
            return true;
        }
        catch (global::System.Runtime.InteropServices.COMException)
        {
            // Element has broken WinUI internal state — not safe to pool.
            return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // No WinUI thread (e.g. unit tests) — skip validation, allow pooling.
            return true;
        }
    }

    /// <summary>
    /// Try to rent an element of the given type from the pool.
    /// Returns null if the pool is empty or the type is not poolable.
    /// </summary>
    public FrameworkElement? TryRent(Type type)
    {
        if (!Enabled) return null;
        if (!PoolableTypes.Contains(type)) return null;
        if (!_pools.TryGetValue(type, out var stack) || stack.Count == 0) return null;
        var item = stack.Pop();
        return item;
    }

    /// <summary>
    /// Return an element to the pool after unmount. Cleans it first.
    /// Silently drops if the type is not poolable or the pool is full.
    /// </summary>
    public void Return(FrameworkElement element)
    {
        if (!Enabled) return;
        var type = element.GetType();
        if (!PoolableTypes.Contains(type)) return;

        // Don't pool elements that had GetElementVisual() called — they permanently
        // lose XAML implicit transition API access (OpacityTransition, etc.).
        if (IsCompositorTainted(element)) return;

        if (!_pools.TryGetValue(type, out var stack))
        {
            stack = new Stack<FrameworkElement>();
            _pools[type] = stack;
        }

        if (stack.Count >= MaxPerType) return;

        // Detach from parent before pooling — WinUI doesn't allow an element in two parents.
        // Use FrameworkElement.Parent (works even for detached trees, unlike VisualTreeHelper).
        DetachFromParent(element);

        // Force WinUI to fully process the detachment by round-tripping through a
        // scratch panel. Without this, WinUI's internal parent tracking may retain
        // stale state that causes COMException when the element is re-parented later.
        // If the round-trip fails, the element is broken and must not be pooled.
        if (!ForceDetach(element))
        {
            return;
        }

        CleanElement(element);
        stack.Push(element);
    }

    /// <summary>
    /// Remove an element from its current parent so it can be safely re-parented.
    /// Uses FrameworkElement.Parent which works even for detached trees
    /// (unlike VisualTreeHelper.GetParent which requires a live visual tree).
    /// </summary>
    private static void DetachFromParent(FrameworkElement element)
    {
        var parent = element.Parent;
        switch (parent)
        {
            case WinUI.Panel panel:
                panel.Children.Remove(element);
                break;
            case WinUI.Border border when ReferenceEquals(border.Child, element):
                border.Child = null;
                break;
            case WinUI.ScrollViewer sv when ReferenceEquals(sv.Content, element):
                sv.Content = null;
                break;
            case WinUI.ContentControl cc when ReferenceEquals(cc.Content, element):
                cc.Content = null;
                break;
            case WinUI.UserControl uc when ReferenceEquals(uc.Content, element):
                uc.Content = null;
                break;
        }
    }

    /// <summary>
    /// Empties all per-type stacks and releases the scratch panel.
    /// Called from <see cref="Reconciler.Dispose"/> to release pooled elements.
    /// </summary>
    public void Clear()
    {
        foreach (var stack in _pools.Values)
            stack.Clear();
        _pools.Clear();
        _scratchPanel = null;
    }

    /// <summary>
    /// Reset an element to a clean state suitable for reuse.
    /// </summary>
    internal static void CleanElement(FrameworkElement fe)
    {
        // Common properties
        fe.Tag = null;
        fe.Margin = new Thickness(0);
        fe.Width = double.NaN;
        fe.Height = double.NaN;
        fe.MinWidth = 0;
        fe.MinHeight = 0;
        fe.MaxWidth = double.PositiveInfinity;
        fe.MaxHeight = double.PositiveInfinity;
        fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        fe.VerticalAlignment = VerticalAlignment.Stretch;
        fe.Opacity = 1.0;
        fe.Visibility = Visibility.Visible;
        fe.ClearValue(FrameworkElement.RenderTransformProperty);
        fe.ClearValue(FrameworkElement.FlowDirectionProperty);

        // Clear accessibility / automation properties so pooled controls don't
        // carry stale UIA state (Name, LabeledBy, LiveSetting, etc.) into reuse.
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HelpTextProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.FullDescriptionProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LandmarkTypeProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AccessibilityViewProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.IsRequiredForFormProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LiveSettingProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.PositionInSetProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.SizeOfSetProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LevelProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.ItemStatusProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LabeledByProperty);
        fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HeadingLevelProperty);
        fe.AccessKey = "";

        // Type-specific cleanup
        switch (fe)
        {
            case WinUI.Panel panel:
                panel.Children.Clear();
                break;
            case WinUI.Border border:
                border.Child = null;
                border.Background = null;
                border.BorderBrush = null;
                border.BorderThickness = new Thickness(0);
                border.CornerRadius = new CornerRadius(0);
                border.Padding = new Thickness(0);
                break;
            case WinUI.ScrollViewer sv:
                sv.Content = null;
                break;
            case WinUI.Viewbox vb:
                vb.Child = null;
                break;
            case TextBlock tb:
                tb.Text = "";
                tb.FontSize = 14; // WinUI default
                tb.ClearValue(TextBlock.FontWeightProperty);
                tb.ClearValue(TextBlock.FontStyleProperty);
                tb.ClearValue(TextBlock.TextWrappingProperty);
                tb.ClearValue(TextBlock.TextAlignmentProperty);
                tb.ClearValue(TextBlock.TextTrimmingProperty);
                tb.ClearValue(TextBlock.IsTextSelectionEnabledProperty);
                tb.ClearValue(TextBlock.FontFamilyProperty);
                break;
            case WinUI.RichTextBlock rtb:
                rtb.Blocks.Clear();
                break;
            case WinUI.ProgressBar pb:
                pb.IsIndeterminate = false;
                pb.Value = 0;
                pb.Minimum = 0;
                pb.Maximum = 100;
                pb.ShowError = false;
                pb.ShowPaused = false;
                break;
            case WinUI.ProgressRing pr:
                pr.IsIndeterminate = false;
                pr.IsActive = true;
                pr.Value = 0;
                pr.Minimum = 0;
                pr.Maximum = 100;
                break;
            case WinUI.Image img:
                img.Source = null;
                break;
            case WinUI.InfoBadge badge:
                badge.Value = -1; // WinUI default (hidden)
                break;

            // Interactive controls — reset transient state so no state leaks between uses.
            // Event handlers are NOT removed: the Tag-based pattern reads the current
            // element from Tag at invocation time, so stale closures are harmless.
            case WinUI.Button button:
                button.Content = null;
                button.IsEnabled = true;
                button.Flyout = null;
                VisualStateManager.GoToState(button, "Normal", false);
                break;
            case TextBox textBox:
                textBox.Text = "";
                textBox.PlaceholderText = "";
                textBox.Header = null;
                textBox.IsReadOnly = false;
                textBox.AcceptsReturn = false;
                textBox.ClearValue(TextBox.TextWrappingProperty);
                VisualStateManager.GoToState(textBox, "Normal", false);
                break;
            case WinUI.ToggleSwitch toggle:
                toggle.IsOn = false;
                toggle.IsEnabled = true;
                toggle.OnContent = null;
                toggle.OffContent = null;
                toggle.Header = null;
                VisualStateManager.GoToState(toggle, "Normal", false);
                break;
        }
    }

    public void Dispose()
    {
        foreach (var stack in _pools.Values)
        {
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (element is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        _pools.Clear();
    }
}
