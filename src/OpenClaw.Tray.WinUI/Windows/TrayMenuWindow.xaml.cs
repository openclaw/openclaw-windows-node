using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.System;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// A popup window that displays the tray menu at the cursor position.
/// Uses Win32 to remove title bar (workaround for Bug 57667927).
/// </summary>
public sealed partial class TrayMenuWindow : WindowEx
{
    private const int MenuWidthViewUnits = 320;
    private const int SubmenuWidthViewUnits = 280;

    #region Win32 Imports
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    
    // SetWindowPos flags
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private enum MonitorDpiType
    {
        MDT_EFFECTIVE_DPI = 0
    }
    #endregion

    public event EventHandler<string>? MenuItemClicked;

    private int _menuHeight = 400;
    private int _itemCount = 0;
    private int _separatorCount = 0;
    private int _headerCount = 0;
    private bool _styleApplied = false;
    private readonly TrayMenuWindow? _ownerMenu;
    private TrayMenuWindow? _activeFlyoutWindow;
    private Button? _activeFlyoutOwner;
    private string? _activeFlyoutKey;
    private string? _activeFlyoutTag;
    private readonly Dictionary<string, (Button button, IReadOnlyList<TrayMenuFlyoutItem> items)> _flyoutsByTag = new(StringComparer.Ordinal);
    private bool _isShown;
    /// <summary>True while the menu window is visible. App can use this to
    /// trigger an in-place rebuild when backing state changes mid-display.</summary>
    public bool IsShown => _isShown;
    private global::Windows.Graphics.RectInt32? _lastMoveAndResizeRect;
    private uint _lastMeasureDpi;
    private double _lastMeasureRasterizationScale;
    private int _updateDepth;

    // Cached theme brushes resolved lazily on first use, then reused for the
    // lifetime of the window. The previous implementation looked up every
    // brush via Application.Current.Resources for every row in the menu,
    // which showed up as a visible hitch on right-click. The cache also
    // collapses Microsoft.UI.Colors.Transparent allocations.
    private Brush? _subtleHoverBrush;
    private Brush? _dividerBrush;
    private Brush? _secondaryTextBrush;
    private static readonly Brush s_transparentBrush =
        new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private Brush SubtleHoverBrush =>
        _subtleHoverBrush ??= (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    private Brush DividerBrush =>
        _dividerBrush ??= (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
    private Brush SecondaryTextBrush =>
        _secondaryTextBrush ??= (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public TrayMenuWindow() : this(ownerMenu: null)
    {
    }

    private TrayMenuWindow(TrayMenuWindow? ownerMenu)
    {
        _ownerMenu = ownerMenu;

        InitializeComponent();

        // Configure as popup-style window
        this.IsMaximizable = false;
        this.IsMinimizable = false;
        this.IsResizable = false;
        this.IsAlwaysOnTop = true;
        
        // Apply acrylic backdrop for system-consistent transparency
        BackdropHelper.TrySetAcrylicBackdrop(this);
        
        // NOTE: Do NOT set IsTitleBarVisible = false!
        // Bug 57667927: causes fail-fast in WndProc during dictionary enumeration.
        // We remove the caption via Win32 SetWindowLong instead.
        
        // Hide when focus lost
        Activated += OnActivated;

        // Keyboard navigation across menu items. We intentionally do NOT
        // attach per-Button KeyboardAccelerator instances — those crash
        // inside this WindowEx popup because their scope falls outside
        // the XamlRoot (see commit 08bce3a on the abandoned settings
        // branch). A single KeyDown handler on MenuPanel sidesteps the
        // accelerator scope entirely.
        MenuPanel.KeyDown += OnMenuPanelKeyDown;
        MenuPanel.IsTabStop = false;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_UI_AUTOMATION") == "1")
            return;

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Always use delayed check — immediate dismiss races with flyout transitions
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(150);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_isShown) return; // already hidden

                var foreground = GetForegroundWindow();
                var thisHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var flyoutHwnd = _activeFlyoutWindow != null && _activeFlyoutWindow._isShown
                    ? WinRT.Interop.WindowNative.GetWindowHandle(_activeFlyoutWindow)
                    : IntPtr.Zero;
                var ownerHwnd = _ownerMenu != null
                    ? WinRT.Interop.WindowNative.GetWindowHandle(_ownerMenu)
                    : IntPtr.Zero;

                // Stay open if focus is on this window, its flyout, or its parent
                if (foreground == thisHwnd || foreground == flyoutHwnd ||
                    (ownerHwnd != IntPtr.Zero && foreground == ownerHwnd))
                    return;

                // Focus went elsewhere — dismiss everything
                HideCascade();
                _ownerMenu?.HideCascade();
            };
            timer.Start();
        }
    }

