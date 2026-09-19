using System.IO.Pipes;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class BrowserBootstrapPipeClientTests
{
    [Fact]public async Task ResponseFrameIsNotCompletionUntilPeerCloses()
    {
        var name="OpenClaw.BrowserBootstrap.Test."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var connecting=server.WaitForConnectionAsync(stop.Token);await client.ConnectAsync(stop.Token);await connecting;
        var run=BrowserBootstrapPipeClient.ExchangeAsync(client,"{}"u8.ToArray(),stop.Token);
        _=await BrowserNativeProtocol.ReadAsync(server,4096,stop.Token);
        var response=BrowserNativeProtocol.Failure("pairing_unavailable");
        await BrowserNativeProtocol.WriteAsync(server,response,stop.Token);
        Assert.False(run.IsCompleted);server.Dispose();Assert.Equal(response,await run);
    }
    [Fact]public async Task CancellationWaitsForActualHandlerCleanupAndDiscardsCredentials()
    {
        var name="OpenClaw.BrowserBootstrap.Test."+Guid.NewGuid().ToString("N");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server=new BrowserBootstrapPipeServer(async (_,ct)=>
        {
            entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}
            finally{cleaning.SetResult();await finish.Task;}
            return BrowserNativeProtocol.Pairing("AAAAAAAAAAAAAAAAAAAAAA","never-delivered");
        },name);
        server.Start();using var stop=new CancellationTokenSource();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeout.Token);
            var run=BrowserBootstrapPipeClient.ExchangeAsync(client,"{}"u8.ToArray(),stop.Token);
            await entered.Task.WaitAsync(timeout.Token);stop.Cancel();await cleaning.Task.WaitAsync(timeout.Token);
            // A real handler is still in cleanup. Expiring a client-only two-second
            // timer must not make its caller eligible to retire the generation.
            await Task.Delay(TimeSpan.FromMilliseconds(2200),timeout.Token);
            Assert.False(run.IsCompleted);finish.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
        }
        finally{finish.TrySetResult();}
    }
    [Fact]public async Task PartialSendIsUnknownFailureEvenAfterPeerClosure()
    {
        using var pipe=new FaultStream(partial:true);
        var error=await Assert.ThrowsAsync<IOException>(()=>BrowserBootstrapPipeClient.ExchangeAsync(pipe,"{}"u8.ToArray(),default));
        Assert.Contains("request completion",error.Message);Assert.True(pipe.CancelByteWritten);
    }
    [Fact]public async Task UnconfirmedCleanupCannotCompleteBeforePeerSettlement()
    {
        using var pipe=new FaultStream(partial:false);
        var run=BrowserBootstrapPipeClient.ExchangeAsync(pipe,"{}"u8.ToArray(),default);
        try
        {
            await pipe.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.IsCompleted);pipe.Settled.SetResult();
            await Assert.ThrowsAsync<IOException>(()=>run);Assert.True(pipe.CancelByteWritten);
        }
        finally{pipe.Settled.TrySetResult();}
    }
    [Fact]public async Task OversizedCleanupStreamCannotEndOwnershipBeforePeerClosure()
    {
        using var pipe=new OversizedCleanupStream();
        var run=BrowserBootstrapPipeClient.ExchangeAsync(pipe,"{}"u8.ToArray(),default);
        try
        {
            await pipe.OverflowReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.IsCompleted);
            pipe.Closed.SetResult();await Assert.ThrowsAsync<IOException>(()=>run);
        }
        finally{pipe.Closed.TrySetResult();}
    }
    private sealed class OversizedCleanupStream:MemoryStream
    {
        private int reads;
        internal readonly TaskCompletionSource OverflowReached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct=default)=>ValueTask.CompletedTask;
        public override ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken ct=default)
        {
            reads++;
            if(reads==1)throw new IOException("synthetic initial read failure");
            if(reads<=259)
            {
                bytes.Span.Clear();
                if(reads==258)OverflowReached.TrySetResult();
                return ValueTask.FromResult(bytes.Length);
            }
            return new ValueTask<int>(End());
        }
        private async Task<int> End(){await Closed.Task;return 0;}
    }
    private sealed class FaultStream(bool partial):MemoryStream
    {
        private int writes,reads;
        internal bool CancelByteWritten;
        internal readonly TaskCompletionSource Cleaning=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Settled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct=default)
        {
            writes++;
            if(partial&&writes==2)throw new IOException("partial test write");
            if(bytes.Length==1&&bytes.Span[0]==0)CancelByteWritten=true;
            return ValueTask.CompletedTask;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken ct=default)
        {
            reads++;
            if(partial)return 0;
            if(reads==1)throw new IOException("test response failure");
            Cleaning.SetResult();await Settled.Task.WaitAsync(ct);return 0;
        }
    }
}
