using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace OpenClawTray.FunctionalUI.Core
{

public abstract record Element
{
    public ElementModifiers Modifiers { get; } = new();
    public GridPosition? GridPosition { get; set; }
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
}

public sealed record GridPosition(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

public sealed record TextBlockElement(string Text) : Element;
public sealed record TextFieldElement(string Value, Action<string>? OnChanged, string? Placeholder, string? Header) : Element;
public sealed record PasswordBoxElement(string Password, Action<string>? OnChanged, string? Placeholder) : Element;
public sealed record ButtonElement(string Label, Action? OnClick, Element? ContentElement = null) : Element;
public sealed record RadioButtonElement(string Label, bool IsChecked, Action<bool>? OnChecked, string? GroupName) : Element;
public sealed record RadioButtonsElement(string[] Items, int SelectedIndex, Action<int>? OnSelectionChanged) : Element;
public sealed record CheckBoxElement(bool IsChecked, Action<bool>? OnChanged, string? Label) : Element;
public sealed record ToggleSwitchElement(bool IsOn, Action<bool>? OnChanged, string? OnContent, string? OffContent, string? Header) : Element;
public sealed record ProgressRingElement(double? Value) : Element;
public sealed record BorderElement(Element? Child) : Element;
public sealed record StackElement(Orientation Orientation, double Spacing, IReadOnlyList<Element?> Children) : Element;
public sealed record GridElement(string[] Columns, string[] Rows, IReadOnlyList<Element?> Children) : Element;
public sealed record ScrollViewElement(Element? Child) : Element;
public sealed record ComponentElement(Type ComponentType, object? Props) : Element;
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
    public object[] Dependencies = [];
    public Action? Cleanup;
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

        if (!DependenciesChanged(hook.Dependencies, dependencies))
            return;

        var oldCleanup = hook.Cleanup;
        hook.Dependencies = dependencies.ToArray();
        _afterRender?.Invoke(() =>
        {
            oldCleanup?.Invoke();
            hook.Cleanup = effectWithCleanup();
        });
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
    public static TextBlockElement TextBlock(string text) => new(text);
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
    public static BorderElement Border(Element? child = null) => new(child);
    public static StackElement VStack(params Element?[] children) => new(Orientation.Vertical, 0, children);
    public static StackElement VStack(double spacing, params Element?[] children) => new(Orientation.Vertical, spacing, children);
    public static StackElement HStack(params Element?[] children) => new(Orientation.Horizontal, 0, children);
    public static StackElement HStack(double spacing, params Element?[] children) => new(Orientation.Horizontal, spacing, children);
    public static GridElement Grid(string[] columns, string[] rows, params Element?[] children) => new(columns, rows, children);
    public static ScrollViewElement ScrollView(Element? child) => new(child);
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
    public static T Grid<T>(this T element, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : Element =>
        element.Apply(e => e.GridPosition = new GridPosition(row, column, rowSpan, columnSpan));
    public static T OnGotFocus<T>(this T element, RoutedEventHandler handler) where T : Element =>
        element.Apply(e => e.Modifiers.GotFocus = handler);

    public static TextBlockElement Set(this TextBlockElement element, Action<TextBlock> setter) => element.AddSetter(setter);
    public static TextFieldElement Set(this TextFieldElement element, Action<TextBox> setter) => element.AddSetter(setter);
    public static PasswordBoxElement Set(this PasswordBoxElement element, Action<PasswordBox> setter) => element.AddSetter(setter);
    public static ButtonElement Set(this ButtonElement element, Action<Button> setter) => element.AddSetter(setter);
    public static RadioButtonsElement Set(this RadioButtonsElement element, Action<RadioButtons> setter) => element.AddSetter(setter);
    public static RadioButtonElement Set(this RadioButtonElement element, Action<RadioButton> setter) => element.AddSetter(setter);
    public static CheckBoxElement Set(this CheckBoxElement element, Action<CheckBox> setter) => element.AddSetter(setter);
    public static ToggleSwitchElement Set(this ToggleSwitchElement element, Action<ToggleSwitch> setter) => element.AddSetter(setter);
    public static BorderElement Set(this BorderElement element, Action<Border> setter) => element.AddSetter(setter);
    public static T Set<T>(this T element, Action<FrameworkElement> setter) where T : Element => element.AddSetter(setter);
    public static T SetToolTip<T>(this T element, object tooltip) where T : Element =>
        element.AddSetter((Action<FrameworkElement>)(e => ToolTipService.SetToolTip(e, tooltip)));

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

    public FunctionalHostControl()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _renderer = new UiRenderer(RequestRender);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        IsTabStop = false;
        Unloaded += (_, _) => Dispose();
    }

    public void Mount(Component component)
    {
        _rootRender = null;
        _rootContext = null;
        _rootComponent = component;
        RequestRender();
    }

    public void Mount(Func<RenderContext, Element> render)
    {
        _rootComponent = null;
        _rootRender = render;
        _rootContext = new RenderContext();
        RequestRender();
    }

    public void Dispose()
    {
        _disposed = true;
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
                _dispatcherQueue.TryEnqueue(() => effect());
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

    public UIElement Render(Element element, string path, List<Action> effects)
    {
        return RenderElement(element, path, effects);
    }

    private UIElement RenderElement(Element element, string path, List<Action> effects)
    {
        return element switch
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
            BorderElement e => ConfigureBorder(GetOrCreate<Border>(path), e, path, effects),
            StackElement e => ConfigureStack(GetOrCreate<Border>(path), e, path, effects),
            GridElement e => ConfigureGrid(GetOrCreate<Border>(path), e, path, effects),
            ScrollViewElement e => ConfigureScrollView(GetOrCreate<ScrollViewer>(path), e, path, effects),
            ComponentElement e => RenderComponent(e, path, effects),
            INavigationHostElement e => RenderElement(e.RenderCurrentRoute(), path + ".route", effects),
            _ => throw new NotSupportedException($"Unsupported functional UI element: {element.GetType().Name}")
        };
    }

    private T GetOrCreate<T>(string path) where T : UIElement, new()
    {
        if (_controls.TryGetValue(path, out var existing) && existing is T typed)
            return typed;

        if (existing is not null)
        {
            DetachChildren(existing);
            RemoveFromParent(existing);
        }

        var control = new T();
        _controls[path] = control;
        return control;
    }

    private UIElement RenderComponent(ComponentElement element, string path, List<Action> effects)
    {
        var componentKey = GetComponentKey(element.ComponentType);
        var key = path + ":" + componentKey;
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
        control.Text = element.Text;
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
        control.ItemsSource = element.Items;
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

    private Border ConfigureBorder(Border control, BorderElement element, string path, List<Action> effects)
    {
        var child = element.Child is null ? null : RenderElement(element.Child, path + ".child", effects);
        if (child is not null)
            RemoveFromParent(child);
        control.Child = child;
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
        ApplySetters(wrapper, element);
        return wrapper;
    }

    private Border ConfigureGrid(Border wrapper, GridElement element, string path, List<Action> effects)
    {
        var grid = GetOrCreate<WinGrid>(path + ".grid");
        grid.ColumnDefinitions.Clear();
        foreach (var col in element.Columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = ParseGridLength(col) });
        grid.RowDefinitions.Clear();
        foreach (var row in element.Rows)
            grid.RowDefinitions.Add(new RowDefinition { Height = ParseGridLength(row) });
        SyncChildren(grid, element.Children, path, effects);
        SetChild(wrapper, grid);
        ApplyModifiers(wrapper, element);
        ApplySetters(wrapper, element);
        return wrapper;
    }

    private ScrollViewer ConfigureScrollView(ScrollViewer control, ScrollViewElement element, string path, List<Action> effects)
    {
        var child = element.Child is null ? null : RenderElement(element.Child, path + ".content", effects);
        if (child is not null)
            RemoveFromParent(child);
        control.Content = child;
        control.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        control.HorizontalScrollMode = element.Modifiers.HorizontalScrollMode ?? ScrollMode.Auto;
        ApplyModifiers(control, element);
        ApplySetters(control, element);
        return control;
    }

    private static GridLength ParseGridLength(string value)
    {
        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            return GridLength.Auto;
        if (value.EndsWith("*", StringComparison.Ordinal))
        {
            var n = value[..^1];
            return new GridLength(string.IsNullOrWhiteSpace(n) ? 1 : double.Parse(n), GridUnitType.Star);
        }
        return new GridLength(double.Parse(value), GridUnitType.Pixel);
    }

    private void SyncChildren(Panel panel, IReadOnlyList<Element?> elements, string path, List<Action> effects)
    {
        var renderedChildren = elements
            .Select((e, i) => e is null ? null : new RenderedChild(e, RenderElement(e, $"{path}.{i}", effects)))
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

        panel.Children.Clear();
        foreach (var child in renderedChildren.Select(child => child.Control))
        {
            RemoveFromParent(child);
            panel.Children.Add(child);
        }
    }

    private sealed record RenderedChild(Element Element, UIElement Control);

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
                contentControl.Content = null;
                break;
        }
    }

    private static void ApplyModifiers(FrameworkElement control, Element element)
    {
        var m = element.Modifiers;
        if (m.Margin is { } margin) control.Margin = margin;
        if (m.Width is { } width) control.Width = width;
        if (m.Height is { } height) control.Height = height;
        if (m.MinWidth is { } minWidth) control.MinWidth = minWidth;
        if (m.MaxWidth is { } maxWidth) control.MaxWidth = maxWidth;
        if (m.MinHeight is { } minHeight) control.MinHeight = minHeight;
        if (m.MaxHeight is { } maxHeight) control.MaxHeight = maxHeight;
        if (m.HorizontalAlignment is { } hAlign) control.HorizontalAlignment = hAlign;
        if (m.VerticalAlignment is { } vAlign) control.VerticalAlignment = vAlign;
        if (m.Opacity is { } opacity) control.Opacity = opacity;
        if (m.Disabled is { } disabled && control is Control disabledControl)
            disabledControl.IsEnabled = !disabled;

        switch (control)
        {
            case TextBlock tb:
                if (m.FontSize is { } textSize) tb.FontSize = textSize;
                if (m.FontWeight is { } textWeight) tb.FontWeight = textWeight;
                if (m.FontFamily is { } textFamily) tb.FontFamily = textFamily;
                if (m.TextWrapping is { } wrapping) tb.TextWrapping = wrapping;
                if (m.Padding is { } textPadding) tb.Padding = textPadding;
                break;
            case Control c:
                if (m.Padding is { } controlPadding) c.Padding = controlPadding;
                if (m.FontSize is { } controlSize) c.FontSize = controlSize;
                if (m.FontWeight is { } controlWeight) c.FontWeight = controlWeight;
                if (m.FontFamily is { } controlFamily) c.FontFamily = controlFamily;
                break;
            case Border b:
                if (m.Padding is { } borderPadding) b.Padding = borderPadding;
                if (m.Background is { } bg) b.Background = bg;
                if (m.CornerRadius is { } radius) b.CornerRadius = radius;
                break;
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
            element.OnSelectionChanged?.Invoke(rb.SelectedIndex);
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
}
}
