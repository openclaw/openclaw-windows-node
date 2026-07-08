using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using Windows.UI;
using Windows.UI.Text;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace OpenClawTray.FunctionalUI.Core
{

public abstract record Element
{
    public ElementModifiers Modifiers { get; } = new();
    public GridPosition? GridPosition { get; set; }
    public string? Key { get; set; }
    public List<Delegate> Setters { get; } = new();
}

public sealed class ElementModifiers
{
    public Thickness? Margin { get; set; }
    public Thickness? Padding { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? MinWidth { get; set; }
    public double? MaxWidth { get; set; }
    public double? MinHeight { get; set; }
    public double? MaxHeight { get; set; }
    public HorizontalAlignment? HorizontalAlignment { get; set; }
    public VerticalAlignment? VerticalAlignment { get; set; }
    public Brush? Background { get; set; }
    public string? BackgroundResourceKey { get; set; }
    public Brush? Foreground { get; set; }
    public string? ForegroundResourceKey { get; set; }
    public Brush? BorderBrush { get; set; }
    public string? BorderBrushResourceKey { get; set; }
    public Thickness? BorderThickness { get; set; }
    public CornerRadius? CornerRadius { get; set; }
    public double? FontSize { get; set; }
    public FontWeight? FontWeight { get; set; }
    public FontFamily? FontFamily { get; set; }
    public TextWrapping? TextWrapping { get; set; }
    public bool? Disabled { get; set; }
    public bool? ReadOnly { get; set; }
    public double? Opacity { get; set; }
    public ScrollMode? HorizontalScrollMode { get; set; }
    public RoutedEventHandler? GotFocus { get; set; }
    public KeyEventHandler? KeyDown { get; set; }
    public PointerEventHandler? PointerEntered { get; set; }
    public PointerEventHandler? PointerExited { get; set; }
    public string? AutomationName { get; set; }
    public Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting? LiveRegion { get; set; }
    public Action<FrameworkElement>? OnMount { get; set; }
    public ResourceOverrides? ResourceOverrides { get; set; }
    public double? FlexGrow { get; set; }
}

internal static class ThemeResources
{
    public static Brush ResolveBrush(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            throw new ArgumentException("Resource key is required.", nameof(resourceKey));

        if (Application.Current is not { Resources: { } resources })
            throw new InvalidOperationException("Application resources are unavailable.");

        if (resources.TryGetValue(resourceKey, out var resource) && resource is Brush brush)
            return brush;

        throw new InvalidOperationException($"Brush resource '{resourceKey}' was not found.");
    }
}

public readonly record struct ThemeRef(string ResourceKey);

public static class Theme
{
    public static ThemeRef Accent => new("AccentFillColorDefaultBrush");
    public static ThemeRef PrimaryText => new("TextFillColorPrimaryBrush");
    public static ThemeRef SecondaryText => new("TextFillColorSecondaryBrush");
    public static ThemeRef TertiaryText => new("TextFillColorTertiaryBrush");
    public static ThemeRef AccentText => new("AccentTextFillColorPrimaryBrush");
    public static ThemeRef CardBackground => new("CardBackgroundFillColorDefaultBrush");
    public static ThemeRef DividerStroke => new("DividerStrokeColorDefaultBrush");
    public static ThemeRef Ref(string resourceKey) => new(resourceKey);
}

public sealed record ResourceOverrides(IReadOnlyDictionary<string, object> Values);

public sealed class ResourceBuilder
{
    private readonly Dictionary<string, object> _values = new();

    public ResourceBuilder Set(string key, string color)
    {
        _values[key] = new SolidColorBrush(ParseColor(color));
        return this;
    }

    public ResourceBuilder Set(string key, Brush brush)
    {
        _values[key] = brush;
        return this;
    }

    public ResourceBuilder Set(string key, ThemeRef themeRef)
    {
        _values[key] = themeRef;
        return this;
    }

    public ResourceBuilder Set(string key, double value)
    {
        _values[key] = value;
        return this;
    }

    public ResourceBuilder Set(string key, CornerRadius value)
    {
        _values[key] = value;
        return this;
    }

    internal ResourceOverrides Build() => new(new Dictionary<string, object>(_values));

    private static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        var offset = value.Length == 8 ? 0 : -2;
        byte a = offset == 0 ? Convert.ToByte(value[..2], 16) : (byte)255;
        byte r = Convert.ToByte(value[(2 + offset)..(4 + offset)], 16);
        byte g = Convert.ToByte(value[(4 + offset)..(6 + offset)], 16);
        byte b = Convert.ToByte(value[(6 + offset)..(8 + offset)], 16);
        return Color.FromArgb(a, r, g, b);
    }
}

public sealed record GridPosition(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

public readonly record struct GridSize(double Value, GridUnitType Type)
{
    public static GridSize Auto { get; } = new(1, GridUnitType.Auto);
    public static GridSize Star(double weight = 1)
    {
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Star weight must be greater than zero.");
        return new GridSize(weight, GridUnitType.Star);
    }

    public static GridSize Px(double pixels)
    {
        if (pixels < 0)
            throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "Pixel size must be non-negative.");
        return new GridSize(pixels, GridUnitType.Pixel);
    }

    public static GridSize Parse(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "Auto", StringComparison.OrdinalIgnoreCase))
            return Auto;
        if (trimmed == "*")
            return Star();
        if (trimmed.EndsWith('*'))
            return Star(double.Parse(trimmed[..^1], CultureInfo.InvariantCulture));
        return Px(double.Parse(trimmed, CultureInfo.InvariantCulture));
    }

    public static implicit operator GridLength(GridSize size) => new(size.Value, size.Type);
    public override string ToString() => Type switch
    {
        GridUnitType.Auto => "Auto",
        GridUnitType.Star when Value == 1 => "*",
        GridUnitType.Star => Value.ToString("R", CultureInfo.InvariantCulture) + "*",
        GridUnitType.Pixel => Value.ToString("R", CultureInfo.InvariantCulture),
        _ => base.ToString() ?? string.Empty
    };
}

public sealed record TextBlockElement(string Text) : Element;
public sealed record TextFieldElement(string Value, Action<string>? OnChanged, string? Placeholder, string? Header) : Element;
public sealed record PasswordBoxElement(string Password, Action<string>? OnChanged, string? Placeholder) : Element;
public sealed record ButtonElement(string Label, Action? OnClick, Element? ContentElement = null) : Element
{
    public FlyoutElement? Flyout { get; set; }
}
public sealed record RadioButtonElement(string Label, bool IsChecked, Action<bool>? OnChecked, string? GroupName) : Element;
public sealed record RadioButtonsElement(string[] Items, int SelectedIndex, Action<int>? OnSelectionChanged) : Element;
public sealed record CheckBoxElement(bool IsChecked, Action<bool>? OnChanged, string? Label) : Element;
public sealed record ToggleSwitchElement(bool IsOn, Action<bool>? OnChanged, string? OnContent, string? OffContent, string? Header) : Element;
public sealed record ProgressRingElement(double? Value) : Element;
public sealed record SliderElement(double Value, double Minimum, double Maximum, Action<double>? OnChanged) : Element;
public sealed record ColorPickerElement(Color Value, Action<Color>? OnChanged) : Element;
public sealed record ComboBoxElement(string[] Items, int SelectedIndex, Action<int>? OnSelectionChanged) : Element;
public sealed record ImageElement(string Source) : Element;
public sealed record BorderElement(Element? Child) : Element;
public sealed record StackElement(Orientation Orientation, double Spacing, IReadOnlyList<Element?> Children) : Element;
public sealed record VirtualStackElement(Orientation Orientation, double Spacing, IReadOnlyList<Element?> Children) : Element;
public sealed record FlexRowElement(IReadOnlyList<Element?> Children) : Element
{
    public double ColumnGap { get; init; }
}
public sealed record GridElement(string[] Columns, string[] Rows, IReadOnlyList<Element?> Children) : Element;
public sealed record ScrollViewElement(Element? Child) : Element;
public sealed record ExpanderElement(string Header, Element? Child, bool IsExpanded = false) : Element;
public sealed record NativeElement(string Identity, Func<FrameworkElement> Create, Action<FrameworkElement>? Configure = null) : Element;
public sealed record ComponentElement(Type ComponentType, object? Props) : Element;
public abstract record FlyoutElement(FlyoutPlacementMode Placement);
public sealed record ContentFlyoutElement(Element Content, FlyoutPlacementMode Placement) : FlyoutElement(Placement);
public abstract record MenuFlyoutItemBase;
public sealed record MenuFlyoutItemData(string Text, Action? OnClick = null, string? Icon = null) : MenuFlyoutItemBase
{
    public bool IsEnabled { get; init; } = true;
    public Thickness? Padding { get; init; }
    public FontWeight? FontWeight { get; init; }
}
public sealed record RadioMenuFlyoutItemData(string Text, string GroupName, bool IsChecked = false, Action? OnClick = null, string? Icon = null) : MenuFlyoutItemBase;
public sealed record MenuFlyoutSeparatorData : MenuFlyoutItemBase;
public sealed record MenuFlyoutContentElement(MenuFlyoutItemBase[] Items, FlyoutPlacementMode Placement) : FlyoutElement(Placement);
internal interface INavigationHostElement
{
    Element RenderCurrentRoute();
}

public sealed record NavigationHostElement<TRoute>(
    OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute> Handle,
    Func<TRoute, Element> RenderRoute) : Element, INavigationHostElement where TRoute : notnull
{
    public OpenClawTray.FunctionalUI.Navigation.NavigationTransition? Transition { get; init; }

    public Element RenderCurrentRoute() => RenderRoute(Handle.CurrentRoute);
}

public abstract class Component
{
    internal RenderContext Context { get; } = new();

    public abstract Element Render();

    protected (T Value, Action<T> Set) UseState<T>(T initialValue, bool threadSafe = false) =>
        Context.UseState(initialValue, threadSafe);