    public void ShowAtCursor()
    {
        ApplyPopupStyle();

        if (GetCursorPos(out POINT pt))
        {
            // Get work area of monitor where cursor is
            var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMonitor, ref monitorInfo);
            var workArea = monitorInfo.rcWork;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetEffectiveMonitorDpi(hMonitor, hwnd);
            SizeToContent(workArea.Bottom - workArea.Top, dpi);
            var menuWidthPx = ConvertViewUnitsToPixels(MenuWidthViewUnits, dpi);
            var menuHeightPx = ConvertViewUnitsToPixels(_menuHeight, dpi);

            const int margin = 8;

            var (x, y) = OpenClaw.Shared.MenuPositioner.CalculatePosition(
                pt.X, pt.Y,
                menuWidthPx, menuHeightPx,
                workArea.Left, workArea.Top, workArea.Right, workArea.Bottom,
                margin);

            var targetRect = new global::Windows.Graphics.RectInt32(x, y, menuWidthPx, menuHeightPx);
            if (!RectEquals(_lastMoveAndResizeRect, targetRect))
            {
                AppWindow.MoveAndResize(targetRect);
                _lastMoveAndResizeRect = targetRect;
            }
        }
        else
        {
            SizeToContent();
        }

        ApplyRoundedWindowRegion();
        _isShown = true;
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _ = VisualTestCapture.CaptureAsync(RootGrid, "TrayMenu");
    }

    private void ShowAdjacentTo(FrameworkElement parentElement)
    {
        ApplyPopupStyle();

        if (!TryGetElementScreenRect(parentElement, out var parentRect))
        {
            ShowAtCursor();
            return;
        }

        var center = new POINT
        {
            X = parentRect.Left + ((parentRect.Right - parentRect.Left) / 2),
            Y = parentRect.Top + ((parentRect.Bottom - parentRect.Top) / 2)
        };
        var hMonitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetEffectiveMonitorDpi(hMonitor, hwnd);
        SizeToContent(monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top, dpi, SubmenuWidthViewUnits);
        var submenuWidthPx = ConvertViewUnitsToPixels(SubmenuWidthViewUnits, dpi);
        var submenuHeightPx = ConvertViewUnitsToPixels(_menuHeight, dpi);

        const int overlap = 2;
        const int margin = 8;
        var maxSubmenuHeightPx = (workArea.Bottom - workArea.Top) - (margin * 2);
        if (maxSubmenuHeightPx < 100)
            maxSubmenuHeightPx = 100;
        if (submenuHeightPx > maxSubmenuHeightPx)
        {
            submenuHeightPx = maxSubmenuHeightPx;
            _menuHeight = MenuSizingHelper.ConvertPixelsToViewUnits(submenuHeightPx, dpi);
            this.SetWindowSize(SubmenuWidthViewUnits, _menuHeight);
        }

        var roomRight = workArea.Right - parentRect.Right;
        var roomLeft = parentRect.Left - workArea.Left;
        var openRight = roomRight >= submenuWidthPx + margin || roomRight >= roomLeft;
        var x = openRight
            ? parentRect.Right - overlap
            : parentRect.Left - submenuWidthPx + overlap;
        var y = parentRect.Top;

        x = Math.Clamp(x, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - submenuWidthPx - margin));
        y = Math.Clamp(y, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - submenuHeightPx - margin));

        var targetRect = new global::Windows.Graphics.RectInt32(x, y, submenuWidthPx, submenuHeightPx);
        if (!RectEquals(_lastMoveAndResizeRect, targetRect))
        {
            AppWindow.MoveAndResize(targetRect);
            _lastMoveAndResizeRect = targetRect;
        }

        ApplyRoundedWindowRegion();
        if (!_isShown)
        {
            AppWindow.Show();
            _isShown = true;
        }
    }

    public void AddMenuItem(string text, string? icon, string action, bool isEnabled = true, bool indent = false)
    {
        var iconElement = ResolveIcon(icon);
        AddMenuItem(text, iconElement, action, isEnabled, indent);
    }

    /// <summary>
    /// Overload that takes a prebuilt <see cref="IconElement"/> (preferred for
    /// PUA Segoe Fluent glyphs). Lays the row out as a 3-column grid:
    /// [24-px icon] [stretching label] [auto chevron/accelerator hint].
    /// </summary>
    public void AddMenuItem(string text, IconElement? icon, string action, bool isEnabled = true, bool indent = false, IconElement? trailing = null)
    {
        var row = BuildItemRow(text, icon, trailing, indent, out var label);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 8, 0, 8),
            Background = s_transparentBrush,
            BorderThickness = new Thickness(0),
            IsEnabled = isEnabled,
            Tag = action,
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetAutomationId(button, BuildMenuItemAutomationId(action, text));
        AutomationProperties.SetName(button, text);

        if (!isEnabled)
            label.Opacity = 0.5;

        button.Click += (s, e) =>
        {
            MenuItemClicked?.Invoke(this, action);
            HideCascade();
        };

        button.PointerEntered += (s, e) =>
        {
            HideActiveFlyout();
            if (button.IsEnabled)
                button.Background = SubtleHoverBrush;
        };
        button.PointerExited += (s, e) =>
        {
            button.Background = s_transparentBrush;
        };

        MenuPanel.Children.Add(button);
        _itemCount++;
    }

    private static string BuildMenuItemAutomationId(string action, string text)
    {
        var source = string.IsNullOrWhiteSpace(action) ? text : action;
        var chars = source
            .Where(char.IsLetterOrDigit)
            .Take(48)
            .ToArray();
        return chars.Length == 0
            ? "TrayMenuItem"
            : "TrayMenuItem" + new string(chars);
    }

    public void AddFlyoutMenuItem(string text, string? icon, IEnumerable<TrayMenuFlyoutItem> items, bool indent = false, string? action = null)
    {
        AddFlyoutMenuItem(text, ResolveIcon(icon), items, indent, action);
    }

    public void AddFlyoutMenuItem(string text, IconElement? icon, IEnumerable<TrayMenuFlyoutItem> items, bool indent = false, string? action = null)
    {
        var flyoutItems = items.ToArray();

        var chevron = FluentIconCatalog.Build(FluentIconCatalog.ChevronR, 12);
        chevron.Opacity = 0.6;
        AutomationProperties.SetAccessibilityView(chevron, AccessibilityView.Raw);

        var row = BuildItemRow(text, icon, chevron, indent, out _);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 8, 0, 8),
            Background = s_transparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(button, text + " submenu");
        AutomationProperties.SetAutomationId(button, BuildMenuItemAutomationId(action ?? text, text));

        var flyoutTag = "flyout:" + (action ?? text);
        button.Tag = flyoutTag;
        _flyoutsByTag[flyoutTag] = (button, flyoutItems);

        button.PointerEntered += (s, e) =>
        {
            button.Background = SubtleHoverBrush;
            ShowCascadingFlyout(button, flyoutItems);
        };
        button.PointerExited += (s, e) =>
        {
            button.Background = s_transparentBrush;
        };
        button.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(action))
            {
                HideActiveFlyout();
                MenuItemClicked?.Invoke(this, action);
            }
            else
            {
                ShowCascadingFlyout(button, flyoutItems);
            }
        };

        MenuPanel.Children.Add(button);
        _itemCount++;
    }

    public void AddSeparator()
    {
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Background = DividerBrush
        };
        AutomationProperties.SetAccessibilityView(sep, AccessibilityView.Raw);
        sep.PointerEntered += (s, e) => HideActiveFlyout();
        MenuPanel.Children.Add(sep);
        _separatorCount++;
    }

    public void AddBrandHeader(string emoji, string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Padding = new Thickness(12, 12, 12, 8),
            Spacing = 8
        };

        panel.Children.Add(new TextBlock
        {
            Text = emoji,
            FontSize = 28
        });

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        AutomationProperties.SetName(panel, text);
        AutomationProperties.SetHeadingLevel(panel, AutomationHeadingLevel.Level1);
        MenuPanel.Children.Add(panel);
        _headerCount += 2; // Counts as larger
    }

    public void AddHeader(string text)
    {
        var isFirst = MenuPanel.Children.Count == 0;
        var topPad = isFirst ? 4 : 14;
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(12, topPad, 12, 8),
            Opacity = 0.7
        };
        AutomationProperties.SetHeadingLevel(tb, AutomationHeadingLevel.Level2);
        AutomationProperties.SetName(tb, text);
        tb.PointerEntered += (s, e) => HideActiveFlyout();
        MenuPanel.Children.Add(tb);
        _headerCount++;
    }

    public void AddCustomElement(UIElement element)
    {
        if (element is FrameworkElement fe)
            fe.PointerEntered += (s, e) => HideActiveFlyout();
        MenuPanel.Children.Add(element);
    }

    /// <summary>
    /// Adds a row with [icon] [title (+ optional description)] [WinUI ToggleSwitch].
    /// Toggling the switch fires MenuItemClicked with the supplied action id.
    /// Used by the Permissions flyout to mirror the in-app PermissionsPage.
    /// </summary>
    public void AddToggleItem(string title, string? icon, string? description, bool isOn, string action)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 10, 16, 10),
            ColumnSpacing = 14
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconElement = ResolveIcon(icon);
        if (iconElement != null)
        {
            AutomationProperties.SetAccessibilityView(iconElement, AccessibilityView.Raw);
            if (iconElement is FontIcon fi)
            {
                fi.FontSize = 18;
            }
            if (iconElement is FrameworkElement ife)
            {
                ife.HorizontalAlignment = HorizontalAlignment.Center;
                ife.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(iconElement, 0);
            grid.Children.Add(iconElement);
        }

        var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        labelStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        });
        if (!string.IsNullOrEmpty(description))
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = description!,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false,
                MaxWidth = 260
            });
        }
        Grid.SetColumn(labelStack, 1);
        grid.Children.Add(labelStack);

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        var stateLabel = new TextBlock
        {
            Text = isOn ? "On" : "Off",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsTextSelectionEnabled = false,
            MinWidth = 22,
            TextAlignment = TextAlignment.Right
        };
        AutomationProperties.SetAccessibilityView(stateLabel, AccessibilityView.Raw);
        trailing.Children.Add(stateLabel);

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (s, e) =>
        {
            stateLabel.Text = toggle.IsOn ? "On" : "Off";
            if (!string.IsNullOrEmpty(action))
                MenuItemClicked?.Invoke(this, action);
        };
        trailing.Children.Add(toggle);

        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        grid.PointerEntered += (s, e) => HideActiveFlyout();
        MenuPanel.Children.Add(grid);
        _itemCount++;
    }

    /// <summary>
    /// Adds a standard menu item with a right-aligned secondary text hint
    /// (used for keyboard-shortcut display like "Win+;").
    /// </summary>
    public void AddMenuItemWithHint(string text, IconElement? icon, string action, string hint, bool indent = false)
    {
        var hintBlock = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        AutomationProperties.SetAccessibilityView(hintBlock, AccessibilityView.Raw);
        AddMenuItemWithTrailingElement(text, icon, action, hintBlock, indent);
    }

    private void AddMenuItemWithTrailingElement(string text, IconElement? icon, string action, FrameworkElement trailing, bool indent)
    {
        var row = BuildItemRowWithTrailing(text, icon, trailing, indent, out var label);
        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 8, 0, 8),
            Background = s_transparentBrush,
            BorderThickness = new Thickness(0),
            Tag = action,
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetAutomationId(button, BuildMenuItemAutomationId(action, text));
        AutomationProperties.SetName(button, text);
        button.Click += (s, e) =>
        {
            MenuItemClicked?.Invoke(this, action);
            HideCascade();
        };
        button.PointerEntered += (s, e) =>
        {
            HideActiveFlyout();
            button.Background = SubtleHoverBrush;
        };
        button.PointerExited += (s, e) =>
        {
            button.Background = s_transparentBrush;
        };
        MenuPanel.Children.Add(button);
        _itemCount++;
    }

    private static Grid BuildItemRowWithTrailing(string text, IconElement? icon, FrameworkElement? trailing, bool indent, out TextBlock label)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        var leftPad = indent ? 28 : 12;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftPad + 24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconSlot = new Border
        {
            Width = 24,
            Margin = new Thickness(leftPad, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (icon != null)
        {
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            if (icon is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            iconSlot.Child = icon;
        }
        Grid.SetColumn(iconSlot, 0);
        grid.Children.Add(iconSlot);
        label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (trailing != null)
        {
            trailing.Margin = new Thickness(0, 0, 12, 0);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    /// <summary>
    /// Adds a custom UIElement as a flyout-enabled menu item with hover/click behavior.
    /// Same behavior as AddFlyoutMenuItem but accepts any UIElement instead of text.
    /// </summary>
    public void AddFlyoutCustomItem(UIElement content, IEnumerable<TrayMenuFlyoutItem> items, string? action = null)
    {
        var flyoutItems = items.ToArray();

        // Wrap content in a 3-col grid so chevron is right-aligned and the
        // tappable area extends across the full row.
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn((FrameworkElement)content, 0);
        grid.Children.Add(content);

        var chevron = FluentIconCatalog.Build(FluentIconCatalog.ChevronR, 12);
        chevron.Opacity = 0.6;
        chevron.Margin = new Thickness(0, 0, 12, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(chevron, AccessibilityView.Raw);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Background = s_transparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };

        var flyoutTag = "flyout:" + (action ?? content.GetType().Name);
        button.Tag = flyoutTag;
        _flyoutsByTag[flyoutTag] = (button, flyoutItems);

        button.PointerEntered += (s, e) =>
        {
            button.Background = SubtleHoverBrush;
            ShowCascadingFlyout(button, flyoutItems);
        };
        button.PointerExited += (s, e) =>
        {
            button.Background = s_transparentBrush;
        };
        button.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(action))
            {
                HideActiveFlyout();
                MenuItemClicked?.Invoke(this, action);
                HideCascade();
            }
            else
            {
                ShowCascadingFlyout(button, flyoutItems);
            }
        };

        MenuPanel.Children.Add(button);
        _itemCount++;
    }

    /// <summary>
    /// Builds the standard 3-column row layout used by AddMenuItem and
    /// AddFlyoutMenuItem: [24-px icon] [label] [trailing].
    /// </summary>
    private static Grid BuildItemRow(string text, IconElement? icon, IconElement? trailing, bool indent, out TextBlock label)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var leftPad = indent ? 28 : 12;
        // Column 0 holds the left padding + a 24-px icon slot. The previous
        // version had column 0 = 24 px while the icon Border also took a
        // leftPad margin → glyphs were clipped at the column boundary.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftPad + 24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconSlot = new Border
        {
            Width = 24,
            Margin = new Thickness(leftPad, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (icon != null)
        {
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            if (icon is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            iconSlot.Child = icon;
        }
        Grid.SetColumn(iconSlot, 0);
        grid.Children.Add(iconSlot);

        label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (trailing != null)
        {
            if (trailing is FrameworkElement tfe)
            {
                tfe.Margin = new Thickness(0, 0, 12, 0);
                tfe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        return grid;
    }

    /// <summary>
    /// Resolves a legacy string icon argument: a single PUA character maps to
    /// a FontIcon via <see cref="FluentIconCatalog"/>; anything else (emoji,
    /// multi-char) renders as a plain TextBlock so existing call sites keep
    /// working while the rest of the UI migrates.
    /// </summary>
    private static IconElement? ResolveIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        if (FluentIconCatalog.IsPuaGlyph(icon))
            return FluentIconCatalog.Build(icon!);
        // Wrap emoji in a PathIcon-shaped surrogate via FontIcon so the row
        // layout is consistent. FontIcon happily renders non-PUA characters
        // using the default font family fallback.
        return new FontIcon
        {
            Glyph = icon!,
            FontSize = 14
        };
    }

    /// <summary>
    /// Suppresses layout passes while a batch of Add* calls runs. Pair every
    /// call with <see cref="EndUpdate"/>. Nested begin/end pairs are honored.
    /// </summary>
    public void BeginUpdate()
    {
        _updateDepth++;
    }

    public void EndUpdate()
    {
        if (_updateDepth > 0)
            _updateDepth--;
        if (_updateDepth == 0)
        {
            MenuPanel.InvalidateMeasure();
        }
    }

    /// <summary>
    /// Adds a custom UIElement as a flyout-enabled menu item whose flyout content is a raw UIElement.
    /// </summary>
    public void ClearItems()
    {
        HideActiveFlyout();
        MenuPanel.Children.Clear();
        _flyoutsByTag.Clear();
        _itemCount = 0;
        _separatorCount = 0;
        _headerCount = 0;
    }

    public void HideCascade()
    {
        HideActiveFlyout();
        this.Hide();
        _isShown = false;
    }

    public void SizeToContent() => SizeToContent(MenuWidthViewUnits);

    /// <summary>
    /// Adjusts the window height to fit content and stores it for positioning.
    /// </summary>
    public void SizeToContent(int widthViewUnits)
    {
        if (TryGetCurrentMonitorMetrics(out var workAreaHeightPx, out var dpi))
        {
            SizeToContent(workAreaHeightPx, dpi, widthViewUnits);
            return;
        }

        SizeToContent(0, 96, widthViewUnits);
    }

    private void SizeToContent(int workAreaHeightPx, uint dpi)
        => SizeToContent(workAreaHeightPx, dpi, MenuWidthViewUnits);

    private void SizeToContent(int workAreaHeightPx, uint dpi, int widthViewUnits)
    {
        PrepareLayoutForMeasurement(dpi);

        // Measure the actual content size instead of estimating
        MenuPanel.Measure(new global::Windows.Foundation.Size(widthViewUnits, double.PositiveInfinity));
        var desiredHeight = MenuPanel.DesiredSize.Height;
        
        // Add border chrome (1px border top+bottom = 2px, plus small rounding buffer)
        var contentHeight = (int)Math.Ceiling(desiredHeight) + 2;
        _menuHeight = Math.Max(contentHeight, 100);

        if (workAreaHeightPx > 0)
        {
            var workAreaHeight = MenuSizingHelper.ConvertPixelsToViewUnits(workAreaHeightPx, dpi);
            _menuHeight = MenuSizingHelper.CalculateWindowHeight(_menuHeight, workAreaHeight);
        }

        this.SetWindowSize(widthViewUnits, _menuHeight);
        ApplyRoundedWindowRegion();
    }

    private void PrepareLayoutForMeasurement(uint dpi)
    {
        dpi = dpi == 0 ? 96 : dpi;
        var rasterizationScale = RootGrid.XamlRoot?.RasterizationScale ?? dpi / 96.0;
        var dpiChanged = _lastMeasureDpi != 0
            && MenuSizingHelper.HasDpiOrScaleChanged(_lastMeasureDpi, _lastMeasureRasterizationScale, dpi, rasterizationScale);

        _lastMeasureDpi = dpi;
        _lastMeasureRasterizationScale = rasterizationScale;

        if (dpiChanged)
        {
            _lastMoveAndResizeRect = null;
            HideActiveFlyout();
        }

        RootGrid.InvalidateMeasure();
        RootGrid.InvalidateArrange();
        MenuPanel.InvalidateMeasure();
        MenuPanel.InvalidateArrange();
        RootGrid.UpdateLayout();
    }

    private bool TryGetCurrentMonitorMetrics(out int workAreaHeight, out uint dpi)
    {
        workAreaHeight = 0;
        dpi = 96;

        if (!GetCursorPos(out POINT pt))
            return false;

        var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            return false;

        workAreaHeight = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        dpi = GetEffectiveMonitorDpi(hMonitor, hwnd);
        return workAreaHeight > 0;
    }

    private static uint GetEffectiveMonitorDpi(IntPtr hMonitor, IntPtr hwnd)
    {
        if (hMonitor != IntPtr.Zero)
        {
            try
            {
                var hr = GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
                if (hr == 0)
                {
                    if (dpiY != 0)
                        return dpiY;

                    if (dpiX != 0)
                        return dpiX;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        var dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
        return dpi == 0 ? 96u : dpi;
    }

    private void ApplyPopupStyle()
    {
        if (_styleApplied)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        SetWindowLong(hwnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        if (_ownerMenu != null)
            exStyle |= WS_EX_NOACTIVATE;

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Must call SetWindowPos with SWP_FRAMECHANGED to apply the style change.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        _styleApplied = true;
        ApplyRoundedWindowRegion();
    }

    private void ApplyRoundedWindowRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        if (!GetWindowRect(hwnd, out var rect))
            return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return;

        var dpi = GetDpiForWindow(hwnd);
        var cornerDiameter = ConvertViewUnitsToPixels(16, dpi);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, cornerDiameter, cornerDiameter);
        if (region == IntPtr.Zero)
            return;

        if (SetWindowRgn(hwnd, region, false) == 0)
        {
            DeleteObject(region);
        }
    }

    private void ShowCascadingFlyout(Button ownerButton, IReadOnlyList<TrayMenuFlyoutItem> items)
    {
        var flyoutKey = CreateFlyoutKey(items);
        var flyoutWindow = _activeFlyoutWindow;
        if (flyoutWindow == null)
        {
            flyoutWindow = new TrayMenuWindow(this);
            flyoutWindow.MenuItemClicked += (_, action) =>
            {
                MenuItemClicked?.Invoke(this, action);
                // Toggle actions inside a sub-flyout should not close the
                // cascade — the user often wants to flip several toggles
                // before dismissing the panel.
                if (!action.StartsWith("perm-toggle|", StringComparison.Ordinal))
                {
                    HideCascade();
                }
            };

            _activeFlyoutWindow = flyoutWindow;
        }

        if (!ReferenceEquals(_activeFlyoutOwner, ownerButton) || !string.Equals(_activeFlyoutKey, flyoutKey, StringComparison.Ordinal))
        {
            // Hide the submenu while repopulating so the user never sees
            // an empty or stale panel. ShowAdjacentTo will re-show it once
            // the new content has been measured and sized.
            if (flyoutWindow._isShown)
            {
                flyoutWindow.Hide();
                flyoutWindow._isShown = false;
            }

            flyoutWindow.ClearItems();
            foreach (var item in items)
            {
                if (item.IsHeader)
                {
                    flyoutWindow.AddHeader(item.Text);
                }
                else if (item.CustomContent != null)
                {
                    flyoutWindow.AddCustomElement(item.CustomContent);
                }
                else if (item.IsToggle)
                {
                    flyoutWindow.AddToggleItem(item.Text, item.Icon, item.Description, item.IsOn, item.Action);
                }
                else if (string.IsNullOrEmpty(item.Action))
                {
                    // Non-interactive detail line — compact padding
                    flyoutWindow.AddCustomElement(new TextBlock
                    {
                        Text = item.Text,
                        Padding = new Thickness(12, 2, 12, 2),
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                else
                {
                    flyoutWindow.AddMenuItem(item.Text, item.Icon, item.Action);
                }
            }

            _activeFlyoutOwner = ownerButton;
            _activeFlyoutKey = flyoutKey;
        }
        _activeFlyoutTag = ownerButton.Tag as string;

        flyoutWindow.SizeToContent(SubmenuWidthViewUnits);

        flyoutWindow.ShowAdjacentTo(ownerButton);
    }

    private void HideActiveFlyout()
    {
        _activeFlyoutWindow?.HideCascade();
        // Keep _activeFlyoutWindow alive for reuse — creating a new WinUI
        // window on every hover is expensive and causes content measurement
        // failures before XamlRoot is initialized.
        _activeFlyoutOwner = null;
        _activeFlyoutKey = null;
        _activeFlyoutTag = null;
    }

    private static string CreateFlyoutKey(IEnumerable<TrayMenuFlyoutItem> items)
    {
        return string.Join('\u001f', items.Select(item => $"{item.Text}\u001e{item.Icon}\u001e{item.Action}"));
    }

    private static bool RectEquals(global::Windows.Graphics.RectInt32? current, global::Windows.Graphics.RectInt32 next)
    {
        return current.HasValue &&
            current.Value.X == next.X &&
            current.Value.Y == next.Y &&
            current.Value.Width == next.Width &&
            current.Value.Height == next.Height;
    }

    private bool TryGetElementScreenRect(FrameworkElement element, out RECT rect)
    {
        rect = default;

        try
        {
            var transform = element.TransformToVisual(null);
            var bounds = transform.TransformBounds(new global::Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var scale = element.XamlRoot?.RasterizationScale ?? 1.0;
            var sourceWindow = _ownerMenu ?? this;
            var windowPosition = sourceWindow.AppWindow.Position;

            rect = new RECT
            {
                Left = windowPosition.X + (int)Math.Round(bounds.Left * scale),
                Top = windowPosition.Y + (int)Math.Round(bounds.Top * scale),
                Right = windowPosition.X + (int)Math.Round(bounds.Right * scale),
                Bottom = windowPosition.Y + (int)Math.Round(bounds.Bottom * scale)
            };

            return rect.Right > rect.Left && rect.Bottom > rect.Top;
        }
        catch
        {
            return false;
        }
    }

    private static int ConvertViewUnitsToPixels(int viewUnits, uint dpi)
    {
        if (viewUnits <= 0)
            return 1;

        if (dpi == 0)
            dpi = 96;

        return Math.Max(1, (int)Math.Ceiling(viewUnits * dpi / 96.0));
    }

    private void OnMenuPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var buttons = MenuPanel.Children
            .OfType<Button>()
            .Where(b => b.IsEnabled && b.Visibility == Visibility.Visible)
            .ToList();
        if (buttons.Count == 0)
            return;

        var focused = FocusManager.GetFocusedElement(MenuPanel.XamlRoot) as Button;
        var idx = focused == null ? -1 : buttons.IndexOf(focused);

        switch (e.Key)
        {
            case VirtualKey.Down:
                buttons[(idx + 1 + buttons.Count) % buttons.Count].Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                buttons[(idx - 1 + buttons.Count) % buttons.Count].Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                if (focused != null && ReferenceEquals(focused, _activeFlyoutOwner) && _activeFlyoutWindow != null)
                {
                    // Already open — push focus into the child flyout.
                    var firstChild = _activeFlyoutWindow.MenuPanel.Children
                        .OfType<Button>().FirstOrDefault();
                    firstChild?.Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Left:
            case VirtualKey.Escape:
                if (_activeFlyoutWindow != null && _activeFlyoutWindow._isShown)
                {
                    HideActiveFlyout();
                }
                else
                {
                    HideCascade();
                    _ownerMenu?.HideCascade();
                }
                e.Handled = true;
                break;
        }
    }

    private void HideCascadeIfFocusLeavesMenu()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var foreground = GetForegroundWindow();
            var thisHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var flyoutHwnd = _activeFlyoutWindow == null || !_activeFlyoutWindow._isShown
                ? IntPtr.Zero
                : WinRT.Interop.WindowNative.GetWindowHandle(_activeFlyoutWindow);

            if (foreground != thisHwnd && foreground != flyoutHwnd)
            {
                HideCascade();
            }
        };
        timer.Start();
    }
}

public sealed class TrayMenuFlyoutItem
{
    public TrayMenuFlyoutItem() { }

    public TrayMenuFlyoutItem(string text, string? icon = null, string? action = null)
    {
        Text = text;
        Icon = icon;
        Action = action ?? "";
    }

    public string Text { get; set; } = "";
    public string? Icon { get; set; }
    public string Action { get; set; } = "";
    public bool IsHeader { get; set; }

    // Renders the row with a WinUI ToggleSwitch on the right. When toggled
    // the flyout fires MenuItemClicked with Action.
    public bool IsToggle { get; set; }
    public bool IsOn { get; set; }

    // Optional secondary description text shown below the title (for ToggleSwitch rows).
    public string? Description { get; set; }

    // Free-form content: when set, the renderer drops the row in via
    // AddCustomElement and ignores Text/Icon/Action.
    public UIElement? CustomContent { get; set; }
}
