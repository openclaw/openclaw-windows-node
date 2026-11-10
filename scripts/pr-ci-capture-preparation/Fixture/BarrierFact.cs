using System.Globalization;
using OpenClawObserve;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace OpenClaw.Barrier.Fixture;
public sealed class BarrierFact {
    [Fact]
    public void BodyReportsActualAckVisibility() {
        var root=Environment.GetEnvironmentVariable("OPENCLAW_BARRIER_ROOT")!;
        var nonce=Environment.GetEnvironmentVariable("OPENCLAW_BARRIER_NONCE")!;
        BarrierFiles.Root(root);BarrierFiles.Nonce(nonce);
        // No artificial wait or mode-specific behavior. This is the real test body's first observable work.
        var ack=BarrierFiles.Read(root,"ack-ready");
        if(ack is not null)Assert.Equal(nonce,ack);
        BarrierFiles.Write(root,"body",nonce+"|"+Environment.ProcessId.ToString(CultureInfo.InvariantCulture)+"|"+(ack is null?"false":"true"));
    }
}