    protected void UseEffect(Action effect, params object[] dependencies) =>
        Context.UseEffect(effect, dependencies);

    protected void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies) =>
        Context.UseEffect(effectWithCleanup, dependencies);

    protected Ref<T> UseRef<T>(T initialValue) => Context.UseRef(initialValue);

    protected T UseMemo<T>(Func<T> factory, params object[] dependencies) =>
        Context.UseMemo(factory, dependencies);

    protected (T Value, Action<Func<T, T>> Update) UseReducer<T>(T initialValue, bool threadSafe = false) =>
        Context.UseReducer(initialValue, threadSafe);

    protected OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute> UseNavigation<TRoute>(TRoute initial)
        where TRoute : notnull =>
        Context.UseNavigation(initial);
}

public abstract class Component<TProps> : Component, IPropsReceiver
{
    public TProps Props { get; private set; } = default!;

    void IPropsReceiver.SetProps(object props) => Props = (TProps)props;
}

internal interface IPropsReceiver
{
    void SetProps(object props);
}

internal interface IHookState;

internal sealed class ValueHookState<T>(T value, bool threadSafe) : IHookState
{
    public T Value = value;
    public readonly bool ThreadSafe = threadSafe;
    public readonly object Lock = new();
}

internal sealed class EffectHookState : IHookState
{
    public object[]? Dependencies;
    public Action? Cleanup;
}

public sealed class Ref<T>(T value)
{
    public T Current { get; set; } = value;
}

internal sealed class RefHookState<T>(T value) : IHookState
{
    public Ref<T> Ref { get; } = new(value);
}

internal sealed class MemoHookState<T> : IHookState
{
    public object[]? Dependencies;
    public T? Value;
    public bool HasValue;
}

internal sealed class NavigationHookState<TRoute>(
    OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute> handle) : IHookState
    where TRoute : notnull
{
    public OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute> Handle { get; } = handle;
}

public sealed class RenderContext
{
    private readonly List<IHookState> _hooks = new();
    private int _hookIndex;
    private Action? _requestRender;
    private Action<Action>? _afterRender;
    private int _uiThreadId;

    internal void BeginRender(Action requestRender, Action<Action> afterRender)
    {
        _hookIndex = 0;
        _requestRender = requestRender;
        _afterRender = afterRender;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    public (T Value, Action<T> Set) UseState<T>(T initialValue, bool threadSafe = false)
    {
        if (_hookIndex >= _hooks.Count)
            _hooks.Add(new ValueHookState<T>(initialValue, threadSafe));

        var currentIndex = _hookIndex++;
        if (_hooks[currentIndex] is not ValueHookState<T> hook)
            throw new InvalidOperationException("Hooks must be called in the same order every render.");

        T current;
        if (hook.ThreadSafe)
            lock (hook.Lock) current = hook.Value;
        else
            current = hook.Value;

        void Set(T next)
        {
            var h = (ValueHookState<T>)_hooks[currentIndex];
            bool changed;
            if (h.ThreadSafe)
            {
                lock (h.Lock)
                {
                    changed = !EqualityComparer<T>.Default.Equals(h.Value, next);
                    if (changed) h.Value = next;
                }
            }
            else
            {
                if (Environment.CurrentManagedThreadId != _uiThreadId)
                    throw new InvalidOperationException("UseState setter was called off the UI thread.");
                changed = !EqualityComparer<T>.Default.Equals(h.Value, next);
                if (changed) h.Value = next;
            }

            if (changed) _requestRender?.Invoke();
        }

        return (current, Set);
    }

    public void UseEffect(Action effect, params object[] dependencies)
    {
        UseEffect(() =>
        {
            effect();
            return () => { };
        }, dependencies);
    }

    public void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies)
    {
        if (_hookIndex >= _hooks.Count)
            _hooks.Add(new EffectHookState());

        var hook = _hooks[_hookIndex++] as EffectHookState
            ?? throw new InvalidOperationException("Hooks must be called in the same order every render.");

        if (hook.Dependencies is not null && !DependenciesChanged(hook.Dependencies, dependencies))
            return;

        var oldCleanup = hook.Cleanup;
        hook.Dependencies = dependencies.ToArray();
        _afterRender?.Invoke(() =>
        {
            oldCleanup?.Invoke();
            hook.Cleanup = effectWithCleanup();
        });
    }

    internal void RunEffectCleanups()
    {
        foreach (var hook in _hooks)
        {
            if (hook is not EffectHookState effectHook)
                continue;

            var cleanup = effectHook.Cleanup;
            effectHook.Cleanup = null;
            cleanup?.Invoke();
        }
    }

    public Ref<T> UseRef<T>(T initialValue)
    {
        if (_hookIndex >= _hooks.Count)
            _hooks.Add(new RefHookState<T>(initialValue));

        var hook = _hooks[_hookIndex++] as RefHookState<T>
            ?? throw new InvalidOperationException("Hooks must be called in the same order every render.");
        return hook.Ref;
    }

    public T UseMemo<T>(Func<T> factory, params object[] dependencies)
    {
        if (_hookIndex >= _hooks.Count)
            _hooks.Add(new MemoHookState<T>());

        var hook = _hooks[_hookIndex++] as MemoHookState<T>
            ?? throw new InvalidOperationException("Hooks must be called in the same order every render.");

        if (!hook.HasValue || hook.Dependencies is null || DependenciesChanged(hook.Dependencies, dependencies))
        {
            hook.Value = factory();
            hook.Dependencies = dependencies.ToArray();
            hook.HasValue = true;
        }

        return hook.Value!;
    }

    public (T Value, Action<Func<T, T>> Update) UseReducer<T>(T initialValue, bool threadSafe = false)
    {
        if (_hookIndex >= _hooks.Count)
            _hooks.Add(new ValueHookState<T>(initialValue, threadSafe));

        var currentIndex = _hookIndex++;
        if (_hooks[currentIndex] is not ValueHookState<T> hook)
            throw new InvalidOperationException("Hooks must be called in the same order every render.");

        T current;
        if (hook.ThreadSafe)
            lock (hook.Lock) current = hook.Value;
        else
            current = hook.Value;

        void Update(Func<T, T> reducer)
        {
            var h = (ValueHookState<T>)_hooks[currentIndex];
            bool changed;
            if (h.ThreadSafe)
            {
                lock (h.Lock)
                {
                    var next = reducer(h.Value);
                    changed = !EqualityComparer<T>.Default.Equals(h.Value, next);
                    if (changed) h.Value = next;
                }
            }
            else
            {
                if (Environment.CurrentManagedThreadId != _uiThreadId)
                    throw new InvalidOperationException("UseReducer updater was called off the UI thread.");
                var next = reducer(h.Value);
                changed = !EqualityComparer<T>.Default.Equals(h.Value, next);
                if (changed) h.Value = next;
            }

            if (changed) _requestRender?.Invoke();
        }

        return (current, Update);
    }

    public OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute> UseNavigation<TRoute>(TRoute initial)
        where TRoute : notnull
    {
        if (_hookIndex >= _hooks.Count)
        {
            var handle = new OpenClawTray.FunctionalUI.Navigation.NavigationHandle<TRoute>(initial);
            handle.Changed += () => _requestRender?.Invoke();
            _hooks.Add(new NavigationHookState<TRoute>(handle));
        }

        var hook = _hooks[_hookIndex++] as NavigationHookState<TRoute>
            ?? throw new InvalidOperationException("Hooks must be called in the same order every render.");
        return hook.Handle;
    }

    private static bool DependenciesChanged(IReadOnlyList<object> oldDeps, IReadOnlyList<object> newDeps)
    {
        if (oldDeps.Count != newDeps.Count) return true;
        for (var i = 0; i < oldDeps.Count; i++)
        {
            if (!Equals(oldDeps[i], newDeps[i])) return true;
        }
        return false;
    }
}

}

namespace OpenClawTray.FunctionalUI.Navigation
{

public sealed class NavigationHandle<TRoute>(TRoute initial) where TRoute : notnull
{
    private readonly Stack<TRoute> _backStack = new();

    public TRoute CurrentRoute { get; private set; } = initial;
    public event Action? Changed;

    public void Navigate(TRoute route)
    {
        _backStack.Push(CurrentRoute);
        CurrentRoute = route;
        Changed?.Invoke();
    }

    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        CurrentRoute = _backStack.Pop();
        Changed?.Invoke();
    }
}

public enum SlideDirection
{
    FromLeft,
    FromRight,
    FromTop,
    FromBottom
}

public sealed record NavigationTransition(SlideDirection Direction, TimeSpan Duration, double Distance)
{
    public static NavigationTransition SlideInOnly(
        SlideDirection direction,
        TimeSpan duration,
        double distance) =>
        new(direction, duration, distance);
}

}

