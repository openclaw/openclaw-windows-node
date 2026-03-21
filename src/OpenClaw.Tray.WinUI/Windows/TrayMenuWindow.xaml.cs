using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Runtime.InteropServices;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// A popup window that displays the tray menu at the cursor position.
/// Uses Win32 to remove title bar (workaround for Bug 57667927).
/// </summary>
public sealed partial class TrayMenuWindow : WindowEx
{
    #region Win32 Imports
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

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

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    
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

    public TrayMenuWindow()
    {
        InitializeComponent();

        // Configure as popup-style window
        this.IsMaximizable = false;
        this.IsMinimizable = false;
        this.IsResizable = false;
        this.IsAlwaysOnTop = true;
        
        // NOTE: Do NOT set IsTitleBarVisible = false!
        // Bug 57667927: causes fail-fast in WndProc during dictionary enumeration.
        // We remove the caption via Win32 SetWindowLong instead.
        
        // Hide when focus lost
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            this.Hide();
        }
    }

    public void ShowAtCursor()
    {
        // Remove title bar via Win32 (once, on first show)
        if (!_styleApplied)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
            SetWindowLong(hwnd, GWL_STYLE, style);
            
            // Must call SetWindowPos with SWP_FRAMECHANGED to apply the style change
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            
            _styleApplied = true;
        }

        if (GetCursorPos(out POINT pt))
        {
            // Get work area of monitor where cursor is
            var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMonitor, ref monitorInfo);
            var workArea = monitorInfo.rcWork;

            // Prefer using the AppWindow's actual pixel size. This is more reliable than
            // estimating based on DPI and item counts, especially on Windows 10 when the
            // cursor can be in the taskbar region (outside rcWork).
            int menuWidthPx;
            int menuHeightPx;
            try
            {
                menuWidthPx = this.AppWindow.Size.Width;
                menuHeightPx = this.AppWindow.Size.Height;
            }
            catch
            {
                menuWidthPx = 0;
                menuHeightPx = 0;
            }

            // Fallback to a conservative estimate if AppWindow size isn't available yet.
            if (menuWidthPx <= 0 || menuHeightPx <= 0)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetEffectiveMonitorDpi(hMonitor, hwnd);
                double scale = dpi / 96.0;
                menuWidthPx = (int)(280 * scale);
                menuHeightPx = (int)(_menuHeight * scale);
            }

            const int margin = 8;

            var (x, y) = OpenClaw.Shared.MenuPositioner.CalculatePosition(
                pt.X, pt.Y,
                menuWidthPx, menuHeightPx,
                workArea.Left, workArea.Top, workArea.Right, workArea.Bottom,
                margin);

            this.Move(x, y);
        }

        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void AddMenuItem(string text, string? icon, string action, bool isEnabled = true, bool indent = false)
    {
        var content = new TextBlock
        {
            Text = string.IsNullOrEmpty(icon) ? text : $"{icon}  {text}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        };

        var leftPadding = indent ? 28 : 12;
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(leftPadding, 8, 12, 8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = isEnabled,
            Tag = action,
            CornerRadius = new CornerRadius(4)
        };

        if (!isEnabled)
            content.Opacity = 0.5;

        button.Click += (s, e) =>
        {
            MenuItemClicked?.Invoke(this, action);
            this.Hide(); // Hide instead of close - window is reused
        };

        // Hover effect
        button.PointerEntered += (s, e) =>
        {
            if (button.IsEnabled)
                button.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        };
        button.PointerExited += (s, e) =>
        {
            button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };

        MenuPanel.Children.Add(button);
        _itemCount++;
    }

    public void AddSeparator()
    {
        MenuPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
        });
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

        MenuPanel.Children.Add(panel);
        _headerCount += 2; // Counts as larger
    }

    public void AddHeader(string text)
    {
        MenuPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(12, 10, 12, 4),
            Opacity = 0.7
        });
        _headerCount++;
    }

    public void ClearItems()
    {
        MenuPanel.Children.Clear();
        _itemCount = 0;
        _separatorCount = 0;
        _headerCount = 0;
    }

    /// <summary>
    /// Adjusts the window height to fit content and stores it for positioning
    /// </summary>
    public void SizeToContent()
    {
        // Calculate height based on item counts
        // Menu items: ~36px each (button with padding)
        // Separators: ~13px each  
        // Headers: ~30px each
        // Plus padding: ~16px
        var contentHeight = (_itemCount * 36) + (_separatorCount * 13) + (_headerCount * 30) + 16;
        _menuHeight = Math.Max(contentHeight, 100); // minimum

        if (TryGetCurrentMonitorMetrics(out var workAreaHeightPx, out var dpi))
        {
            // Constrain the popup to the visible work area so the ScrollViewer gets
            // a viewport and the menu stays reachable near the tray/taskbar.
            var workAreaHeight = MenuSizingHelper.ConvertPixelsToViewUnits(workAreaHeightPx, dpi);
            _menuHeight = MenuSizingHelper.CalculateWindowHeight(contentHeight, workAreaHeight);
        }

        this.SetWindowSize(280, _menuHeight);
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
}
