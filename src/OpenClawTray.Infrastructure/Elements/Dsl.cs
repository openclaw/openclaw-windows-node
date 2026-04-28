using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Localization;
using OpenClawTray.Infrastructure.Markdown;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Text;
using Windows.UI.Text;
using MenuFlyoutItemBase = OpenClawTray.Infrastructure.Core.MenuFlyoutItemBase;

namespace OpenClawTray.Infrastructure;

// AI-HINT: This is the main DSL entry point. All Reactor UI is built via:
//   using static OpenClawTray.Infrastructure.Factories;
// Factory methods return Element records (virtual DOM), never real WinUI controls.
// Organization: Text → Buttons → Input → Layout → Navigation → Dialogs → Data → Media → Markdown.
// Layout helpers: VStack/HStack/Grid/Canvas/RelativePanel produce container elements.


/// <summary>
/// Static factory methods that form the Reactor DSL.
/// Import with: using static OpenClawTray.Infrastructure.Factories;
///
/// This gives you a clean, declarative syntax:
///   VStack(
///       TextBlock("Hello").Bold(),
///       Button("Click me", () => setCount(count + 1)),
///       count > 5 ? TextBlock("Wow!") : null
///   )
/// </summary>
public static partial class Factories
{
    // ── Localization ──────────────────────────────────────────────────

    public static Element LocaleProvider(string locale, Element child,
        Localization.IStringResourceProvider? resourceProvider = null,
        string defaultLocale = "en-US",
        bool pseudoLocalize = false) =>
        Component<Localization.LocaleProviderComponent, Localization.LocaleProviderElement>(
            new Localization.LocaleProviderElement(locale, child, resourceProvider, defaultLocale, pseudoLocalize));

    // ── Text ────────────────────────────────────────────────────────

    public static TextBlockElement TextBlock(string content) => new(content);

    public static TextBlockElement Heading(string content) =>
        new(content) { FontSize = 28, Weight = new global::Windows.UI.Text.FontWeight(700),
            Modifiers = new Core.ElementModifiers
            {
                HeadingLevel = Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1
            } };

    public static TextBlockElement SubHeading(string content) =>
        new(content) { FontSize = 20, Weight = new global::Windows.UI.Text.FontWeight(600),
            Modifiers = new Core.ElementModifiers
            {
                HeadingLevel = Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2
            } };

    public static TextBlockElement Caption(string content) =>
        new(content) { FontSize = 12 };

    public static RichTextBlockElement RichText(string text) => new(text);

    public static RichEditBoxElement RichEditBox(string text = "", Action<string>? onTextChanged = null) =>
        new(text) { OnTextChanged = onTextChanged };

    // ── Buttons ─────────────────────────────────────────────────────

    public static ButtonElement Button(string label, Action? onClick = null) =>
        new(label, onClick);

    public static ButtonElement Button(Element content, Action? onClick = null) =>
        new("", onClick) { ContentElement = content };

