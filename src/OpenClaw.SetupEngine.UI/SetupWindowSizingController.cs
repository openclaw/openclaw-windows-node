using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

/// <summary>Tracks HWND DPI and native minimums without resetting the user's size after initialization.</summary>
internal sealed class SetupWindowSizingController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _content;
    private XamlRoot? _root;
    private bool _disposed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindowSizingController(Window window, FrameworkElement content)
    {
        (_window, _content) = (window, content);
        _content.Loaded += ContentLoaded;
        _window.AppWindow.Changed += WindowChanged;
        _window.Closed += WindowClosed;
        ApplyMinimumSize();
        var initial = SetupWindowSizing.InitialPixels(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)));
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(initial.Width, initial.Height));
        if (_content.IsLoaded) AttachRoot();
    }

    private void ContentLoaded(object sender, RoutedEventArgs args) => AttachRoot();

    private void AttachRoot()
    {
        if (_disposed) return;
        var root = _content.XamlRoot;
        if (!ReferenceEquals(root, _root))
        {
            if (_root is { } previous) previous.Changed -= RootChanged;
            _root = root;
            if (root is not null) root.Changed += RootChanged;
        }
        ApplyMinimumSize();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyMinimumSize();

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidPresenterChange) ApplyMinimumSize();
    }

    internal void ApplyMinimumSize()
    {
        if (_disposed || _window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var minimum = SetupWindowSizing.MinimumPixels(dpi);
        if (presenter.PreferredMinimumWidth != minimum.Width)
            presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height)
            presenter.PreferredMinimumHeight = minimum.Height;
    }

    private void WindowClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _content.Loaded -= ContentLoaded;
        _window.AppWindow.Changed -= WindowChanged;
        _window.Closed -= WindowClosed;
        if (_root is { } root) root.Changed -= RootChanged;
        _root = null;
    }
}
