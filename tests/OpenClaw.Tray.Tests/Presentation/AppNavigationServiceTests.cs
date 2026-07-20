using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the navigation service's UI-thread contract: mutating
/// operations are marshaled onto the UI thread, and the back-stack read fails fast
/// when called off the UI thread. This enforces the documented affinity rather than
/// only describing it.
/// </summary>
public sealed class AppNavigationServiceTests
{
    [Fact]
    public void Navigate_OnUiThread_InvokesSynchronously()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = true };
        var tags = new List<string>();
        var svc = new AppNavigationService(dispatcher, tags.Add, () => false, () => { });

        svc.Navigate("connection");

        Assert.Equal(new[] { "connection" }, tags);
        Assert.Equal(0, dispatcher.EnqueuedCount);
    }

    [Fact]
    public void Navigate_OffUiThread_MarshalsThroughDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var tags = new List<string>();
        var svc = new AppNavigationService(dispatcher, tags.Add, () => false, () => { });

        svc.Navigate("permissions");

        // Off-thread: the frame is NOT touched inline — the work is queued to the UI thread.
        Assert.Empty(tags);
        Assert.Equal(1, dispatcher.EnqueuedCount);

        dispatcher.FlushPending();
        Assert.Equal(new[] { "permissions" }, tags);
    }

    [Fact]
    public void GoBack_OffUiThread_MarshalsThroughDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var backCount = 0;
        var svc = new AppNavigationService(dispatcher, _ => { }, () => true, () => backCount++);

        svc.GoBack();

        Assert.Equal(0, backCount);
        Assert.Equal(1, dispatcher.EnqueuedCount);

        dispatcher.FlushPending();
        Assert.Equal(1, backCount);
    }

    [Fact]
    public void CanGoBack_OffUiThread_Throws()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false };
        var svc = new AppNavigationService(dispatcher, _ => { }, () => true, () => { });

        Assert.Throws<InvalidOperationException>(() => _ = svc.CanGoBack);
    }

    [Fact]
    public void CanGoBack_OnUiThread_ReturnsUnderlyingValue()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = true };
        var svc = new AppNavigationService(dispatcher, _ => { }, () => true, () => { });

        Assert.True(svc.CanGoBack);
    }
}