namespace OpenClawTray.FunctionalUI
{

using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Navigation;

public static class Factories
{
    public static BorderElement Empty() => new(null);
    public static TextBlockElement TextBlock(string text) => new(text);
    public static TextBlockElement Caption(string text) => TextBlock(text).FontSize(12);
    public static TextFieldElement TextField(string value, Action<string>? onChanged = null, string? placeholder = null, string? header = null) =>
        new(value, onChanged, placeholder, header);
    public static PasswordBoxElement PasswordBox(string password, Action<string>? onPasswordChanged = null, string? placeholderText = null) =>
        new(password, onPasswordChanged, placeholderText);
    public static ButtonElement Button(string label, Action? onClick = null) => new(label, onClick);
    public static ButtonElement Button(Element content, Action? onClick = null) => new("", onClick, content);
    public static RadioButtonElement RadioButton(string label, bool isChecked = false, Action<bool>? onChecked = null, string? groupName = null) =>
        new(label, isChecked, onChecked, groupName);
    public static RadioButtonsElement RadioButtons(string[] items, int selectedIndex = -1, Action<int>? onSelectionChanged = null) =>
        new(items, selectedIndex, onSelectionChanged);
    public static CheckBoxElement CheckBox(bool isChecked, Action<bool>? onChanged = null, string? label = null) =>
        new(isChecked, onChanged, label);
    public static ToggleSwitchElement ToggleSwitch(bool isOn, Action<bool>? onChanged = null, string? onContent = null, string? offContent = null, string? header = null) =>
        new(isOn, onChanged, onContent, offContent, header);
    public static ProgressRingElement ProgressRing() => new(null);
    public static ProgressRingElement ProgressRing(double value) => new(value);
    public static SliderElement Slider(double value, double minimum, double maximum, Action<double>? onChanged = null) =>
        new(value, minimum, maximum, onChanged);
    public static ColorPickerElement ColorPicker(Color value, Action<Color>? onChanged = null) =>
        new(value, onChanged);
    public static ComboBoxElement ComboBox(string[] items, int selectedIndex = -1, Action<int>? onSelectionChanged = null) =>
        new(items, selectedIndex, onSelectionChanged);
    public static ImageElement Image(string source) => new(source);
    public static BorderElement Border(Element? child = null) => new(child);
    public static FlexRowElement FlexRow(params Element?[] children) => new(children);
    public static StackElement VStack(params Element?[] children) => new(Orientation.Vertical, 0, children);
    public static StackElement VStack(double spacing, params Element?[] children) => new(Orientation.Vertical, spacing, children);
    public static StackElement HStack(params Element?[] children) => new(Orientation.Horizontal, 0, children);
    public static StackElement HStack(double spacing, params Element?[] children) => new(Orientation.Horizontal, spacing, children);
    public static VirtualStackElement VirtualVStack(double spacing, params Element?[] children) => new(Orientation.Vertical, spacing, children);
    public static VirtualStackElement VirtualHStack(double spacing, params Element?[] children) => new(Orientation.Horizontal, spacing, children);
    public static GridElement Grid(string[] columns, string[] rows, params Element?[] children) => new(columns, rows, children);
    public static GridElement Grid(GridSize[] columns, GridSize[] rows, params Element?[] children) =>
        new(columns.Select(c => c.ToString()).ToArray(), rows.Select(r => r.ToString()).ToArray(), children);
    public static ScrollViewElement ScrollView(Element? child) => new(child);
    public static ExpanderElement Expander(string header, Element? child, bool isExpanded = false) => new(header, child, isExpanded);
    public static NativeElement Native<TControl>(Func<TControl> create, Action<TControl>? configure = null, string? key = null)
        where TControl : FrameworkElement =>
        new(
            key ?? typeof(TControl).AssemblyQualifiedName ?? typeof(TControl).FullName ?? typeof(TControl).Name,
            () => create(),
            configure is null ? null : element => configure((TControl)element));
    public static NativeElement Native(Func<FrameworkElement> create, Action<FrameworkElement>? configure = null, string? key = null) =>
        new(key ?? $"{create.Method.Module.ModuleVersionId:N}:{create.Method.MetadataToken}", create, configure);
    public static ContentFlyoutElement ContentFlyout(Element content, FlyoutPlacementMode placement = FlyoutPlacementMode.Bottom) =>
        new(content, placement);
    public static MenuFlyoutContentElement MenuItems(params MenuFlyoutItemBase[] items) =>
        new(items, FlyoutPlacementMode.Bottom);
    public static MenuFlyoutContentElement MenuItems(FlyoutPlacementMode placement, params MenuFlyoutItemBase[] items) =>
        new(items, placement);
    public static MenuFlyoutItemData MenuItem(string text, Action? onClick = null, string? icon = null) =>
        new(text, onClick, icon);
    public static RadioMenuFlyoutItemData RadioMenuItem(string text, string groupName, bool isChecked = false, Action? onClick = null, string? icon = null) =>
        new(text, groupName, isChecked, onClick, icon);
    public static MenuFlyoutSeparatorData MenuSeparator() => new();
    public static ComponentElement Component<TComponent>() where TComponent : Component, new() =>
        new(typeof(TComponent), null);
    public static ComponentElement Component<TComponent, TProps>(TProps props) where TComponent : Component<TProps>, new() =>
        new(typeof(TComponent), props);
    public static NavigationHostElement<TRoute> NavigationHost<TRoute>(
        NavigationHandle<TRoute> handle,
        Func<TRoute, Element> renderRoute) where TRoute : notnull =>
        new(handle, renderRoute);
}

public static class ElementExtensions
{
    public static T Margin<T>(this T element, double uniform) where T : Element =>
        element.Apply(e => e.Modifiers.Margin = new Thickness(uniform));
    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : Element =>
        element.Apply(e => e.Modifiers.Margin = new Thickness(left, top, right, bottom));
    public static T Padding<T>(this T element, double uniform) where T : Element =>
        element.Apply(e => e.Modifiers.Padding = new Thickness(uniform));
    public static T Padding<T>(this T element, double left, double top, double right, double bottom) where T : Element =>
        element.Apply(e => e.Modifiers.Padding = new Thickness(left, top, right, bottom));
    public static T Width<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.Width = value);
    public static T Height<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.Height = value);
    public static T MinWidth<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.MinWidth = value);
    public static T MaxWidth<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.MaxWidth = value);
    public static T MinHeight<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.MinHeight = value);
    public static T MaxHeight<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.MaxHeight = value);
    public static T HAlign<T>(this T element, HorizontalAlignment value) where T : Element =>
        element.Apply(e => e.Modifiers.HorizontalAlignment = value);
    public static T VAlign<T>(this T element, VerticalAlignment value) where T : Element =>
        element.Apply(e => e.Modifiers.VerticalAlignment = value);
    public static T Background<T>(this T element, string hex) where T : Element =>
        element.Apply(e => e.Modifiers.Background = new SolidColorBrush(ParseColor(hex)));
    public static T Background<T>(this T element, Color color) where T : Element =>
        element.Apply(e => e.Modifiers.Background = new SolidColorBrush(color));
    public static T Background<T>(this T element, Brush brush) where T : Element =>
        element.Apply(e => e.Modifiers.Background = brush);
    public static T Background<T>(this T element, ThemeRef theme) where T : Element =>
        element.Apply(e => e.Modifiers.BackgroundResourceKey = theme.ResourceKey);
    public static T BackgroundResource<T>(this T element, string resourceKey) where T : Element =>
        element.Apply(e => e.Modifiers.BackgroundResourceKey = resourceKey);
    public static T Foreground<T>(this T element, string hex) where T : Element =>
        element.Apply(e => e.Modifiers.Foreground = new SolidColorBrush(ParseColor(hex)));
    public static T Foreground<T>(this T element, Color color) where T : Element =>
        element.Apply(e => e.Modifiers.Foreground = new SolidColorBrush(color));
    public static T Foreground<T>(this T element, Brush brush) where T : Element =>
        element.Apply(e => e.Modifiers.Foreground = brush);
    public static T Foreground<T>(this T element, ThemeRef theme) where T : Element =>
        element.Apply(e => e.Modifiers.ForegroundResourceKey = theme.ResourceKey);
    public static T WithBorder<T>(this T element, string hex, double thickness = 1) where T : Element =>
        element.Apply(e =>
        {
            e.Modifiers.BorderBrush = new SolidColorBrush(ParseColor(hex));
            e.Modifiers.BorderThickness = new Thickness(thickness);
        });
    public static T WithBorder<T>(this T element, Brush brush, double thickness = 1) where T : Element =>
        element.Apply(e =>
        {
            e.Modifiers.BorderBrush = brush;
            e.Modifiers.BorderThickness = new Thickness(thickness);
        });
    public static T WithBorder<T>(this T element, ThemeRef theme, double thickness = 1) where T : Element =>
        element.Apply(e =>
        {
            e.Modifiers.BorderBrushResourceKey = theme.ResourceKey;
            e.Modifiers.BorderThickness = new Thickness(thickness);
        });
    public static T CornerRadius<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.CornerRadius = new CornerRadius(value));
    public static T FontSize<T>(this T element, double value) where T : Element =>
        element.Apply(e => e.Modifiers.FontSize = value);
    public static T FontWeight<T>(this T element, FontWeight value) where T : Element =>
        element.Apply(e => e.Modifiers.FontWeight = value);
    public static T FontFamily<T>(this T element, string value) where T : Element =>
        element.Apply(e => e.Modifiers.FontFamily = new FontFamily(value));
    public static T TextWrapping<T>(this T element) where T : Element =>
        element.Apply(e => e.Modifiers.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap);
    public static T Disabled<T>(this T element, bool disabled = true) where T : Element =>
        element.Apply(e => e.Modifiers.Disabled = disabled);
    public static T ReadOnly<T>(this T element, bool readOnly = true) where T : Element =>
        element.Apply(e => e.Modifiers.ReadOnly = readOnly);
    public static T Opacity<T>(this T element, double opacity) where T : Element =>
        element.Apply(e => e.Modifiers.Opacity = opacity);
    public static T HorizontalScrollMode<T>(this T element, ScrollMode value) where T : Element =>
        element.Apply(e => e.Modifiers.HorizontalScrollMode = value);
    public static T Size<T>(this T element, double width, double height) where T : Element =>
        element.Apply(e =>
        {
            e.Modifiers.Width = width;
            e.Modifiers.Height = height;
        });
    public static T Flex<T>(this T element, double grow = 0) where T : Element =>
        element.Apply(e => e.Modifiers.FlexGrow = grow);
    public static T Grid<T>(this T element, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : Element =>
        element.Apply(e => e.GridPosition = new GridPosition(row, column, rowSpan, columnSpan));
    public static T OnGotFocus<T>(this T element, RoutedEventHandler handler) where T : Element =>
        element.Apply(e => e.Modifiers.GotFocus = handler);
    public static T OnKeyDown<T>(this T element, KeyEventHandler handler) where T : Element =>
        element.Apply(e => e.Modifiers.KeyDown = handler);
    public static T OnPointerEntered<T>(this T element, PointerEventHandler handler) where T : Element =>
        element.Apply(e => e.Modifiers.PointerEntered = handler);
    public static T OnPointerExited<T>(this T element, PointerEventHandler handler) where T : Element =>
        element.Apply(e => e.Modifiers.PointerExited = handler);
    public static T AutomationName<T>(this T element, string name) where T : Element =>
        element.Apply(e => e.Modifiers.AutomationName = name);
    public static T LiveRegion<T>(this T element, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting value) where T : Element =>
        element.Apply(e => e.Modifiers.LiveRegion = value);
    public static T WithKey<T>(this T element, string key) where T : Element =>
        element.Apply(e => e.Key = key);
    public static T OnMount<T>(this T element, Action<FrameworkElement> action) where T : Element =>
        element.Apply(e => e.Modifiers.OnMount = action);
    public static T Resources<T>(this T element, Action<ResourceBuilder> configure) where T : Element
    {
        var builder = new ResourceBuilder();
        configure(builder);
        return element.Apply(e => e.Modifiers.ResourceOverrides = builder.Build());
    }
    public static ButtonElement WithFlyout(this ButtonElement element, FlyoutElement flyout) =>
        element.Apply(e => e.Flyout = flyout);
    public static TextBlockElement SemiBold(this TextBlockElement element) =>
        element.FontWeight(Microsoft.UI.Text.FontWeights.SemiBold);

    public static TextBlockElement Set(this TextBlockElement element, Action<TextBlock> setter) => element.AddSetter(setter);
    public static TextFieldElement Set(this TextFieldElement element, Action<TextBox> setter) => element.AddSetter(setter);
    public static PasswordBoxElement Set(this PasswordBoxElement element, Action<PasswordBox> setter) => element.AddSetter(setter);
    public static ButtonElement Set(this ButtonElement element, Action<Button> setter) => element.AddSetter(setter);
    public static RadioButtonsElement Set(this RadioButtonsElement element, Action<RadioButtons> setter) => element.AddSetter(setter);
    public static RadioButtonElement Set(this RadioButtonElement element, Action<RadioButton> setter) => element.AddSetter(setter);
    public static CheckBoxElement Set(this CheckBoxElement element, Action<CheckBox> setter) => element.AddSetter(setter);
    public static ToggleSwitchElement Set(this ToggleSwitchElement element, Action<ToggleSwitch> setter) => element.AddSetter(setter);
    public static SliderElement Set(this SliderElement element, Action<Slider> setter) => element.AddSetter(setter);
    public static ColorPickerElement Set(this ColorPickerElement element, Action<ColorPicker> setter) => element.AddSetter(setter);
    public static ComboBoxElement Set(this ComboBoxElement element, Action<ComboBox> setter) => element.AddSetter(setter);
    public static ImageElement Set(this ImageElement element, Action<Image> setter) => element.AddSetter(setter);
    public static BorderElement Set(this BorderElement element, Action<Border> setter) => element.AddSetter(setter);
    public static ProgressRingElement Set(this ProgressRingElement element, Action<ProgressRing> setter) => element.AddSetter(setter);
    public static ScrollViewElement Set(this ScrollViewElement element, Action<ScrollViewer> setter) => element.AddSetter(setter);
    public static ExpanderElement Set(this ExpanderElement element, Action<Expander> setter) => element.AddSetter(setter);
    public static T Set<T>(this T element, Action<FrameworkElement> setter) where T : Element => element.AddSetter(setter);
    public static T SetToolTip<T>(this T element, object tooltip) where T : Element =>
        element.AddSetter((Action<FrameworkElement>)(e =>
        {
            var current = ToolTipService.GetToolTip(e);
            if (!Equals(current, tooltip))
                ToolTipService.SetToolTip(e, tooltip);
        }));

    private static T Apply<T>(this T element, Action<T> change)
    {
        change(element);
        return element;
    }

    private static T AddSetter<T>(this T element, Delegate setter) where T : Element
    {
        element.Setters.Add(setter);
        return element;
    }

    private static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        var offset = value.Length == 8 ? 0 : -2;
        byte a = offset == 0 ? Convert.ToByte(value[..2], 16) : (byte)255;
        byte r = Convert.ToByte(value[(2 + offset)..(4 + offset)], 16);
        byte g = Convert.ToByte(value[(4 + offset)..(6 + offset)], 16);
        byte b = Convert.ToByte(value[(6 + offset)..(8 + offset)], 16);
        return Color.FromArgb(a, r, g, b);
    }
}

}