    /// <summary>
    /// Creates a Button driven by a Command. Maps Label → Content, Execute → Click,
    /// IsEnabled → IsEnabled. Description / Accelerator / AccessKey are wired via
    /// a Setter so per-site overrides win via the normal modifier ordering.
    /// </summary>
    public static ButtonElement Button(Core.Command command) =>
        new ButtonElement(command.Label, () => Core.CommandBindings.Invoke(command))
        {
            IsEnabled = command.IsEnabled,
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    public static HyperlinkButtonElement HyperlinkButton(string content, Uri? navigateUri = null, Action? onClick = null) =>
        new(content, navigateUri, onClick);

    /// <summary>
    /// Creates a HyperlinkButton driven by a Command. Maps Label → Content, Execute →
    /// Click. For external navigation combine with <c>.NavigateUri(...)</c> via
    /// <c>.Set()</c>.
    /// </summary>
    public static HyperlinkButtonElement HyperlinkButton(Core.Command command) =>
        new HyperlinkButtonElement(command.Label, null, () => Core.CommandBindings.Invoke(command))
        {
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    public static RepeatButtonElement RepeatButton(string label, Action? onClick = null) =>
        new(label, onClick);

    /// <summary>Creates a RepeatButton driven by a Command. Click auto-repeats while held.</summary>
    public static RepeatButtonElement RepeatButton(Core.Command command) =>
        new RepeatButtonElement(command.Label, () => Core.CommandBindings.Invoke(command))
        {
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    public static ToggleButtonElement ToggleButton(string label, bool isChecked = false, Action<bool>? onToggled = null) =>
        new(label, isChecked, onToggled);

    /// <summary>
    /// Creates a ToggleButton driven by a Command. The command fires on each toggle
    /// (both check and uncheck) — per the spec's "Option A" semantics. Use the
    /// <c>isChecked</c> parameter to seed the initial state.
    /// </summary>
    public static ToggleButtonElement ToggleButton(Core.Command command, bool isChecked = false) =>
        new ToggleButtonElement(command.Label, isChecked, _ => Core.CommandBindings.Invoke(command))
        {
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    public static DropDownButtonElement DropDownButton(string label, Element? flyout = null) =>
        new(label, flyout);

    public static SplitButtonElement SplitButton(string label, Action? onClick = null, Element? flyout = null) =>
        new(label, onClick, flyout);

    /// <summary>
    /// Creates a SplitButton driven by a Command for the primary action. The flyout
    /// (dropdown portion) is independent and supplied separately.
    /// </summary>
    public static SplitButtonElement SplitButton(Core.Command command, Element? flyout = null) =>
        new SplitButtonElement(command.Label, () => Core.CommandBindings.Invoke(command), flyout)
        {
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    public static ToggleSplitButtonElement ToggleSplitButton(string label, bool isChecked = false, Action<bool>? onIsCheckedChanged = null, Element? flyout = null) =>
        new(label, isChecked, onIsCheckedChanged, flyout);

    /// <summary>Creates a ToggleSplitButton driven by a Command (fires on each toggle).</summary>
    public static ToggleSplitButtonElement ToggleSplitButton(Core.Command command, bool isChecked = false, Element? flyout = null) =>
        new ToggleSplitButtonElement(command.Label, isChecked, _ => Core.CommandBindings.Invoke(command), flyout)
        {
            Setters = [b => Core.CommandBindings.ApplyButtonBaseCommon(b, command)],
        };

    // ── Input controls ──────────────────────────────────────────────

    public static TextFieldElement TextField(string value, Action<string>? onChanged = null, string? placeholder = null, string? header = null) =>
        new(value, onChanged, placeholder) { Header = header };

    public static PasswordBoxElement PasswordBox(string password, Action<string>? onPasswordChanged = null, string? placeholderText = null) =>
        new(password, onPasswordChanged, placeholderText);

    public static NumberBoxElement NumberBox(double value, Action<double>? onValueChanged = null, string? header = null) =>
        new(value, onValueChanged, header);

    public static AutoSuggestBoxElement AutoSuggestBox(string text, Action<string>? onTextChanged = null, Action<string>? onQuerySubmitted = null) =>
        new(text, onTextChanged, onQuerySubmitted);

    public static CheckBoxElement CheckBox(bool isChecked, Action<bool>? onChanged = null, string? label = null) =>
        new(isChecked, onChanged, label);

    public static CheckBoxElement ThreeStateCheckBox(bool? checkedState, Action<bool?>? onCheckedStateChanged = null, string? label = null) =>
        new(checkedState == true, Label: label) { IsThreeState = true, CheckedState = checkedState, OnCheckedStateChanged = onCheckedStateChanged };

    public static RadioButtonElement RadioButton(string label, bool isChecked = false, Action<bool>? onChecked = null, string? groupName = null) =>
        new(label, isChecked, onChecked, groupName);

    public static RadioButtonsElement RadioButtons(string[] items, int selectedIndex = -1, Action<int>? onSelectionChanged = null) =>
        new(items, selectedIndex, onSelectionChanged);

    public static ComboBoxElement ComboBox(string[] items, int selectedIndex = -1, Action<int>? onSelectionChanged = null) =>
        new(items, selectedIndex, onSelectionChanged);

    public static ComboBoxElement ComboBox(Element[] itemElements, int selectedIndex, Action<int>? onSelectionChanged) =>
        new([], selectedIndex, onSelectionChanged) { ItemElements = itemElements };

    public static SliderElement Slider(double value, double min = 0, double max = 100, Action<double>? onChanged = null) =>
        new(value, min, max, onChanged);

    public static ToggleSwitchElement ToggleSwitch(bool isOn, Action<bool>? onChanged = null, string? onContent = null, string? offContent = null, string? header = null) =>
        new(isOn, onChanged, onContent, offContent) { Header = header };

    public static RatingControlElement RatingControl(double value = 0, Action<double>? onValueChanged = null) =>
        new(value, onValueChanged);

    public static ColorPickerElement ColorPicker(global::Windows.UI.Color color, Action<global::Windows.UI.Color>? onColorChanged = null) =>
        new(color, onColorChanged);

    // ── Date / Time ─────────────────────────────────────────────────

    public static CalendarDatePickerElement CalendarDatePicker(DateTimeOffset? date = null, Action<DateTimeOffset?>? onDateChanged = null) =>
        new(date, onDateChanged);

    public static DatePickerElement DatePicker(DateTimeOffset date, Action<DateTimeOffset>? onDateChanged = null) =>
        new(date, onDateChanged);

    public static TimePickerElement TimePicker(TimeSpan time, Action<TimeSpan>? onTimeChanged = null) =>
        new(time, onTimeChanged);

    // ── Progress ────────────────────────────────────────────────────

    public static ProgressElement Progress(double value) => new(value);
    public static ProgressElement ProgressIndeterminate() => new(null);

    public static ProgressRingElement ProgressRing() => new(null);
    public static ProgressRingElement ProgressRing(double value) => new(value);

    // ── Status / Info ───────────────────────────────────────────────

    public static InfoBarElement InfoBar(string? title = null, string? message = null) => new(title, message);

    public static InfoBadgeElement InfoBadge() => new();
    public static InfoBadgeElement InfoBadge(int value) => new() { Value = value };

    // ── Layout ──────────────────────────────────────────────────────

    public static StackElement VStack(params Element?[] children) =>
        new(Orientation.Vertical, FilterChildren(children));

    public static StackElement VStack(double spacing, params Element?[] children) =>
        new(Orientation.Vertical, FilterChildren(children)) { Spacing = spacing };

    public static StackElement HStack(params Element?[] children) =>
        new(Orientation.Horizontal, FilterChildren(children));

    public static StackElement HStack(double spacing, params Element?[] children) =>
        new(Orientation.Horizontal, FilterChildren(children)) { Spacing = spacing };

    public static WrapGridElement WrapGrid(params Element?[] children) =>
        new(FilterChildren(children));

    public static WrapGridElement WrapGrid(int maxRowsOrColumns, params Element?[] children) =>
        new(FilterChildren(children)) { MaximumRowsOrColumns = maxRowsOrColumns };

    public static ScrollViewElement ScrollView(Element child) => new(child);

    public static BorderElement Border(Element child) => new(child);

    public static ExpanderElement Expander(string header, Element content, bool isExpanded = false, Action<bool>? onExpandedChanged = null) =>
        new(header, content, isExpanded, onExpandedChanged);

    public static SplitViewElement SplitView(Element? pane = null, Element? content = null) =>
        new(pane, content);

    public static ViewboxElement Viewbox(Element child) => new(child);

    public static CanvasElement Canvas(params Element?[] children) => new(FilterChildren(children));

    // ── Grid ────────────────────────────────────────────────────────

    public static GridElement Grid(
        string[] columns, string[] rows,
        params Element?[] children) =>
        new(new GridDefinition(columns, rows), FilterChildren(children));

    // ── Grid layout builders ────────────────────────────────────────

    /// <summary>
    /// Creates a grid with items interspersed with separator elements along one axis.
    /// Commonly used for split panels where children are separated by splitters.
    ///
    /// Each item gets a proportional (*) size from <paramref name="proportions"/>,
    /// and separators get a fixed pixel size of <paramref name="separatorSize"/>.
    ///
    /// Example: InterspersedGrid(Orientation.Horizontal, children, proportions, 6,
    ///              i => MySplitter(i))
    /// produces columns: "0.33*", "6", "0.33*", "6", "0.34*" with children and splitters placed.
    /// </summary>
    public static GridElement InterspersedGrid(
        Orientation orientation,
        Element[] items,
        double[] proportions,
        double separatorSize,
        Func<int, Element> separatorFactory)
    {
        if (items.Length == 0) return Grid([], [], []);
        if (items.Length != proportions.Length)
            throw new ArgumentException("items and proportions must have the same length");
        for (int i = 0; i < proportions.Length; i++)
        {
            if (proportions[i] < 0 || double.IsNaN(proportions[i]))
                throw new ArgumentOutOfRangeException(nameof(proportions), $"proportions[{i}] must be a non-negative number, got {proportions[i]}");
        }

        var sizes = new List<string>();
        var children = new List<Element>();
        bool isHorizontal = orientation == Orientation.Horizontal;

        for (int i = 0; i < items.Length; i++)
        {
            var starValue = proportions[i];
            sizes.Add(string.Format(global::System.Globalization.CultureInfo.InvariantCulture, "{0:F6}*", starValue));

            children.Add(isHorizontal
                ? items[i].Grid(row: 0, column: i * 2)
                : items[i].Grid(row: i * 2, column: 0));

            if (i < items.Length - 1)
            {
                sizes.Add($"{separatorSize}");
                var sep = separatorFactory(i);
                children.Add(isHorizontal
                    ? sep.Grid(row: 0, column: i * 2 + 1)
                    : sep.Grid(row: i * 2 + 1, column: 0));
            }
        }

        return isHorizontal
            ? Grid(sizes.ToArray(), ["*"], children.ToArray())
            : Grid(["*"], sizes.ToArray(), children.ToArray());
    }

    /// <summary>
    /// Creates a uniform grid with equal-sized cells along one axis.
    /// Shorthand for a grid where all items share equal proportions with no separators.
    /// </summary>
    public static GridElement UniformGrid(Orientation orientation, params Element?[] items)
    {
        var filtered = FilterChildren(items);
        if (filtered.Length == 0) return Grid([], [], []);

        var sizes = Enumerable.Repeat("*", filtered.Length).ToArray();
        bool isHorizontal = orientation == Orientation.Horizontal;

        for (int i = 0; i < filtered.Length; i++)
        {
            filtered[i] = isHorizontal
                ? filtered[i].Grid(row: 0, column: i)
                : filtered[i].Grid(row: i, column: 0);
        }

        return isHorizontal
            ? Grid(sizes, ["*"], filtered)
            : Grid(["*"], sizes, filtered);
    }

    // ── Navigation ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a navigation host that renders the current route's content.
    /// Automatically provides the navigation handle via context so child components
    /// can retrieve it with <c>UseNavigation&lt;TRoute&gt;()</c>.
    /// Use <c>with { }</c> to set Transition, CacheMode, and CacheSize.
    /// </summary>
    public static NavigationHostElement NavigationHost<TRoute>(
        Navigation.NavigationHandle<TRoute> nav,
        Func<TRoute, Element> routeMap) where TRoute : notnull
    {
        return new NavigationHostElement(nav, route => routeMap((TRoute)route))
            .Provide(Navigation.NavigationContext<TRoute>.Instance, nav);
    }

    public static NavigationViewElement NavigationView(NavigationViewItemData[] menuItems, Element? content = null) =>
        new(menuItems, content);

    public static NavigationViewItemData NavItem(string content, string? icon = null, string? tag = null) =>
        new(content, icon, tag);

    public static NavigationViewItemData NavItemHeader(string content) =>
        new(content) { IsHeader = true };

    public static TitleBarElement TitleBar(string title) => new(title);

    public static TabViewElement TabView(params TabViewItemData[] tabs) => new(tabs);

    public static TabViewItemData Tab(string header, Element content) => new(header, content);

    public static BreadcrumbBarElement BreadcrumbBar(BreadcrumbBarItemData[] items, Action<BreadcrumbBarItemData>? onItemClicked = null) =>
        new(items, onItemClicked);

    public static BreadcrumbBarItemData Breadcrumb(string label, object? tag = null) => new(label, tag);

    public static PivotElement Pivot(params PivotItemData[] items) => new(items);

    public static PivotItemData PivotItem(string header, Element content) => new(header, content);

    // ── Collections ─────────────────────────────────────────────────

    public static ListViewElement ListView(params Element[] items) => new(items);

    public static GridViewElement GridView(params Element[] items) => new(items);

    public static TreeViewElement TreeView(params TreeViewNodeData[] nodes) => new(nodes);

    public static TreeViewNodeData TreeNode(string content, params TreeViewNodeData[] children) =>
        new(content, children.Length > 0 ? children : null);

    public static FlipViewElement FlipView(params Element[] items) => new(items);

    // ── Dialogs / Overlays ──────────────────────────────────────────

    public static ContentDialogElement ContentDialog(string title, Element content, string primaryButtonText = "OK") =>
        new(title, content, primaryButtonText);

    public static FlyoutElement Flyout(Element target, Element flyoutContent) =>
        new(target, flyoutContent);

    public static TeachingTipElement TeachingTip(string title, string? subtitle = null) =>
        new(title, subtitle);

    public static ContentFlyoutElement ContentFlyout(Element content, FlyoutPlacementMode placement = FlyoutPlacementMode.Auto) =>
        new(content) { Placement = placement };

    public static MenuFlyoutContentElement MenuItems(params MenuFlyoutItemBase[] items) =>
        new(items);

    public static MenuFlyoutContentElement MenuItems(FlyoutPlacementMode placement, params MenuFlyoutItemBase[] items) =>
        new(items) { Placement = placement };

    // ── Menus ───────────────────────────────────────────────────────

    public static MenuBarElement MenuBar(params MenuBarItemData[] items) => new(items);

    public static MenuBarItemData Menu(string title, params MenuFlyoutItemBase[] items) => new(title, items);

    public static MenuFlyoutItemData MenuItem(string text, Action? onClick = null, string? icon = null) => new(text, onClick, icon);

    /// <summary>
    /// Creates a MenuFlyoutItem driven by a Command. Maps Label → Text, Icon,
    /// Execute → OnClick, Accelerator, IsEnabled, AccessKey.
    /// </summary>
    public static MenuFlyoutItemData MenuItem(Core.Command command) =>
        new(command.Label, command.Execute)
        {
            IsEnabled = command.IsEnabled,
            IconElement = command.Icon,
            KeyboardAccelerators = command.Accelerator is not null ? [command.Accelerator] : null,
            AccessKey = command.AccessKey,
            Description = command.Description,
        };

    /// <summary>
    /// Creates a MenuFlyoutItem driven by a parameterized Command. Wraps the action
    /// to invoke with the bound parameter.
    /// </summary>
    public static MenuFlyoutItemData MenuItem<T>(Core.Command<T> command, T parameter) =>
        new(command.Label, command.Execute is not null ? () => command.Execute(parameter) : null)
        {
            IsEnabled = command.IsEnabled,
            IconElement = command.Icon,
            KeyboardAccelerators = command.Accelerator is not null ? [command.Accelerator] : null,
            AccessKey = command.AccessKey,
            Description = command.Description,
        };

    public static ToggleMenuFlyoutItemData ToggleMenuItem(string text, bool isChecked = false, Action<bool>? onToggled = null, string? icon = null) => new(text, isChecked, onToggled, icon);

    public static RadioMenuFlyoutItemData RadioMenuItem(string text, string groupName, bool isChecked = false, Action? onClick = null, string? icon = null) => new(text, groupName, isChecked, onClick, icon);

    public static MenuFlyoutSeparatorData MenuSeparator() => new();

    public static MenuFlyoutSubItemData MenuSubItem(string text, params MenuFlyoutItemBase[] items) => new(text, items);

    public static MenuFlyoutElement MenuFlyout(Element target, params MenuFlyoutItemBase[] items) => new(target, items);

    public static CommandBarElement CommandBar(AppBarItemBase[]? primaryCommands = null, AppBarItemBase[]? secondaryCommands = null) =>
        new(primaryCommands, secondaryCommands);

    public static AppBarButtonData AppBarButton(string label, Action? onClick = null, string? icon = null) => new(label, onClick, icon);

    /// <summary>
    /// Creates an AppBarButton driven by a Command. Maps Label, Icon, Execute,
    /// Accelerator, IsEnabled, AccessKey, and Description.
    /// </summary>
    public static AppBarButtonData AppBarButton(Core.Command command) =>
        new(command.Label, command.Execute)
        {
            IsEnabled = command.IsEnabled,
            IconElement = command.Icon,
            KeyboardAccelerators = command.Accelerator is not null ? [command.Accelerator] : null,
            AccessKey = command.AccessKey,
            Description = command.Description,
        };

    public static AppBarToggleButtonData AppBarToggleButton(string label, bool isChecked = false, Action<bool>? onToggled = null, string? icon = null) =>
        new(label, isChecked, onToggled, icon);

    public static AppBarSeparatorData AppBarSeparator() => new();

    // ── Media ───────────────────────────────────────────────────────

    public static ImageElement Image(string source) => new(source);

    public static PersonPictureElement PersonPicture() => new();

    public static WebView2Element WebView2(Uri? source = null) => new(source);

    // ── Components ──────────────────────────────────────────────────

    /// <summary>
    /// Embed a Component class as a child element.
    /// Usage: Component&lt;MyWidget&gt;()
    /// </summary>
    public static ComponentElement Component<T>() where T : Component, new() =>
        new(typeof(T)) { _factory = () => new T() };

    /// <summary>
    /// Embed a Component class with typed props as a child element.
    /// Usage: Component&lt;MyWidget, string&gt;("param")
    /// </summary>
    public static ComponentElement Component<T, TProps>(TProps props)
        where T : Component<TProps>, new() =>
        new(typeof(T), props) { _factory = () => new T() };

    /// <summary>
    /// Define an inline function component (like a React function component).
    /// Usage: Func(ctx => { var (n,setN) = ctx.UseState(0); return TextBlock($"{n}"); })
    /// </summary>
    public static FuncElement Func(Func<RenderContext, Element> render) => new(render);

    /// <summary>
    /// Define a memoized inline function component. Skips re-render when dependencies haven't changed.
    /// Empty deps array = render once + own state changes only. Non-empty = re-render when any dep changes.
    /// Usage: Memo(ctx => TextBlock("stable"), someProp, otherProp)
    /// </summary>
    public static MemoElement Memo(Func<RenderContext, Element> render, params object?[] dependencies)
        => new(render, dependencies.Length == 0 ? null : dependencies);

    // ── Command host ─────────────────────────────────────────────────

    /// <summary>
    /// Scopes keyboard accelerators from the given commands to the child subtree.
    /// Only commands with an Accelerator produce keyboard accelerators on the host element.
    /// </summary>
    public static Core.CommandHostElement CommandHost(Core.Command[] commands, Element child) =>
        new(commands, child);

    // ── Conditional helpers ─────────────────────────────────────────

    /// <summary>
    /// Renders element only when condition is true. Reads nicely:
    ///   When(items.Any(), () =&gt; TextBlock("Has items"))
    /// </summary>
    public static Element When(bool condition, Func<Element> then) =>
        condition ? then() : EmptyElement.Instance;

    /// <summary>
    /// If/else as an expression:
    ///   If(loggedIn, () =&gt; TextBlock("Welcome"), () =&gt; Button("Login", ...))
    /// </summary>
    public static Element If(bool condition, Func<Element> then, Func<Element>? otherwise = null) =>
        condition ? then() : (otherwise?.Invoke() ?? EmptyElement.Instance);

    /// <summary>
    /// Map a list to elements (like .map() in React JSX):
    ///   ForEach(items, item =&gt; TextBlock(item.Name))
    /// </summary>
    public static Element ForEach<T>(IEnumerable<T> items, Func<T, Element> render) =>
        new GroupElement(items.Select(render).ToArray());

    /// <summary>
    /// Map with index:
    ///   ForEach(items, (item, i) =&gt; TextBlock($"{i}: {item}"))
    /// </summary>
    public static Element ForEach<T>(IEnumerable<T> items, Func<T, int, Element> render) =>
        new GroupElement(items.Select((item, i) => render(item, i)).ToArray());

    /// <summary>
    /// Groups elements without introducing a layout container (like React's Fragment).
    /// Children are flattened into the parent container.
    /// </summary>
    public static Element Group(params Element?[] children) =>
        new GroupElement(FilterChildren(children));

    /// <summary>
    /// Renders nothing. Useful as a default/fallback.
    /// </summary>
    public static Element Empty() => EmptyElement.Instance;

    /// <summary>
    /// Wraps a child subtree in an error boundary. If any component in the subtree
    /// throws during rendering, the fallback function is called with the exception.
    /// When the ErrorBoundary re-renders, it retries the child (error recovery).
    /// </summary>
    public static ErrorBoundaryElement ErrorBoundary(
        Element child, Func<Exception, Element> fallback) => new(child, fallback);

    /// <summary>
    /// Wraps a child subtree in an error boundary with a static fallback element.
    /// </summary>
    public static ErrorBoundaryElement ErrorBoundary(
        Element child, Element fallback) => new(child, _ => fallback);

    // ── Thickness helpers (WinUI lacks a (horizontal, vertical) constructor) ──

    /// <summary>
    /// Creates a Thickness with horizontal and vertical values.
    /// Usage: Thick(16, 8) → Thickness(16, 8, 16, 8)
    /// </summary>
    public static Thickness Thick(double horizontal, double vertical) =>
        new(horizontal, vertical, horizontal, vertical);

    /// <summary>
    /// Creates a uniform Thickness. Shorthand for new Thickness(uniform).
    /// </summary>
    public static Thickness Thick(double uniform) => new(uniform);

    /// <summary>
    /// Creates a Thickness with all four sides specified.
    /// </summary>
    public static Thickness Thick(double left, double top, double right, double bottom) =>
        new(left, top, right, bottom);

    // ── Typed (data-driven) collections ───────────────────────────

    public static TemplatedListViewElement<T> ListView<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    public static TemplatedGridViewElement<T> GridView<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    public static TemplatedFlipViewElement<T> FlipView<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    // ── Virtualized collections ───────────────────────────────────

    public static LazyVStackElement<T> LazyVStack<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    public static LazyHStackElement<T> LazyHStack<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    // ── Shapes ───────────────────────────────────────────────────────

    public static RectangleElement Rectangle() => new();

    public static EllipseElement Ellipse() => new();

    public static LineElement Line(double x1, double y1, double x2, double y2) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    public static PathElement Path() => new();

    // ── Additional layout ───────────────────────────────────────────

    public static RelativePanelElement RelativePanel(params Element?[] children) => new(FilterChildren(children));

    // ── Additional media ────────────────────────────────────────────

    public static MediaPlayerElementElement MediaPlayerElement(string? source = null) => new(source);

    public static AnimatedVisualPlayerElement AnimatedVisualPlayer() => new();

    // ── Additional collections ──────────────────────────────────────

    public static SemanticZoomElement SemanticZoom(Element zoomedInView, Element zoomedOutView) =>
        new(zoomedInView, zoomedOutView);

    public static ListBoxElement ListBox(string[] items, int selectedIndex = -1, Action<int>? onSelectionChanged = null) =>
        new(items) { SelectedIndex = selectedIndex, OnSelectionChanged = onSelectionChanged };

    // ── Additional navigation ───────────────────────────────────────

    public static SelectorBarElement SelectorBar(SelectorBarItemData[] items, int selectedIndex = 0, Action<int>? onSelectionChanged = null) =>
        new(items) { SelectedIndex = selectedIndex, OnSelectionChanged = onSelectionChanged };

    public static SelectorBarItemData SelectorBarItem(string text, string? icon = null) => new(text, icon);

    public static PipsPagerElement PipsPager(int numberOfPages, int selectedPageIndex = 0, Action<int>? onSelectedIndexChanged = null) =>
        new(numberOfPages) { SelectedPageIndex = selectedPageIndex, OnSelectedIndexChanged = onSelectedIndexChanged };

    public static AnnotatedScrollBarElement AnnotatedScrollBar() => new();

    // ── Additional overlays / containers ────────────────────────────

    public static PopupElement Popup(Element child, bool isOpen = false, Action? onClosed = null) =>
        new(child) { IsOpen = isOpen, OnClosed = onClosed };

    public static RefreshContainerElement RefreshContainer(Element content, Action? onRefreshRequested = null) =>
        new(content) { OnRefreshRequested = onRefreshRequested };

    public static CommandBarFlyoutElement CommandBarFlyout(Element target, AppBarItemBase[]? primaryCommands = null, AppBarItemBase[]? secondaryCommands = null) =>
        new(target, primaryCommands, secondaryCommands);

    // ── Additional date / time ──────────────────────────────────────

    public static CalendarViewElement CalendarView() => new();

    // ── SwipeControl ────────────────────────────────────────────────

    public static SwipeControlElement SwipeControl(Element content,
        SwipeItemData[]? leftItems = null, SwipeItemData[]? rightItems = null) =>
        new(content) { LeftItems = leftItems, RightItems = rightItems };

    // ── AnimatedIcon ────────────────────────────────────────────────

    public static AnimatedIconElement AnimatedIcon(object? source = null, IconSource? fallbackIconSource = null) =>
        new() { Source = source, FallbackIconSource = fallbackIconSource };

    // ── ParallaxView ────────────────────────────────────────────────

    public static ParallaxViewElement ParallaxView(Element child, double verticalShift = 0, double horizontalShift = 0) =>
        new(child) { VerticalShift = verticalShift, HorizontalShift = horizontalShift };

    // ── MapControl ──────────────────────────────────────────────────

    public static MapControlElement MapControl(string? mapServiceToken = null, double zoomLevel = 1) =>
        new() { MapServiceToken = mapServiceToken, ZoomLevel = zoomLevel };

    // ── Frame ───────────────────────────────────────────────────────

    public static FrameElement Frame(Type? sourcePageType = null, object? navigationParameter = null) =>
        new() { SourcePageType = sourcePageType, NavigationParameter = navigationParameter };

    // ── ItemsView ───────────────────────────────────────────────────

    public static ItemsViewElement<T> ItemsView<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        Func<T, int, Element> viewBuilder) => new(items, keySelector, viewBuilder);

    // ── Rich text helpers ───────────────────────────────────────────

    public static RichTextBlockElement RichText(RichTextParagraph[] paragraphs) =>
        new("") { Paragraphs = paragraphs };

    public static RichTextParagraph Paragraph(params RichTextInline[] inlines) => new(inlines);

    public static RichTextRun Run(string text) => new(text);

    public static RichTextHyperlink Hyperlink(string text, Uri navigateUri) => new(text, navigateUri);

    // ── Markdown ─────────────────────────────────────────────────────

    /// <summary>
    /// Render a markdown string as a Reactor element tree.
    /// </summary>
    public static Element Markdown(string markdown) =>
        MarkdownBuilder.Build(markdown, null);

    /// <summary>
    /// Render a markdown string as a Reactor element tree with custom rendering options.
    /// </summary>
    public static Element Markdown(string markdown, MarkdownOptions options) =>
        MarkdownBuilder.Build(markdown, options);

    // ── Icons ────────────────────────────────────────────────────────

    public static SymbolIconData SymbolIcon(string symbol) => new(symbol);

    public static FontIconData FontIcon(string glyph, string? fontFamily = null, double? fontSize = null) =>
        new(glyph, fontFamily, fontSize);

    public static BitmapIconData BitmapIcon(global::System.Uri source, bool showAsMonochrome = true) =>
        new(source, showAsMonochrome);

    public static PathIconData PathIcon(string data) => new(data);

    public static ImageIconData ImageIcon(global::System.Uri source) => new(source);

    // ── Keyboard Accelerators ───────────────────────────────────────

    public static KeyboardAcceleratorData Accelerator(global::Windows.System.VirtualKey key, global::Windows.System.VirtualKeyModifiers modifiers = global::Windows.System.VirtualKeyModifiers.None) =>
        new(key, modifiers);

    // ── Brushes ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new AcrylicBrush. This allocates a WinRT DependencyObject on every call.
    /// On hot paths (e.g., inside Render methods), cache the result with <c>UseMemo</c>:
    /// <code>var brush = ctx.UseMemo(() => UI.AcrylicBrush(color, 0.8), color);</code>
    /// </summary>
    public static Microsoft.UI.Xaml.Media.AcrylicBrush AcrylicBrush(
        global::Windows.UI.Color tintColor,
        double tintOpacity = 0.8,
        global::Windows.UI.Color? fallbackColor = null,
        double? tintLuminosityOpacity = null)
    {
        var brush = new Microsoft.UI.Xaml.Media.AcrylicBrush
        {
            TintColor = tintColor,
            TintOpacity = tintOpacity,
        };
        if (fallbackColor.HasValue) brush.FallbackColor = fallbackColor.Value;
        if (tintLuminosityOpacity.HasValue) brush.TintLuminosityOpacity = tintLuminosityOpacity.Value;
        return brush;
    }

    // ── Internals ───────────────────────────────────────────────────

    private static Element[] FilterChildren(Element?[] children)
    {
        // Fast path: check if any nulls or GroupElements need expansion
        bool needsExpansion = false;
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is null or GroupElement or EmptyElement)
            {
                needsExpansion = true;
                break;
            }
        }
        if (!needsExpansion) return (Element[])(object)children;

        // Flatten GroupElements and remove nulls
        var result = new List<Element>();
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is GroupElement group)
            {
                foreach (var gc in group.Children)
                {
                    if (gc is not null and not EmptyElement)
                        result.Add(gc);
                }
            }
            else if (children[i] is not null and not EmptyElement)
            {
                result.Add(children[i]!);
            }
        }
        return result.ToArray();
    }
}
