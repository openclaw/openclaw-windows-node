// Headless generated-control and dispatcher substitutes for the linked Bindings page.
// These tests exercise the page owner, not native rendering or dispatcher thread affinity.
namespace Microsoft.UI.Xaml
{
    public class Application
    {
        private static readonly AsyncLocal<Application?> CurrentApplication = new();
        public static Application? Current
        {
            get => CurrentApplication.Value;
            set => CurrentApplication.Value = value;
        }
    }

    public enum Visibility { Visible, Collapsed }
    public class RoutedEventArgs : EventArgs { }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Page
    {
        public event EventHandler<Microsoft.UI.Xaml.RoutedEventArgs>? Unloaded;
        public TestDispatcher DispatcherQueue { get; } = new();
        public void Unload() => Unloaded?.Invoke(this, new());
    }

    public sealed class TestDispatcher
    {
        private readonly Queue<Action> _callbacks = new();
        public bool TryEnqueue(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }
        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback)) callback();
        }
    }

    public enum InfoBarSeverity { Warning, Error }
    public sealed class TestControl
    {
        public Microsoft.UI.Xaml.Visibility Visibility { get; set; }
        public object? ItemsSource { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsOpen { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
        public InfoBarSeverity Severity { get; set; }
    }
}

namespace OpenClawTray
{
    public class App : Microsoft.UI.Xaml.Application
    {
        internal Services.AppState AppState { get; } = new();
        public OpenClaw.Shared.IOperatorGatewayClient? GatewayClient { get; set; }
        public OpenClaw.Connection.GatewayConnectionManager? ConnectionManager => null;
    }
}

namespace OpenClawTray.Helpers
{
    public static class LocalizationHelper
    {
        public static string GetString(string key) => key;
    }
}

namespace OpenClawTray.Pages
{
    public sealed partial class BindingsPage
    {
        internal Microsoft.UI.Xaml.Controls.TestControl LoadingState { get; } = new();
        internal Microsoft.UI.Xaml.Controls.TestControl BindingsList { get; } = new();
        internal Microsoft.UI.Xaml.Controls.TestControl SingleAgentInfoBar { get; } = new();
        internal Microsoft.UI.Xaml.Controls.TestControl ConnectionInfoBar { get; } = new();
        internal Microsoft.UI.Xaml.Controls.TestControl RefreshButton { get; } = new();
        private void InitializeComponent() { }
    }
}