namespace OpenClawTray.FunctionalUI.Hosting
{

using OpenClawTray.FunctionalUI.Core;

public sealed class FunctionalHostControl : ContentControl, IDisposable
{
    private readonly UiRenderer _renderer;
    private readonly DispatcherQueue _dispatcherQueue;
    private Component? _rootComponent;
    private Func<RenderContext, Element>? _rootRender;
    private RenderContext? _rootContext;
    private int _renderPending;
    private bool _disposed;

    /// <summary>
    /// When true, the control will not auto-dispose on <c>Unloaded</c>.
    /// Set this when the host lives inside a page with
    /// <c>NavigationCacheMode="Enabled"</c> so the component tree
    /// survives page navigation.
    /// </summary>
    public bool SuppressAutoDispose { get; set; }
    internal int CachedVirtualStackControlCount => _renderer.CachedVirtualStackControlCount;

    public FunctionalHostControl()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _renderer = new UiRenderer(RequestRender);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = ThemeResources.ResolveBrush("SolidBackgroundFillColorBaseBrush");
        IsTabStop = false;
        Unloaded += (_, _) => { if (!SuppressAutoDispose) Dispose(); };
    }

    public void Mount(Component component)
    {
        if (!ReferenceEquals(_rootComponent, component) || _rootRender is not null || _rootContext is not null)
            ClearRootState();

        _rootRender = null;
        _rootContext = null;
        _rootComponent = component;
        RequestRender();
    }

    public void Mount(Func<RenderContext, Element> render, bool preserveRootContext = false)
    {
        if (!preserveRootContext || _rootComponent is not null || _rootContext is null)
        {
            ClearRootState();
            _rootContext = new RenderContext();
        }

        _rootComponent = null;
        _rootRender = render;
        RequestRender();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rootComponent?.Context.RunEffectCleanups();
        _rootContext?.RunEffectCleanups();
        _renderer.Dispose();
        Content = null;
    }

    private void ClearRootState()
    {
        _rootComponent?.Context.RunEffectCleanups();
        _rootContext?.RunEffectCleanups();
    }

    private void RequestRender()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        _dispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _renderPending, 0);

        try
        {
            var effects = new List<Action>();
            Element? tree = null;

            if (_rootRender is not null && _rootContext is not null)
            {
                _rootContext.BeginRender(RequestRender, effects.Add);
                tree = _rootRender(_rootContext);
            }
            else if (_rootComponent is not null)
            {
                _rootComponent.Context.BeginRender(RequestRender, effects.Add);
                tree = _rootComponent.Render();
            }

            if (tree is null) return;
            Content = _renderer.Render(tree, "root", effects);

            foreach (var effect in effects)
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_disposed)
                        effect();
                });
        }
        catch (Exception ex)
        {
            Content = new TextBlock
            {
                Text = ex.ToString(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16)
            };
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray",
                "functional-ui-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n");
        }
    }
}

internal sealed class UiRenderer(Action requestRender)
{
    private readonly Dictionary<string, UIElement> _controls = new();
    private readonly Dictionary<string, Component> _components = new();
    private readonly Dictionary<string, Flyout> _contentFlyouts = new();
    private readonly HashSet<string> _mountedPaths = new();
    private readonly HashSet<string> _visitedControlPaths = new();
    private readonly HashSet<string> _visitedComponentKeys = new();
    private readonly HashSet<string> _visitedContentFlyoutPaths = new();
    private readonly HashSet<string> _visitedVirtualStackPaths = new();
    private readonly Dictionary<string, string[]> _virtualStackOwnedPathPrefixes = new();

    internal int CachedVirtualStackControlCount =>
        _controls.Keys.Count(IsOwnedByVirtualStack);

    private static readonly DependencyProperty NativeIdentityProperty =
        DependencyProperty.RegisterAttached(
            "NativeIdentity",
            typeof(string),
            typeof(UiRenderer),
            new PropertyMetadata(null));

    public UIElement Render(Element element, string path, List<Action> effects)
    {
        _visitedControlPaths.Clear();
        _visitedComponentKeys.Clear();
        _visitedContentFlyoutPaths.Clear();
        _visitedVirtualStackPaths.Clear();

        var rendered = RenderElement(element, path, effects);
        PruneUnvisitedPaths();
        return rendered;
    }

    public void Dispose()
    {
        foreach (var component in _components.Values)
            component.Context.RunEffectCleanups();

        foreach (var control in _controls.Values)
            DetachChildren(control);

        _components.Clear();
        _controls.Clear();
        _contentFlyouts.Clear();
        _mountedPaths.Clear();
        _visitedVirtualStackPaths.Clear();
        _virtualStackOwnedPathPrefixes.Clear();
    }

    private UIElement RenderElement(Element element, string path, List<Action> effects)
    {
        return RenderElementCore(element, ResolveElementPath(path, element), effects);
    }

