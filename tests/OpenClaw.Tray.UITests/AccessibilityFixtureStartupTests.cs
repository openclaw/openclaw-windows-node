using System.Runtime.InteropServices;
using System.Diagnostics;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class AccessibilityFixtureStartupTests
{
    [Fact]
    [Trait("Category", "AccessibilityStartup")]
    public async Task RealFixtureStartsItsOwnHubAndAcknowledgesConnectionNavigation()
    {
        using var fixture = new AccessibilityAppFixture(initializeAxe: false);
        Assert.NotEqual(IntPtr.Zero, fixture.HubWindowHandle);
        Assert.NotEqual(0u, GetWindowThreadProcessId(fixture.HubWindowHandle, out var processId));
        Assert.NotEqual((uint)Environment.ProcessId, processId);
        await fixture.NavigateAsync("connection", "ConnectionPage", "ConnectionPageMarker");
    }

    [Fact]
    [Trait("Category", "AccessibilityStartup")]
    public void StartupDiscoveryFailureReapsTheActualOwnedChildAndTemporaryProfile()
    {
        Process? ownedChild = null;
        string? profile = null;
        var failure = new InvalidOperationException("Synthetic window discovery failure.");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                new AccessibilityAppFixture(initializeAxe: false, hubWindowLocator: child =>
                {
                    ownedChild = Process.GetProcessById(child.Id);
                    _ = ownedChild.Handle;
                    profile = child.StartInfo.Environment["OPENCLAW_TRAY_DATA_DIR"];
                    throw failure;
                }));
            Assert.Same(failure, error);
            Assert.NotNull(ownedChild);
            Assert.True(ownedChild.WaitForExit(0));
            Assert.NotNull(profile);
            Assert.False(Directory.Exists(profile));
        }
        finally { ownedChild?.Dispose(); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
