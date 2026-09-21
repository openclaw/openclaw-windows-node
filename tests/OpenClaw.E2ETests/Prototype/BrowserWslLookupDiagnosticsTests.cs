using System.Text;

namespace OpenClaw.E2ETests.Setup;

public sealed class BrowserWslLookupDiagnosticsTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task LookupMarker_ReportsScriptEntryWithoutChangingPidOutput(string newline)
    {
        var output="LOOKUP_READY"+newline+"1234"+newline;
        using var reader=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(output)));
        bool entered=false;
        Assert.Equal(output,await BrowserWslProductionOwnerTests.ReadLookupOutput(reader,()=>entered=true));
        Assert.True(entered);
    }

    [Fact]
    public async Task AbsentMarker_DoesNotClaimScriptEntry()
    {
        using var reader=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("1234\n")));
        bool entered=false;
        await BrowserWslProductionOwnerTests.ReadLookupOutput(reader,()=>entered=true);
        Assert.False(entered);
    }

    [Fact]
    public async Task FaultedDrain_IsSettledWithoutInventingCleanupFailure()
    {
        var drain=Task.FromException(new InvalidDataException("Lookup output bound"));
        await BrowserWslProductionOwnerTests.ObserveLookupDrainAsync(drain);
        Assert.True(drain.IsFaulted);
    }

    [Fact]
    public async Task UnfinishedDrain_IsNotReportedSettled()
    {
        var drain=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settlement=BrowserWslProductionOwnerTests.ObserveLookupDrainAsync(drain.Task);
        Assert.False(settlement.IsCompleted);
        drain.SetException(new InvalidDataException("Lookup output bound"));
        await settlement;
        Assert.True(drain.Task.IsFaulted);
    }

    [Fact]
    public async Task OutputBound_RemainsFailClosed()
    {
        using var reader=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("LOOKUP_READY\n"+new string('1',4096))));
        await Assert.ThrowsAsync<InvalidDataException>(()=>BrowserWslProductionOwnerTests.ReadLookupOutput(reader,()=>{}));
    }
}