    private UIElement RenderElementCore(Element element, string path, List<Action> effects)
    {
        var control = element switch
        {
            TextBlockElement e => ConfigureTextBlock(GetOrCreate<TextBlock>(path), e),
            TextFieldElement e => ConfigureTextBox(GetOrCreate<TextBox>(path), e),
            PasswordBoxElement e => ConfigurePasswordBox(GetOrCreate<PasswordBox>(path), e),
            ButtonElement e => ConfigureButton(GetOrCreate<Button>(path), e, path, effects),
            RadioButtonElement e => ConfigureRadioButton(GetOrCreate<RadioButton>(path), e),
            RadioButtonsElement e => ConfigureRadioButtons(GetOrCreate<RadioButtons>(path), e),
            CheckBoxElement e => ConfigureCheckBox(GetOrCreate<CheckBox>(path), e),
            ToggleSwitchElement e => ConfigureToggleSwitch(GetOrCreate<ToggleSwitch>(path), e),
            ProgressRingElement e => ConfigureProgressRing(GetOrCreate<ProgressRing>(path), e),
            SliderElement e => ConfigureSlider(GetOrCreate<Slider>(path), e),
            ColorPickerElement e => ConfigureColorPicker(GetOrCreate<ColorPicker>(path), e),
            ComboBoxElement e => ConfigureComboBox(GetOrCreate<ComboBox>(path), e),
            ImageElement e => ConfigureImage(GetOrCreate<Image>(path), e),
            BorderElement e => ConfigureBorder(GetOrCreate<Border>(path), e, path, effects),
            StackElement e => ConfigureStack(GetOrCreate<Border>(path), e, path, effects),
            VirtualStackElement e => ConfigureVirtualStack(GetOrCreate<Border>(path), e, path, effects),
            FlexRowElement e => ConfigureFlexRow(GetOrCreate<Border>(path), e, path, effects),
            GridElement e => ConfigureGrid(GetOrCreate<Border>(path), e, path, effects),
            ScrollViewElement e => ConfigureScrollView(GetOrCreate<ScrollViewer>(path), e, path, effects),
            ExpanderElement e => ConfigureExpander(GetOrCreate<Expander>(path), e, path, effects),
            NativeElement e => ConfigureNative(GetOrCreate<ContentControl>(path), e),
            ComponentElement e => RenderComponent(e, path, effects),
            INavigationHostElement e => RenderNavigationHost(e, path, effects),
            _ => throw new NotSupportedException($"Unsupported functional UI element: {element.GetType().Name}")
        };

        QueueMount(control, element, path, effects);
        return control;
    }

    private static string ResolveElementPath(string path, Element element) =>
        string.IsNullOrEmpty(element.Key) ? path : path + "#" + element.Key;

    /// <summary>
    /// Renders a navigation host into a STABLE Border wrapper at <paramref name="path"/>
    /// whose Child is swapped to the current route's content on every render. This
    /// prevents stale route UIElements from leaking into the parent panel — previously
    /// the navigation host returned the inner content directly to its parent's
    /// SyncChildren, which made route transitions correct only as a side effect of the
    /// parent's clear-and-re-add loop and could leave the previous page's UI visible
    /// alongside the new page in some re-render orderings (see the LocalSetupProgress →
    /// Wizard overlap bug).
    /// </summary>
    private UIElement RenderNavigationHost(INavigationHostElement element, string path, List<Action> effects)
    {
        var wrapper = GetOrCreate<Border>(path);
        var child = RenderElement(element.RenderCurrentRoute(), path + ".route", effects);
        SetChild(wrapper, child);
        if (element is Element e)
        {
            ApplyModifiers(wrapper, e);
            ApplySetters(wrapper, e);
        }
        return wrapper;
    }

    private T GetOrCreate<T>(string path) where T : UIElement, new()
    {
        _visitedControlPaths.Add(path);

        if (_controls.TryGetValue(path, out var existing) && existing is T typed)
            return typed;

        if (existing is not null)
        {
            _mountedPaths.Remove(path);
            DetachChildren(existing);
            RemoveFromParent(existing);
        }

        var control = new T();
        _controls[path] = control;
        return control;
    }

    private void QueueMount(UIElement control, Element element, string path, List<Action> effects)
    {
        if (element.Modifiers.OnMount is null || control is not FrameworkElement fe || !_mountedPaths.Add(path))
            return;

        effects.Add(() => element.Modifiers.OnMount(fe));
    }

    private UIElement RenderComponent(ComponentElement element, string path, List<Action> effects)
    {
        var componentKey = GetComponentKey(element.ComponentType);
        var key = path + ":" + componentKey;
        _visitedComponentKeys.Add(key);

        if (!_components.TryGetValue(key, out var component))
        {
            component = (Component)Activator.CreateInstance(element.ComponentType)!;
            _components[key] = component;
        }

        if (element.Props is not null && component is IPropsReceiver receiver)
            receiver.SetProps(element.Props);

        component.Context.BeginRender(requestRender, effects.Add);
        return RenderElement(component.Render(), $"{path}.{componentKey}.child", effects);
    }

    private static string GetComponentKey(Type componentType) =>
        componentType.FullName?
            .Replace('.', '_')
            .Replace('+', '_')
            .Replace('`', '_')
        ?? componentType.Name;

