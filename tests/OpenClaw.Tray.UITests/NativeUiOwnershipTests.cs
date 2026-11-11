// Exercises native ownership with two windows on the existing WinUI dispatcher, not another app boot.
// Run with the NativeUiOwnershipTests filter on an isolated Windows desktop.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

/// <summary>Proves that native ancestry admits owned XAML and rejects foreign or unverifiable elements.</summary>
[Collection(UICollection.Name)]
public sealed class NativeUiOwnershipTests(UIThreadFixture ui)
{
    [Fact]
    public async Task OwnedXaml_RejectsAnotherWindowAndWrongOrUnverifiableOwners()
    {
        Window? first = null;
        Window? second = null;
        try
        {
            var handles = await ui.RunOnUIAsync(() =>
            {
                first = CreateWindow();
                second = CreateWindow();
                return Task.FromResult((
                    First: WinRT.Interop.WindowNative.GetWindowHandle(first),
                    Second: WinRT.Interop.WindowNative.GetWindowHandle(second)));
            });
            await ui.YieldToRenderAsync();
            Assert.NotEqual(IntPtr.Zero, handles.First);
            Assert.NotEqual(IntPtr.Zero, handles.Second);
            var owner = AutomationElement.FromHandle(handles.First);
            var other = AutomationElement.FromHandle(handles.Second);
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "OwnershipProofControl");
            var ownedControl = owner.FindFirst(TreeScope.Descendants, condition);
            var otherControl = other.FindFirst(TreeScope.Descendants, condition);
            Assert.NotNull(ownedControl);
            Assert.NotNull(otherControl);
            Assert.Equal(0, ownedControl.Current.NativeWindowHandle);

            Assert.True(NativeUiOwnership.IsOwnedDescendant(owner, ownedControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, otherControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(other, ownedControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, ownedControl, Environment.ProcessId + 1));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, AutomationElement.RootElement, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(ownedControl, ownedControl, Environment.ProcessId));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                second?.Close();
                first?.Close();
            });
        }
    }

    private static Window CreateWindow()
    {
        var button = new Button { Content = "Ownership fixture" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "OwnershipProofControl");
        var window = new Window { Content = button };
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 400, 300));
        window.Activate();
        return window;
    }
}
