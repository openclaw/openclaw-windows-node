using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Compact chromeless countdown overlay (3-2-1) shown before recording starts.
/// Displays as a small floating dark pill with a white countdown number.
/// </summary>
public sealed class RecordingCountdownWindow : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private readonly TaskCompletionSource _tcs = new();
    private readonly TextBlock _countdownText;
    private readonly DispatcherQueueTimer _timer;
    private int _remaining;

    public RecordingCountdownWindow(int seconds = 3)
    {
        _remaining = seconds;

        Title = "";
        this.SetWindowSize(120, 120);
        this.CenterOnScreen();
        ExtendsContentIntoTitleBar = true;
        IsMinimizable = false;
        IsMaximizable = false;
        IsResizable = false;

        _countdownText = new TextBlock
        {
            Text = _remaining.ToString(),
            FontSize = 56,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White),
            // Nudge up slightly to compensate for font descender space
            Padding = new Thickness(0, 0, 0, 6)
        };

        // Solid dark circle on a fully transparent window
        var pill = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, 30, 30, 30)),
            CornerRadius = new CornerRadius(60),
            Width = 100,
            Height = 100,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _countdownText
        };

        Content = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { pill }
        };

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        _remaining--;

        if (_remaining <= 0)
        {
            _timer.Stop();
            Close();
            return;
        }

        _countdownText.Text = _remaining.ToString();
    }

    public Task ShowCountdownAsync()
    {
        Closed += (s, e) => _tcs.TrySetResult();

        // Transparent window background so only the dark circle is visible
        SystemBackdrop = new TransparentTintBackdrop();

        Activate();

        // Strip window chrome and make topmost
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                SetWindowLong(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
            }
        }
        catch { /* best-effort */ }

        _timer.Start();

        return _tcs.Task;
    }
}
