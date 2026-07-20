using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class CaptureOperationGateTests
{
    [Fact]
    public async Task EnterAsync_CancelledWaiter_DoesNotEnterLater()
    {
        using var gate = new CaptureOperationGate();
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var queued = gate.EnterAsync(cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        first.Dispose();

        using var next = await gate.EnterAsync(CancellationToken.None);
        Assert.True(queued.IsCanceled);
    }
}
