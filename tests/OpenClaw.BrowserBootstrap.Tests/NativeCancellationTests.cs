using System.Buffers.Binary;
using System.Text;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap.Tests;

public class NativeCancellationTests
{
    [Theory][InlineData(ManagementContract.Companion)][InlineData(ManagementContract.Native)]
    public async Task ChromeEofRevokesBothBackendsBeforeResponse(string mode)
    {
        var request=ManagementContractTests.Request() with {Mode=mode};
        if(mode==ManagementContract.Companion)request=request with {Context=null};
        var generation=RegistrationServiceTests.Make(request);
        using var input=new DisconnectableFrame();using var output=new MemoryStream();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>generation,_=>new Lease(),default,
            async (_,_,_,ct)=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){cancelled.SetResult();throw;}return [];});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));input.Disconnect();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0,output.Length);
    }
    [Fact]public async Task UnsafeRuntimeNeverStartsBackend()
    {
        var generation=RegistrationServiceTests.Make(ManagementContractTests.Request());
        using var input=new DisconnectableFrame();using var output=new MemoryStream();var starts=0;
        await NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>generation,_=>throw new ContractException("unsafe_acl"),default,
            (_,_,_,_)=>{starts++;return Task.FromResult(Array.Empty<byte>());});
        Assert.Equal(0,starts);
        Assert.Contains("manifest_invalid",Encoding.UTF8.GetString(output.ToArray()));
    }
    [Fact]public async Task RevokedBackendCannotPublishEvenIfItIgnoresCancellation()
    {
        var generation=RegistrationServiceTests.Make(ManagementContractTests.Request());
        using var input=new DisconnectableFrame();using var output=new MemoryStream();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=NativeTransport.RunAsync(input,output,[ManagementContract.Origin],()=>generation,_=>new Lease(),default,
            (_,_,_,_)=>{entered.SetResult();return release.Task;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));input.Disconnect();
        await input.Disconnected.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);release.SetResult(Encoding.UTF8.GetBytes("synthetic-credential-response"));
        await run.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(0,output.Length);
    }
    private sealed class Lease:IDisposable{public void Dispose(){}}
    private sealed class DisconnectableFrame:MemoryStream
    {
        private readonly TaskCompletionSource disconnected=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Disconnected=>disconnected.Task;
        public void Disconnect()=>disconnected.TrySetResult();
        public DisconnectableFrame():base(Frame()){}
        private static byte[] Frame(){var data=Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}");var bytes=new byte[data.Length+4];BinaryPrimitives.WriteInt32LittleEndian(bytes,data.Length);data.CopyTo(bytes,4);return bytes;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {
            if(Position<Length)return await base.ReadAsync(buffer,ct);
            await disconnected.Task.WaitAsync(ct);return 0;
        }
    }
}
