using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenClawTray.Infrastructure.Accessibility;

/// <summary>
/// A lightweight WinUI Panel that provides custom automation semantics for
/// composite Reactor components. Wraps a single child and overrides
/// OnCreateAutomationPeer() to expose a custom role, value, and range to
/// screen readers.
///
/// This solves the fundamental limitation where Reactor components (C# records)
/// cannot override OnCreateAutomationPeer() on WinUI Controls.
///
/// Usage from Reactor DSL:
///   StarRating(value: 3, max: 5)
///       .Semantics(role: "slider", value: "3 of 5 stars",
///                  rangeValue: 3, rangeMin: 0, rangeMax: 5)
/// </summary>
public sealed partial class SemanticPanel : Panel
{
    // ── Dependency properties for semantic description ──

    public static readonly DependencyProperty SemanticRoleProperty =
        DependencyProperty.Register(nameof(SemanticRole), typeof(string), typeof(SemanticPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SemanticValueProperty =
        DependencyProperty.Register(nameof(SemanticValue), typeof(string), typeof(SemanticPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RangeMinimumProperty =
        DependencyProperty.Register(nameof(RangeMinimum), typeof(double), typeof(SemanticPanel),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty RangeMaximumProperty =
        DependencyProperty.Register(nameof(RangeMaximum), typeof(double), typeof(SemanticPanel),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty RangeValueProperty =
        DependencyProperty.Register(nameof(RangeValue), typeof(double), typeof(SemanticPanel),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(SemanticPanel),
            new PropertyMetadata(true));

    public string? SemanticRole
    {
        get => (string?)GetValue(SemanticRoleProperty);
        set => SetValue(SemanticRoleProperty, value);
    }

    public string? SemanticValue
    {
        get => (string?)GetValue(SemanticValueProperty);
        set => SetValue(SemanticValueProperty, value);
    }

    public double RangeMinimum
    {
        get => (double)GetValue(RangeMinimumProperty);
        set => SetValue(RangeMinimumProperty, value);
    }

    public double RangeMaximum
    {
        get => (double)GetValue(RangeMaximumProperty);
        set => SetValue(RangeMaximumProperty, value);
    }

    public double RangeValue
    {
        get => (double)GetValue(RangeValueProperty);
        set => SetValue(RangeValueProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new SemanticPanelAutomationPeer(this);

    // Single-child passthrough layout
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count > 0)
        {
            Children[0].Measure(availableSize);
            return Children[0].DesiredSize;
        }
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0)
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }
}

/// <summary>
/// Custom AutomationPeer for SemanticPanel. Implements IRangeValueProvider and
/// IValueProvider so screen readers can read the composite component's semantic
/// role, value, and range. Analogous to SwiftUI's .accessibilityRepresentation {}
/// and Compose's Modifier.semantics { role = Role.Slider }.
/// </summary>
public sealed partial class SemanticPanelAutomationPeer : FrameworkElementAutomationPeer,
    IRangeValueProvider, IValueProvider
{
    private SemanticPanel Panel => (SemanticPanel)Owner;

    public SemanticPanelAutomationPeer(SemanticPanel owner) : base(owner) { }

    protected override string GetLocalizedControlTypeCore()
        => Panel.SemanticRole ?? "group";

    protected override string GetClassNameCore() => "SemanticPanel";

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        return Panel.SemanticRole?.ToLowerInvariant() switch
        {
            "slider" => AutomationControlType.Slider,
            "progressbar" or "progress" => AutomationControlType.ProgressBar,
            "spinbutton" or "spinner" => AutomationControlType.Spinner,
            "group" => AutomationControlType.Group,
            "list" => AutomationControlType.List,
            "listitem" => AutomationControlType.ListItem,
            "tab" => AutomationControlType.Tab,
            "tabitem" => AutomationControlType.TabItem,
            "tree" => AutomationControlType.Tree,
            "treeitem" => AutomationControlType.TreeItem,
            "menu" => AutomationControlType.Menu,
            "menuitem" => AutomationControlType.MenuItem,
            "toolbar" => AutomationControlType.ToolBar,
            "statusbar" => AutomationControlType.StatusBar,
            "image" => AutomationControlType.Image,
            "document" => AutomationControlType.Document,
            "custom" => AutomationControlType.Custom,
            _ => AutomationControlType.Group,
        };
    }

    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        // Expose IRangeValueProvider when range properties are set
        if (patternInterface == PatternInterface.RangeValue
            && Panel.RangeMaximum != Panel.RangeMinimum)
            return this;

        // Expose IValueProvider when a semantic value is set
        if (patternInterface == PatternInterface.Value
            && Panel.SemanticValue is not null)
            return this;

        return base.GetPatternCore(patternInterface);
    }

    // ── IRangeValueProvider ──
    public double Minimum => Panel.RangeMinimum;
    public double Maximum => Panel.RangeMaximum;
    public double Value => Panel.RangeValue;
    public double SmallChange => 1;
    public double LargeChange => (Panel.RangeMaximum - Panel.RangeMinimum) / 10.0;
    public bool IsReadOnly => Panel.IsReadOnly;

    public void SetValue(double value)
    {
        if (!Panel.IsReadOnly)
            Panel.RangeValue = value;
    }

    // ── IValueProvider ──
    string IValueProvider.Value => Panel.SemanticValue ?? "";
    bool IValueProvider.IsReadOnly => Panel.IsReadOnly;

    void IValueProvider.SetValue(string value)
    {
        if (!Panel.IsReadOnly)
            Panel.SemanticValue = value;
    }
}
