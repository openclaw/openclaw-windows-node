using System.Buffers.Binary;
using System.Text;
using OpenClaw.BrowserBootstrap.Contracts;
using System.Text.Json;

namespace OpenClaw.BrowserBootstrap.Tests;

public class NativeRegistrationRuntimeTests
{
    private static byte[] Failure(string code)=>JsonSerializer.SerializeToUtf8Bytes(new{v=1,ok=false,code});
    private static byte[] Pairing(string nonce,string pairingString)=>JsonSerializer.SerializeToUtf8Bytes(new{v=1,ok=true,nonce,pairingString});
    internal static Generation Make(string mode=ManagementContract.Native)
    {
        var request=ManagementContractTests.Request() with{Mode=mode};
        return RegistrationServiceTests.Make(mode==ManagementContract.Companion?request with{Context=null}:request);
    }
    internal sealed class Platform(Generation generation):INativeRegistrationPlatform,IDisposable
    {
        internal readonly Mutex Mutex=new();
        internal NativeInventory Inventory=new("owned",[generation]);
        internal readonly List<int> Acquired=[],Released=[];
        internal int Observations,Leases;
        internal bool Abandoned;
        internal Action? OnAcquireAttempt;
        internal Func<Generation,IDisposable>? Admit;
        public IDisposable Acquire()
        {
            OnAcquireAttempt?.Invoke();
            try { if(!Mutex.WaitOne(TimeSpan.FromSeconds(5)))throw new ContractException("busy"); }
            catch(AbandonedMutexException){Abandoned=true;}
            lock(Acquired)Acquired.Add(Environment.CurrentManagedThreadId);
            return new Release(()=>{lock(Released)Released.Add(Environment.CurrentManagedThreadId);Mutex.ReleaseMutex();});
        }
        public NativeInventory ObserveNative(){Interlocked.Increment(ref Observations);return Inventory;}
        public IDisposable LeaseRuntime(Generation g){Interlocked.Increment(ref Leases);return Admit?.Invoke(g)??new Release(()=>{});}
        public void Dispose()=>Mutex.Dispose();
    }
    internal sealed class Release(Action release):IDisposable{public void Dispose()=>release();}
    internal sealed class Input(byte[] payload):MemoryStream(Frame(payload))
    {
        internal readonly TaskCompletionSource Disconnected=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static byte[] Frame(byte[] payload){var bytes=new byte[payload.Length+4];BinaryPrimitives.WriteInt32LittleEndian(bytes,payload.Length);payload.CopyTo(bytes,4);return bytes;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {
            if(Position<Length)return await base.ReadAsync(buffer,ct);
            await Disconnected.Task.WaitAsync(ct);return 0;
        }
    }
    private sealed class Output:MemoryStream
    {
        internal readonly TaskCompletionSource Flushing=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinishFlush=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Flushed;
        public override async Task FlushAsync(CancellationToken ct)
        {Flushing.TrySetResult();await FinishFlush.Task.WaitAsync(ct);Flushed=true;}
    }
    [Theory][InlineData("missing")][InlineData("invalid")][InlineData("foreign")][InlineData(null)]
    public async Task NonCompleteActivationNeverStartsWork(string? state)
    {
        var g=Make();using var p=new Platform(g){Inventory=new(state,[])};var effects=0;
        await Assert.ThrowsAsync<ContractException>(()=>new NativeRegistrationRuntime(p).RunAsync(g,(_,_)=>{effects++;return Task.CompletedTask;},default));
        Assert.Equal(0,effects);Assert.Equal(0,p.Leases);Assert.Equal(p.Acquired,p.Released);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ChangedGenerationOrReceiptRefusesBeforeEffects(bool receipt)
    {
        var g=Make();var changed=receipt?g with{Receipt=g.Receipt with{BindingSha256=new('1',64)}}:
            g with{Installation=g.Installation with{Generation="aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa"}};
        using var p=new Platform(g){Inventory=new("owned",[changed])};
        await Assert.ThrowsAsync<ContractException>(()=>new NativeRegistrationRuntime(p).RunAsync(g,(_,_)=>throw new Exception("effects entered"),default));
        Assert.Equal(0,p.Leases);
    }
    [Theory][InlineData(ManagementContract.Native,false)][InlineData(ManagementContract.Companion,false)]
    [InlineData(ManagementContract.Native,true)][InlineData(ManagementContract.Companion,true)]
    public async Task RetirementCannotCompleteBeforeFinalFrameFlush(string mode,bool failure)
    {
        var g=Make(mode);using var p=new Platform(g);using var input=new Input(NativeKeylessProbe.Request(false));using var output=new Output();
        var retirementWaiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            async (_,_,_,ct)=>{await Task.Yield();ct.ThrowIfCancellationRequested();if(failure)throw new IOException("synthetic backend failure");return Pairing("AAAAAAAAAAAAAAAAAAAAAA","synthetic-unit-only");});
        await output.Flushing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var retire=Task.Factory.StartNew(()=>{retirementWaiting.SetResult();using var held=p.Acquire();Assert.True(output.Flushed);p.Inventory=new("missing",[]);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            await retirementWaiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(p.Released);Assert.False(retire.IsCompleted);
            output.FinishFlush.SetResult();
            await Task.WhenAll(run,retire).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(p.Acquired,p.Released);Assert.Contains(failure?"pairing_unavailable":"synthetic-unit-only",Encoding.UTF8.GetString(output.ToArray()));
        }
        finally{output.FinishFlush.TrySetResult();}
    }
    [Theory][InlineData(ManagementContract.Native)][InlineData(ManagementContract.Companion)]
    public async Task CompletedRetirementRefusesStaleLauncherWithoutBackend(string mode)
    {
        var g=Make(mode);using var p=new Platform(g);using(var owner=p.Acquire())p.Inventory=new("missing",[]);
        using var input=new Input(NativeKeylessProbe.Request(false));using var output=new MemoryStream();var effects=0;
        await NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>{effects++;return Task.FromResult(Pairing("AAAAAAAAAAAAAAAAAAAAAA","must-not-appear"));});
        Assert.Equal(0,effects);Assert.Contains("manifest_invalid",Encoding.UTF8.GetString(output.ToArray()));Assert.Equal("missing",p.Inventory.State);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ErrorAndCancellationReleaseOnAcquiringThread(bool cancel)
    {
        var g=Make();using var p=new Platform(g);using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<Exception>(()=>new NativeRegistrationRuntime(p).RunAsync(g,async (_,ct)=>
        {await Task.Yield();if(cancel){stop.Cancel();ct.ThrowIfCancellationRequested();}throw new IOException();},stop.Token));
        Assert.Equal(p.Acquired,p.Released);Assert.Single(p.Acquired);
        Assert.True(p.Mutex.WaitOne(0));p.Mutex.ReleaseMutex();
    }
    [Fact]public async Task CancelWhileWaitingNeverObservesOrStartsEffects()
    {
        var g=Make();using var p=new Platform(g);using var stop=new CancellationTokenSource();
        using var held=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var attempt=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder=new Thread(()=>{p.Mutex.WaitOne();held.Set();release.Wait();p.Mutex.ReleaseMutex();});holder.Start();held.Wait();
        p.OnAcquireAttempt=()=>attempt.TrySetResult();
        try
        {
            var run=new NativeRegistrationRuntime(p).RunAsync(g,(_,_)=>throw new Exception("cancelled effects"),stop.Token);
            await attempt.Task.WaitAsync(TimeSpan.FromSeconds(3));stop.Cancel();release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
            Assert.Equal(0,p.Observations);Assert.Equal(0,p.Leases);Assert.Equal(p.Acquired,p.Released);
        }
        finally{release.Set();holder.Join();}
    }
    [Fact]public async Task AbandonedOwnerRequiresFreshObservation()
    {
        var g=Make();using var p=new Platform(g);var abandoned=new Thread(()=>p.Mutex.WaitOne());abandoned.Start();abandoned.Join();
        p.Inventory=new("missing",[]);
        await Assert.ThrowsAsync<ContractException>(()=>new NativeRegistrationRuntime(p).RunAsync(g,(_,_)=>throw new Exception("stale work"),default));
        Assert.True(p.Abandoned);Assert.Equal(1,p.Observations);Assert.Equal(p.Acquired,p.Released);
    }
    [Fact]public async Task CancellationKeepsOwnershipUntilBackendCleanupSettles()
    {
        var g=Make();using var p=new Platform(g);using var stop=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=new NativeRegistrationRuntime(p).RunAsync(g,async (_,ct)=>
        {entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{cleanup.SetResult();await finish.Task;}},stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));stop.Cancel();await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(p.Mutex.WaitOne(0));Assert.Empty(p.Released);finish.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);Assert.Equal(p.Acquired,p.Released);
        }
        finally{finish.TrySetResult();}
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ExactNegativeStillExecutesTransportWithoutAcquiring(bool invalid)
    {
        var g=Make();using var p=new Platform(g){Inventory=new("missing",[])};
        var caller=invalid?g.Binding.ExpectedOrigins[0]:ManagementContract.RejectionOrigin(g.Binding.ExpectedOrigins);
        using var input=new Input(NativeKeylessProbe.Request(invalid));using var output=new MemoryStream();var calls=0;
        await NativeTransport.RunAsync(input,output,[caller],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>{calls++;return Task.FromResult(Failure(invalid?"invalid_request":"origin_forbidden"));});
        Assert.Equal(1,calls);Assert.Empty(p.Acquired);Assert.Contains(invalid?"invalid_request":"origin_forbidden",Encoding.UTF8.GetString(output.ToArray()));
    }
    [Fact]public async Task BackendCleanupBudgetFailureIsNotChromeEofOrSuccess()
    {
        var g=Make();using var p=new Platform(g);using var input=new Input(NativeKeylessProbe.Request(false));using var output=new MemoryStream();
        await NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>throw new OperationCanceledException("synthetic settled cleanup failure"));
        var text=Encoding.UTF8.GetString(output.ToArray());Assert.Contains("pairing_unavailable",text);Assert.DoesNotContain("pairingString",text);
        Assert.Equal(p.Acquired,p.Released);
    }
    [Fact]public async Task UnexpectedProbeSuccessIsNotForwardedOrFabricatedAsPass()
    {
        var g=Make();using var p=new Platform(g);using var input=new Input(NativeKeylessProbe.Request(true));using var output=new MemoryStream();
        await NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>Task.FromResult(Pairing("AAAAAAAAAAAAAAAAAAAAAA","never-forward")));
        var text=Encoding.UTF8.GetString(output.ToArray());Assert.Contains("manifest_invalid",text);Assert.DoesNotContain("never-forward",text);Assert.DoesNotContain("invalid_request",text);
    }
    [Fact]public async Task PartialDeliveryIsNotRetriedOutsideTheOwner()
    {
        var g=Make();using var p=new Platform(g);using var input=new Input(NativeKeylessProbe.Request(false));using var output=new BrokenOutput();
        await Assert.ThrowsAnyAsync<IOException>(()=>NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>Task.FromResult(Pairing("AAAAAAAAAAAAAAAAAAAAAA","synthetic-unit-only"))));
        Assert.Equal(2,output.Writes);Assert.Equal(p.Acquired,p.Released);
    }
    [Fact]public async Task CancellationRacingPartialDeliveryRemainsTerminalFailure()
    {
        var g=Make();using var p=new Platform(g);using var stop=new CancellationTokenSource();
        using var input=new Input(NativeKeylessProbe.Request(false));using var output=new BrokenOutput(()=>stop.Cancel());
        await Assert.ThrowsAnyAsync<IOException>(()=>NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),stop.Token,
            (_,_,_,_)=>Task.FromResult(Pairing("AAAAAAAAAAAAAAAAAAAAAA","synthetic-unit-only"))));
        Assert.True(stop.IsCancellationRequested);Assert.Equal(2,output.Writes);Assert.Equal(p.Acquired,p.Released);
    }
    private sealed class BrokenOutput(Action? beforeFailure=null):MemoryStream
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken ct=default)
        {Writes++;if(Writes==2){beforeFailure?.Invoke();throw new IOException("synthetic partial write");}return base.WriteAsync(data,ct);}
    }
    public static IEnumerable<object[]> Variants()
    {
        const string good="""{"v":1,"op":"bootstrap","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}""";
        foreach(var raw in new[]{good,good.Replace(":1,",":1.0,"),good.Replace(":1,",":1e0,"),good.Replace("}",",\"extra\":true}"),
            good.Replace("}",",\"v\":1}"),good.Replace("}",",\"\\u0076\":1}"),good.Replace("bootstrap","unknown"),
            good.Replace("bootstrap","ensure_relay"),good.Replace("}",",\"relayPort\":\"1\"}")," "+Encoding.UTF8.GetString(NativeKeylessProbe.Request(true)),
            "\uFEFF"+good,"{",good.Replace("\"v\":1,\"op\":\"bootstrap\"","\"op\":\"bootstrap\",\"v\":1")}) yield return [raw];
    }
    [Theory][MemberData(nameof(Variants))]
    public async Task AnyOtherCompleteInputNeedsActivationEvenWhenCSharpWouldReject(string raw)
    {
        var g=Make();using var p=new Platform(g){Inventory=new("missing",[])};
        using var input=new Input(Encoding.UTF8.GetBytes(raw));using var output=new MemoryStream();var effects=0;
        await NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>g,_=>new Release(()=>{}),new(p),default,
            (_,_,_,_)=>{effects++;return Task.FromResult(Failure("invalid_request"));});
        Assert.Single(p.Acquired);Assert.Equal(0,effects);Assert.Contains("manifest_invalid",Encoding.UTF8.GetString(output.ToArray()));
    }
}
