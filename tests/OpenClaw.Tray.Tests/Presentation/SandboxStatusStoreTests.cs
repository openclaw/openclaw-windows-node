using OpenClaw.Shared.Mxc;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class SandboxStatusStoreTests
{
    [Fact]
    public void ProbeResults_DistinguishSupportedUnsupportedAndFailedWithoutChangingPolicy()
    {
        var store = new SandboxStatusStore();
        var notifications = new List<SandboxStatusSnapshot>();
        store.Changed += (_, snapshot) => notifications.Add(snapshot);
        Assert.Equal(SandboxStatus.Checking, store.Current.Status);
        store.Publish(new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true));
        Assert.Equal(SandboxStatus.Available, store.Current.Status);
        store.Publish(new(false, false, true, [], isWindowsUnsupported: true));
        Assert.Equal(SandboxStatus.Compatibility, store.Current.Status);
        store.Publish(new(false, false, false, [], probeErrored: true, isWindowsUnsupported: true));
        Assert.Equal(SandboxStatus.Unavailable, store.Current.Status);
        store.Publish(new(false, false, false, []));
        Assert.Equal(3, notifications.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, notifications.Select(snapshot => snapshot.Version));
    }
}