    private TextBlock ConfigureTextBlock(TextBlock control, TextBlockElement element)
    {
        // Skip Text assignment when element text is empty and the control
        // has Inlines (populated by a setter, e.g. SafeMarkdownText).
        // Setting Text on a TextBlock with Inlines clears them.
        if (!(string.IsNullOrEmpty(element.Text) && control.Inlines.Count > 0))
        {
            if (control.Text != element.Text)
                control.Text = element.Text;
        }
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private TextBox ConfigureTextBox(TextBox control, TextFieldElement element)
    {
        control.TextChanged -= TextBoxTextChanged;
        control.GotFocus -= TextBoxGotFocus;
        control.Tag = element;
        if (control.Text != element.Value)
            control.Text = element.Value;
        control.PlaceholderText = element.Placeholder ?? "";
        control.Header = element.Header;
        control.TextChanged += TextBoxTextChanged;
        if (element.Modifiers.GotFocus is not null)
            control.GotFocus += TextBoxGotFocus;
        ApplyModifiers(control, element);
        control.IsReadOnly = element.Modifiers.ReadOnly == true;
        ApplySetters(control, element);
        return control;
    }

    private PasswordBox ConfigurePasswordBox(PasswordBox control, PasswordBoxElement element)
    {
        control.PasswordChanged -= PasswordBoxPasswordChanged;
        control.Tag = element;
        if (control.Password != element.Password)
            control.Password = element.Password;
        control.PlaceholderText = element.Placeholder ?? "";
        control.PasswordChanged += PasswordBoxPasswordChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Button ConfigureButton(Button control, ButtonElement element, string path, List<Action> effects)
    {
        control.Tag = element;
        control.Click -= ButtonClick;
        control.Click += ButtonClick;
        control.Content = element.ContentElement is null
            ? element.Label
            : RenderElement(element.ContentElement, path + ".content", effects);
        control.Flyout = element.Flyout is null ? null : CreateFlyout(element.Flyout, path + ".flyout", effects);
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private RadioButton ConfigureRadioButton(RadioButton control, RadioButtonElement element)
    {
        control.Checked -= RadioButtonChecked;
        control.Tag = element;
        control.Content = element.Label;
        control.GroupName = element.GroupName;
        control.IsChecked = element.IsChecked;
        control.Checked += RadioButtonChecked;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private RadioButtons ConfigureRadioButtons(RadioButtons control, RadioButtonsElement element)
    {
        control.SelectionChanged -= RadioButtonsSelectionChanged;
        control.Tag = element;

        // Only reassign ItemsSource when the items have actually changed (content comparison).
        // Setting ItemsSource to a new array reference with the same content causes the
        // WinUI RadioButtons control to rebuild its children and reset the visual selection,
        // which makes SelectedIndex assignments unreliable and causes selection to not stick.
        var currentItems = control.ItemsSource as string[];
        var needsItemUpdate = currentItems == null
            || currentItems.Length != element.Items.Length
            || !currentItems.AsSpan().SequenceEqual(element.Items);

        if (needsItemUpdate)
        {
            control.ItemsSource = element.Items;
        }

        if (element.SelectedIndex >= 0 && element.SelectedIndex < element.Items.Length)
        {
            control.SelectedIndex = element.SelectedIndex;
            control.SelectedItem = element.Items[element.SelectedIndex];
        }
        else
        {
            control.SelectedIndex = -1;
            control.SelectedItem = null;
        }
        control.SelectionChanged += RadioButtonsSelectionChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private CheckBox ConfigureCheckBox(CheckBox control, CheckBoxElement element)
    {
        control.Checked -= CheckBoxChanged;
        control.Unchecked -= CheckBoxChanged;
        control.Tag = element;
        control.Content = element.Label;
        control.IsChecked = element.IsChecked;
        control.Checked += CheckBoxChanged;
        control.Unchecked += CheckBoxChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private ToggleSwitch ConfigureToggleSwitch(ToggleSwitch control, ToggleSwitchElement element)
    {
        control.Toggled -= ToggleSwitchToggled;
        control.Tag = element;
        control.IsOn = element.IsOn;
        control.OnContent = element.OnContent;
        control.OffContent = element.OffContent;
        control.Header = element.Header;
        control.Toggled += ToggleSwitchToggled;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private ProgressRing ConfigureProgressRing(ProgressRing control, ProgressRingElement element)
    {
        control.IsActive = true;
        control.IsIndeterminate = element.Value is null;
        if (element.Value is not null)
            control.Value = element.Value.Value;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Slider ConfigureSlider(Slider control, SliderElement element)
    {
        control.ValueChanged -= SliderValueChanged;
        control.Tag = element;
        control.Minimum = element.Minimum;
        control.Maximum = element.Maximum;
        if (Math.Abs(control.Value - element.Value) > double.Epsilon)
            control.Value = element.Value;
        control.ValueChanged += SliderValueChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private ColorPicker ConfigureColorPicker(ColorPicker control, ColorPickerElement element)
    {
        control.ColorChanged -= ColorPickerColorChanged;
        control.Tag = element;
        control.Color = element.Value;
        control.ColorChanged += ColorPickerColorChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private ComboBox ConfigureComboBox(ComboBox control, ComboBoxElement element)
    {
        control.SelectionChanged -= ComboBoxSelectionChanged;
        control.Tag = element;
        var currentItems = control.ItemsSource as string[];
        var needsItemUpdate = currentItems == null
            || currentItems.Length != element.Items.Length
            || !currentItems.AsSpan().SequenceEqual(element.Items);
        if (needsItemUpdate)
            control.ItemsSource = element.Items;
        control.SelectedIndex = element.SelectedIndex >= 0 && element.SelectedIndex < element.Items.Length
            ? element.SelectedIndex
            : -1;
        control.SelectionChanged += ComboBoxSelectionChanged;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Image ConfigureImage(Image control, ImageElement element)
    {
        var sourceUri = new Uri(element.Source);
        if (control.Source is not BitmapImage existingSource || existingSource.UriSource != sourceUri)
            control.Source = new BitmapImage(sourceUri);

        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Border ConfigureBorder(Border control, BorderElement element, string path, List<Action> effects)
    {
        var child = element.Child is null ? null : RenderElement(element.Child, path + ".child", effects);
        if (child is not null)
            SetChild(control, child);
        else
            control.Child = null;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Border ConfigureStack(Border wrapper, StackElement element, string path, List<Action> effects)
    {
        var panel = GetOrCreate<StackPanel>(path + ".panel");
        panel.Orientation = element.Orientation;
        panel.Spacing = element.Spacing;
        SyncChildren(panel, element.Children, path, effects);
        SetChild(wrapper, panel);
        ApplyModifiers(wrapper, element);
        ApplySetters(panel, element);
        return wrapper;
    }

    private Border ConfigureVirtualStack(Border wrapper, VirtualStackElement element, string path, List<Action> effects)
    {
        var repeater = GetOrCreate<ItemsRepeater>(path + ".repeater");
        _visitedVirtualStackPaths.Add(path);
        if (repeater.Layout is not StackLayout layout)
        {
            layout = new StackLayout();
            repeater.Layout = layout;
        }
        layout.Orientation = element.Orientation;
        layout.Spacing = element.Spacing;

        var items = element.Children
            .Select((child, index) => child is null ? null : new VirtualStackItem(
                index,
                ResolveElementPath(ChildPath(path, index, child), child),
                child))
            .Where(item => item is not null)
            .Cast<VirtualStackItem>()
            .ToArray();
        _virtualStackOwnedPathPrefixes[path] = items.Select(item => item.Path).ToArray();

        if (repeater.ItemsSource is VirtualStackItem[] currentItems && VirtualStackItemsMatch(currentItems, items))
        {
            for (var i = 0; i < currentItems.Length; i++)
            {
                currentItems[i].Index = items[i].Index;
                currentItems[i].Element = items[i].Element;
            }
            UpdateRealizedVirtualStackItems(currentItems, effects);
        }
        else
        {
            repeater.ItemsSource = items;
        }

        if (repeater.ItemTemplate is not VirtualStackItemTemplate template || !template.Matches(this, path))
            repeater.ItemTemplate = new VirtualStackItemTemplate(this, path);

        repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
        repeater.VerticalAlignment = VerticalAlignment.Stretch;

        SetChild(wrapper, repeater);
        ApplyModifiers(wrapper, element);
        ApplySetters(repeater, element);
        return wrapper;
    }

    private void UpdateRealizedVirtualStackItems(IEnumerable<VirtualStackItem> items, List<Action> effects)
    {
        foreach (var item in items)
        {
            if (item.RealizedContainer is null)
                continue;

            var child = RenderElementCore(item.Element, item.Path, effects);
            SetChild(item.RealizedContainer, child);
            PruneUnvisitedCachedSubtree(item.Path);
        }
    }

    private static bool VirtualStackItemsMatch(VirtualStackItem[] currentItems, VirtualStackItem[] nextItems)
    {
        if (currentItems.Length != nextItems.Length)
            return false;

        for (var i = 0; i < currentItems.Length; i++)
        {
            if (!string.Equals(currentItems[i].Path, nextItems[i].Path, StringComparison.Ordinal))
                return false;
            if (currentItems[i].Element.GetType() != nextItems[i].Element.GetType())
                return false;
            if (currentItems[i].Element is ComponentElement currentComponent
                && nextItems[i].Element is ComponentElement nextComponent
                && currentComponent.ComponentType != nextComponent.ComponentType)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class VirtualStackItem(int index, string path, Element element)
    {
        public int Index { get; set; } = index;
        public string Path { get; } = path;
        public Element Element { get; set; } = element;
        public Border? RealizedContainer { get; set; }
    }

    private sealed class VirtualStackItemTemplate(UiRenderer renderer, string path) : IElementFactory
    {
        private readonly Dictionary<UIElement, VirtualStackItem> _realizedItems = new();

        public bool Matches(UiRenderer otherRenderer, string otherPath) =>
            ReferenceEquals(renderer, otherRenderer) && string.Equals(path, otherPath, StringComparison.Ordinal);

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not VirtualStackItem item)
                return new Border();

            var container = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
            var effects = new List<Action>();
            var child = renderer.RenderElementCore(item.Element, item.Path, effects);
            renderer.SetChild(container, child);
            item.RealizedContainer = container;
            _realizedItems[container] = item;
            foreach (var effect in effects)
                effect();
            return container;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not { } control || !_realizedItems.Remove(control, out var item))
                return;

            if (ReferenceEquals(item.RealizedContainer, control))
                item.RealizedContainer = null;
            renderer.RemoveCachedSubtree(item.Path);
        }
    }

    private void PruneUnvisitedCachedSubtree(string prefix)
    {
        foreach (var (key, component) in _components
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix) && !_visitedComponentKeys.Contains(pair.Key))
                     .ToArray())
        {
            component.Context.RunEffectCleanups();
            _components.Remove(key);
        }

        foreach (var (path, flyout) in _contentFlyouts
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix) && !_visitedContentFlyoutPaths.Contains(pair.Key))
                     .ToArray())
        {
            flyout.Hide();
            flyout.Content = null;
            _contentFlyouts.Remove(path);
        }

        foreach (var (path, cachedControl) in _controls
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix) && !_visitedControlPaths.Contains(pair.Key))
                     .OrderByDescending(pair => pair.Key.Length)
                     .ToArray())
        {
            _mountedPaths.Remove(path);
            DetachChildren(cachedControl);
            RemoveFromParent(cachedControl);
            _controls.Remove(path);
        }
    }

    private void RemoveCachedSubtree(string prefix)
    {
        foreach (var (key, component) in _components
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix))
                     .ToArray())
        {
            component.Context.RunEffectCleanups();
            _components.Remove(key);
        }

        foreach (var (path, flyout) in _contentFlyouts
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix))
                     .ToArray())
        {
            flyout.Hide();
            _contentFlyouts.Remove(path);
        }

        foreach (var (path, cachedControl) in _controls
                     .Where(pair => IsPathAtOrBelow(pair.Key, prefix))
                     .OrderByDescending(pair => pair.Key.Length)
                     .ToArray())
        {
            _mountedPaths.Remove(path);
            DetachChildren(cachedControl);
            RemoveFromParent(cachedControl);
            _controls.Remove(path);
        }
    }

    private Border ConfigureFlexRow(Border wrapper, FlexRowElement element, string path, List<Action> effects)
    {
        var panel = GetOrCreate<StackPanel>(path + ".panel");
        panel.Orientation = Orientation.Horizontal;
        panel.Spacing = element.ColumnGap;
        SyncChildren(panel, element.Children, path, effects);
        SetChild(wrapper, panel);
        ApplyModifiers(wrapper, element);
        ApplySetters(panel, element);
        return wrapper;
    }

    private Border ConfigureGrid(Border wrapper, GridElement element, string path, List<Action> effects)
    {
        var grid = GetOrCreate<WinGrid>(path + ".grid");
        SyncGridDefinitions(grid.ColumnDefinitions, element.Columns, s => new ColumnDefinition { Width = ParseGridLength(s) }, cd => cd.Width);
        SyncGridDefinitions(grid.RowDefinitions, element.Rows, s => new RowDefinition { Height = ParseGridLength(s) }, rd => rd.Height);
        SyncChildren(grid, element.Children, path, effects);
        SetChild(wrapper, grid);
        ApplyModifiers(wrapper, element);
        ApplySetters(grid, element);
        return wrapper;
    }

    /// <summary>
    /// Updates grid column/row definitions only when they actually differ,
    /// avoiding unnecessary layout invalidation that causes cursor/background
    /// flickering on re-render.
    /// </summary>
    private static void SyncGridDefinitions<TDef>(
        IList<TDef> existing,
        IReadOnlyList<string> desired,
        Func<string, TDef> create,
        Func<TDef, GridLength> getLength)
    {
        if (existing.Count == desired.Count)
        {
            var match = true;
            for (var i = 0; i < desired.Count; i++)
            {
                var want = ParseGridLength(desired[i]);
                var have = getLength(existing[i]);
                if (want.GridUnitType != have.GridUnitType || Math.Abs(want.Value - have.Value) > double.Epsilon)
                {
                    match = false;
                    break;
                }
            }
            if (match) return;
        }

        existing.Clear();
        foreach (var d in desired)
            existing.Add(create(d));
    }

    private ScrollViewer ConfigureScrollView(ScrollViewer control, ScrollViewElement element, string path, List<Action> effects)
    {
        var child = element.Child is null ? null : RenderElement(element.Child, path + ".content", effects);
        if (child is not null)
        {
            if (!ReferenceEquals(control.Content, child))
            {
                RemoveFromParent(child);
                control.Content = child;
            }
        }
        else
        {
            control.Content = null;
        }
        control.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        control.HorizontalScrollMode = element.Modifiers.HorizontalScrollMode ?? ScrollMode.Auto;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private Expander ConfigureExpander(Expander control, ExpanderElement element, string path, List<Action> effects)
    {
        var child = element.Child is null ? null : RenderElement(element.Child, path + ".content", effects);
        if (child is not null)
        {
            if (!ReferenceEquals(control.Content, child))
            {
                RemoveFromParent(child);
                control.Content = child;
            }
        }
        else
        {
            control.Content = null;
        }
        control.Header = element.Header;
        control.IsExpanded = element.IsExpanded;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private ContentControl ConfigureNative(ContentControl wrapper, NativeElement element)
    {
        var currentIdentity = (string?)wrapper.GetValue(NativeIdentityProperty);
        if (wrapper.Content is not FrameworkElement native
            || !string.Equals(currentIdentity, element.Identity, StringComparison.Ordinal))
        {
            DisposeNativeContent(wrapper.Content);
            native = element.Create();
            native.HorizontalAlignment = HorizontalAlignment.Stretch;
            native.VerticalAlignment = VerticalAlignment.Stretch;
            wrapper.Content = native;
            wrapper.SetValue(NativeIdentityProperty, element.Identity);
        }

        wrapper.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        wrapper.VerticalContentAlignment = VerticalAlignment.Stretch;
        element.Configure?.Invoke(native);
        ApplyModifiers(wrapper, element);
        ApplySetters(native, element);
        return wrapper;
    }

    private static void DisposeNativeContent(object? content)
    {
        if (content is IDisposable disposable)
            disposable.Dispose();
    }

    private FlyoutBase CreateFlyout(FlyoutElement element, string path, List<Action> effects)
    {
        return element switch
        {
            ContentFlyoutElement content => CreateContentFlyout(content, path, effects),
            MenuFlyoutContentElement menu => CreateMenuFlyout(menu),
            _ => throw new NotSupportedException($"Unsupported functional UI flyout: {element.GetType().Name}")
        };
    }

    private Flyout CreateContentFlyout(ContentFlyoutElement element, string path, List<Action> effects)
    {
        _visitedContentFlyoutPaths.Add(path);

        // Cache the Flyout instance per path so its identity is STABLE across
        // re-renders. ConfigureButton reassigns control.Flyout on every render,
        // and a full-root re-render fires on every state change (including the
        // flyout content's own search box). If we minted a `new Flyout()` each
        // time, an already-open flyout would (a) become unreachable from
        // control.Flyout (so callers' `Flyout.Hide()` would target an orphan and
        // no-op) and (b) have its reused content control reparented out from
        // under the visible popup. Reusing the same Flyout keeps it dismissable
        // and stops the open popup from being torn apart mid-interaction.
        if (!_contentFlyouts.TryGetValue(path, out var flyout))
        {
            flyout = new Flyout();
            _contentFlyouts[path] = flyout;
        }

        flyout.Placement = element.Placement;
        var content = RenderElement(element.Content, path + ".content", effects);
        if (!ReferenceEquals(flyout.Content, content))
        {
            RemoveFromParent(content);
            flyout.Content = content;
        }
        return flyout;
    }

    private void PruneUnvisitedPaths()
    {
        foreach (var path in _virtualStackOwnedPathPrefixes.Keys.ToArray())
        {
            if (!_visitedVirtualStackPaths.Contains(path))
                _virtualStackOwnedPathPrefixes.Remove(path);
        }

        foreach (var (key, component) in _components.ToArray())
        {
            if (_visitedComponentKeys.Contains(key) || IsOwnedByVirtualStack(key))
                continue;

            component.Context.RunEffectCleanups();
            _components.Remove(key);
        }

        foreach (var (path, flyout) in _contentFlyouts.ToArray())
        {
            if (_visitedContentFlyoutPaths.Contains(path) || IsOwnedByVirtualStack(path))
                continue;

            flyout.Hide();
            flyout.Content = null;
            _contentFlyouts.Remove(path);
        }

        foreach (var (path, control) in _controls.ToArray())
        {
            if (_visitedControlPaths.Contains(path) || IsOwnedByVirtualStack(path))
                continue;

            _mountedPaths.Remove(path);
            DetachChildren(control);
            RemoveFromParent(control);
            _controls.Remove(path);
        }
    }

    private bool IsOwnedByVirtualStack(string path)
    {
        foreach (var prefixes in _virtualStackOwnedPathPrefixes.Values)
        {
            foreach (var prefix in prefixes)
            {
                if (IsPathAtOrBelow(path, prefix))
                    return true;
            }
        }

        return false;
    }

    private static bool IsPathAtOrBelow(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + ".", StringComparison.Ordinal)
        || path.StartsWith(prefix + ":", StringComparison.Ordinal);

    private static MenuFlyout CreateMenuFlyout(MenuFlyoutContentElement element)
    {
        var flyout = new MenuFlyout { Placement = element.Placement };
        foreach (var item in element.Items)
            flyout.Items.Add(CreateMenuFlyoutItem(item));
        return flyout;
    }

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutItemBase CreateMenuFlyoutItem(OpenClawTray.FunctionalUI.Core.MenuFlyoutItemBase item)
    {
        switch (item)
        {
            case MenuFlyoutItemData data:
                var menuItem = new MenuFlyoutItem
                {
                    Text = data.Text,
                    IsEnabled = data.IsEnabled,
                    Tag = data
                };
                if (data.Padding is { } padding)
                    menuItem.Padding = padding;
                if (data.FontWeight is { } weight)
                    menuItem.FontWeight = weight;
                menuItem.Click += MenuFlyoutItemClick;
                return menuItem;
            case RadioMenuFlyoutItemData data:
                var radioItem = new RadioMenuFlyoutItem
                {
                    Text = data.Text,
                    GroupName = data.GroupName,
                    IsChecked = data.IsChecked,
                    Tag = data
                };
                radioItem.Click += RadioMenuFlyoutItemClick;
                return radioItem;
            case MenuFlyoutSeparatorData:
                return new MenuFlyoutSeparator();
            default:
                throw new NotSupportedException($"Unsupported menu flyout item: {item.GetType().Name}");
        }
    }

    private static GridLength ParseGridLength(string value)
    {
        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            return GridLength.Auto;
        if (value.EndsWith("*", StringComparison.Ordinal))
        {
            var n = value[..^1];
            return new GridLength(string.IsNullOrWhiteSpace(n) ? 1 : double.Parse(n, CultureInfo.InvariantCulture), GridUnitType.Star);
        }
        return new GridLength(double.Parse(value, CultureInfo.InvariantCulture), GridUnitType.Pixel);
    }


    private void SyncChildren(Panel panel, IReadOnlyList<Element?> elements, string path, List<Action> effects)
    {
        var renderedChildren = elements
            .Select((e, i) => e is null ? null : new RenderedChild(e, RenderElement(e, ChildPath(path, i, e), effects)))
            .Where(child => child is not null)
            .Cast<RenderedChild>()
            .ToList();

        foreach (var renderedChild in renderedChildren)
        {
            if (renderedChild.Control is FrameworkElement fe && renderedChild.Element.GridPosition is { } pos)
            {
                WinGrid.SetRow(fe, pos.Row);
                WinGrid.SetColumn(fe, pos.Column);
                WinGrid.SetRowSpan(fe, pos.RowSpan);
                WinGrid.SetColumnSpan(fe, pos.ColumnSpan);
            }
        }

        var desiredChildren = renderedChildren.Select(child => child.Control).ToArray();
        if (ChildrenMatch(panel, desiredChildren))
            return;

        // Fast path: when panel is empty (e.g. pre-cleared), just add children
        // directly. Avoids EnsureChildAt → RemoveFromParent which scans ALL
        // cached controls — O(controls × children) with no benefit when
        // children have no parent.
        if (panel.Children.Count == 0)
        {
            foreach (var child in desiredChildren)
            {
                if (child is FrameworkElement { Parent: { } })
                    RemoveFromParent(child);
                panel.Children.Add(child);
            }
            return;
        }

        var desiredSet = new HashSet<UIElement>(desiredChildren);
        for (var i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(panel.Children[i]))
                panel.Children.RemoveAt(i);
        }

        for (var i = 0; i < desiredChildren.Length; i++)
            EnsureChildAt(panel, desiredChildren[i], i);
    }

    private sealed record RenderedChild(Element Element, UIElement Control);

    private static string ChildPath(string parentPath, int index, Element element) =>
        string.IsNullOrEmpty(element.Key)
            ? $"{parentPath}.{index}"
            : $"{parentPath}.key:{element.Key}";

    private static bool ChildrenMatch(Panel panel, UIElement[] desiredChildren)
    {
        if (panel.Children.Count != desiredChildren.Length)
            return false;

        for (var i = 0; i < desiredChildren.Length; i++)
        {
            if (!ReferenceEquals(panel.Children[i], desiredChildren[i]))
                return false;
        }

        return true;
    }

    private void EnsureChildAt(Panel panel, UIElement child, int index)
    {
        var currentIndex = panel.Children.IndexOf(child);
        if (currentIndex == index)
            return;

        if (currentIndex >= 0)
        {
            panel.Children.RemoveAt(currentIndex);
            if (currentIndex < index)
                index--;
        }
        else
        {
            RemoveFromParent(child);
        }

        panel.Children.Insert(index, child);
    }

    private void SetChild(Border wrapper, UIElement child)
    {
        if (ReferenceEquals(wrapper.Child, child))
            return;

        RemoveFromParent(child);
        wrapper.Child = child;
    }

    private void RemoveFromParent(UIElement element)
    {
        if (element is FrameworkElement { Parent: Panel panel })
            panel.Children.Remove(element);
        else if (element is FrameworkElement { Parent: Border border } && ReferenceEquals(border.Child, element))
            border.Child = null;
        else if (element is FrameworkElement { Parent: ScrollViewer scrollViewer } && ReferenceEquals(scrollViewer.Content, element))
            scrollViewer.Content = null;

        foreach (var control in _controls.Values)
        {
            if (ReferenceEquals(control, element))
                continue;

            switch (control)
            {
                case Panel knownPanel:
                    for (var i = knownPanel.Children.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(knownPanel.Children[i], element))
                            knownPanel.Children.RemoveAt(i);
                    }
                    break;

                case Border knownBorder when ReferenceEquals(knownBorder.Child, element):
                    knownBorder.Child = null;
                    break;

                case ScrollViewer knownScrollViewer when ReferenceEquals(knownScrollViewer.Content, element):
                    knownScrollViewer.Content = null;
                    break;

                case ContentControl knownContentControl when ReferenceEquals(knownContentControl.Content, element):
                    knownContentControl.Content = null;
                    break;
            }
        }
    }

    private static void DetachChildren(UIElement element)
    {
        switch (element)
        {
            case Panel panel:
                panel.Children.Clear();
                break;
            case Border border:
                border.Child = null;
                break;
            case ScrollViewer scrollViewer:
                scrollViewer.Content = null;
                break;
            case ContentControl contentControl:
                DisposeNativeContent(contentControl.Content);
                contentControl.Content = null;
                break;
        }
    }

    private static void ApplyModifiers(FrameworkElement control, Element element)
    {
        var m = element.Modifiers;
        control.Tag = element;
        if (m.Margin is { } margin) control.Margin = margin;
        else control.ClearValue(FrameworkElement.MarginProperty);
        if (m.Width is { } width) control.Width = width;
        else control.ClearValue(FrameworkElement.WidthProperty);
        if (m.Height is { } height) control.Height = height;
        else control.ClearValue(FrameworkElement.HeightProperty);
        if (m.MinWidth is { } minWidth) control.MinWidth = minWidth;
        else control.ClearValue(FrameworkElement.MinWidthProperty);
        if (m.MaxWidth is { } maxWidth) control.MaxWidth = maxWidth;
        else control.ClearValue(FrameworkElement.MaxWidthProperty);
        if (m.MinHeight is { } minHeight) control.MinHeight = minHeight;
        else control.ClearValue(FrameworkElement.MinHeightProperty);
        if (m.MaxHeight is { } maxHeight) control.MaxHeight = maxHeight;
        else control.ClearValue(FrameworkElement.MaxHeightProperty);
        if (m.HorizontalAlignment is { } hAlign) control.HorizontalAlignment = hAlign;
        else control.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        if (m.VerticalAlignment is { } vAlign) control.VerticalAlignment = vAlign;
        else control.ClearValue(FrameworkElement.VerticalAlignmentProperty);
        if (m.Opacity is { } opacity) control.Opacity = opacity;
        else control.ClearValue(UIElement.OpacityProperty);
        if (m.AutomationName is { } automationName) AutomationProperties.SetName(control, automationName);
        else control.ClearValue(AutomationProperties.NameProperty);
        if (m.LiveRegion is { } liveRegion) AutomationProperties.SetLiveSetting(control, liveRegion);
        else control.ClearValue(AutomationProperties.LiveSettingProperty);
        ApplyResourceOverrides(control, m.ResourceOverrides);
        if (control is Control disabledControl)
        {
            if (m.Disabled is { } disabled)
                disabledControl.IsEnabled = !disabled;
            else
                disabledControl.ClearValue(Control.IsEnabledProperty);
        }

        control.KeyDown -= ElementKeyDown;
        if (m.KeyDown is not null) control.KeyDown += ElementKeyDown;
        control.PointerEntered -= ElementPointerEntered;
        if (m.PointerEntered is not null) control.PointerEntered += ElementPointerEntered;
        control.PointerExited -= ElementPointerExited;
        if (m.PointerExited is not null) control.PointerExited += ElementPointerExited;

        switch (control)
        {
            case TextBlock tb:
                if (m.FontSize is { } textSize) tb.FontSize = textSize;
                else tb.ClearValue(TextBlock.FontSizeProperty);
                if (m.FontWeight is { } textWeight) tb.FontWeight = textWeight;
                else tb.ClearValue(TextBlock.FontWeightProperty);
                if (m.FontFamily is { } textFamily) tb.FontFamily = textFamily;
                else tb.ClearValue(TextBlock.FontFamilyProperty);
                if (m.TextWrapping is { } wrapping) tb.TextWrapping = wrapping;
                else tb.ClearValue(TextBlock.TextWrappingProperty);
                if (m.Padding is { } textPadding) tb.Padding = textPadding;
                else tb.ClearValue(TextBlock.PaddingProperty);
                if (m.ForegroundResourceKey is { } textFgResource) tb.Foreground = ThemeResources.ResolveBrush(textFgResource);
                else if (m.Foreground is { } textFg) tb.Foreground = textFg;
                else tb.ClearValue(TextBlock.ForegroundProperty);
                tb.ClearValue(TextBlock.TextTrimmingProperty);
                tb.ClearValue(TextBlock.MaxLinesProperty);
                tb.ClearValue(TextBlock.LineHeightProperty);
                tb.ClearValue(TextBlock.CharacterSpacingProperty);
                break;
            case Control c:
                if (m.Padding is { } controlPadding) c.Padding = controlPadding;
                else c.ClearValue(Control.PaddingProperty);
                if (m.FontSize is { } controlSize) c.FontSize = controlSize;
                else c.ClearValue(Control.FontSizeProperty);
                if (m.FontWeight is { } controlWeight) c.FontWeight = controlWeight;
                else c.ClearValue(Control.FontWeightProperty);
                if (m.FontFamily is { } controlFamily) c.FontFamily = controlFamily;
                else c.ClearValue(Control.FontFamilyProperty);
                if (m.ForegroundResourceKey is { } controlFgResource) c.Foreground = ThemeResources.ResolveBrush(controlFgResource);
                else if (m.Foreground is { } controlFg) c.Foreground = controlFg;
                else c.ClearValue(Control.ForegroundProperty);
                if (m.BorderBrushResourceKey is { } controlBorderResource) c.BorderBrush = ThemeResources.ResolveBrush(controlBorderResource);
                else if (m.BorderBrush is { } controlBorder) c.BorderBrush = controlBorder;
                else c.ClearValue(Control.BorderBrushProperty);
                if (m.BorderThickness is { } controlThickness) c.BorderThickness = controlThickness;
                else c.ClearValue(Control.BorderThicknessProperty);
                break;
            case Border b:
                if (m.Padding is { } borderPadding) b.Padding = borderPadding;
                else b.ClearValue(Border.PaddingProperty);
                if (m.BackgroundResourceKey is { } backgroundResourceKey)
                    b.Background = ThemeResources.ResolveBrush(backgroundResourceKey);
                else if (m.Background is { } bg)
                    b.Background = bg;
                else b.ClearValue(Border.BackgroundProperty);
                if (m.BorderBrushResourceKey is { } borderResourceKey)
                    b.BorderBrush = ThemeResources.ResolveBrush(borderResourceKey);
                else if (m.BorderBrush is { } borderBrush)
                    b.BorderBrush = borderBrush;
                else b.ClearValue(Border.BorderBrushProperty);
                if (m.BorderThickness is { } borderThickness)
                    b.BorderThickness = borderThickness;
                else b.ClearValue(Border.BorderThicknessProperty);
                if (m.CornerRadius is { } radius) b.CornerRadius = radius;
                else b.ClearValue(Border.CornerRadiusProperty);
                break;
        }
    }

    private static void ApplyResourceOverrides(FrameworkElement control, ResourceOverrides? overrides)
    {
        if (overrides is null)
            return;

        foreach (var (key, value) in overrides.Values)
        {
            control.Resources[key] = value is ThemeRef themeRef
                ? ThemeResources.ResolveBrush(themeRef.ResourceKey)
                : value;
        }
    }

    private static void ApplySetters(FrameworkElement control, Element element)
    {
        foreach (var setter in element.Setters)
            setter.DynamicInvoke(control);
    }

    private static void TextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { Tag: TextFieldElement element } tb)
            element.OnChanged?.Invoke(tb.Text);
    }

