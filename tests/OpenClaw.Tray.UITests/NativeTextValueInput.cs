// Uses the public UI Automation client surface for native-only WinUI text patterns.
// Usage (on the UI dispatcher): await NativeTextValueInput.SetAsync(window, input, "probe text").
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Uia = System.Windows.Automation;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Admits a loaded, writable owned TextBox and invokes its native ValuePattern off the UI thread.</summary>
internal static class NativeTextValueInput
{
    /// <summary>Use the client pattern: TextBox's native ValuePattern is not a public WinUI IValueProvider.</summary>
    internal static async Task SetAsync(Window window, TextBox input, string value)
    {
        Assert.True(input.DispatcherQueue.HasThreadAccess);
        await WaitForRenderedConditionAsync(() => input.IsLoaded && input.IsEnabled && !input.IsReadOnly
            && input.XamlRoot is not null && input.ActualWidth > 0 && input.ActualHeight > 0,
            "loaded writable native text input");
        var windowContent = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        Assert.Same(windowContent.XamlRoot, input.XamlRoot);
        var peer = FrameworkElementAutomationPeer.FromElement(input)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(input);
        Assert.NotNull(peer);
        var id = AutomationProperties.GetAutomationId(input);
        if (string.IsNullOrEmpty(id))
        {
            id = "NativeTextValueInput-" + Guid.NewGuid().ToString("N");
            AutomationProperties.SetAutomationId(input, id);
        }
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Assert.NotEqual(IntPtr.Zero, handle);
        // UIA marshals the native pattern call back to the control's dispatcher. Never block
        // that dispatcher with a synchronous client call into its own process.
        await Task.Run(() =>
        {
            var root = Uia.AutomationElement.FromHandle(handle);
            Assert.Equal(Environment.ProcessId, root.Current.ProcessId);
            var matches = root.FindAll(Uia.TreeScope.Descendants,
                new Uia.AndCondition(
                    new Uia.PropertyCondition(Uia.AutomationElement.AutomationIdProperty, id),
                    new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.Edit)))
                .Cast<Uia.AutomationElement>().ToArray();
            var element = Assert.Single(matches);
            Assert.Equal(Environment.ProcessId, element.Current.ProcessId);
            Assert.True(element.Current.IsEnabled);
            var pattern = Assert.IsType<Uia.ValuePattern>(element.GetCurrentPattern(Uia.ValuePattern.Pattern));
            Assert.False(pattern.Current.IsReadOnly);
            pattern.SetValue(value);
        });
    }
}