    private static void TextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TextFieldElement element })
            element.Modifiers.GotFocus?.Invoke(sender, e);
    }

    private static void ElementKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Element element })
            element.Modifiers.KeyDown?.Invoke(sender, e);
    }

    private static void ElementPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Element element })
            element.Modifiers.PointerEntered?.Invoke(sender, e);
    }

    private static void ElementPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Element element })
            element.Modifiers.PointerExited?.Invoke(sender, e);
    }

    private static void PasswordBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { Tag: PasswordBoxElement element } pb)
            element.OnChanged?.Invoke(pb.Password);
    }

    private static void ButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ButtonElement element })
            element.OnClick?.Invoke();
    }

    private static void RadioButtonChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: RadioButtonElement element })
            element.OnChecked?.Invoke(true);
    }

    private static void RadioButtonsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is RadioButtons { Tag: RadioButtonsElement element } rb)
        {
            System.Diagnostics.Debug.WriteLine($"[WizardDiag] RadioButtons.SelectionChanged: idx={rb.SelectedIndex} itemCount={rb.Items?.Count ?? 0}");
            element.OnSelectionChanged?.Invoke(rb.SelectedIndex);
        }
    }

    private static void CheckBoxChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: CheckBoxElement element } cb)
            element.OnChanged?.Invoke(cb.IsChecked == true);
    }

    private static void ToggleSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: ToggleSwitchElement element } ts)
            element.OnChanged?.Invoke(ts.IsOn);
    }

    private static void SliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { Tag: SliderElement element } slider)
            element.OnChanged?.Invoke(slider.Value);
    }

    private static void ColorPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (sender.Tag is ColorPickerElement element)
            element.OnChanged?.Invoke(args.NewColor);
    }

    private static void ComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: ComboBoxElement element } combo)
            element.OnSelectionChanged?.Invoke(combo.SelectedIndex);
    }

    private static void MenuFlyoutItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: MenuFlyoutItemData data })
            data.OnClick?.Invoke();
    }

    private static void RadioMenuFlyoutItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: RadioMenuFlyoutItemData data })
            data.OnClick?.Invoke();
    }
}
}
