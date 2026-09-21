using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Buffers.Binary;

namespace OpenClaw.Connection.Tests;
public sealed class BrowserBootstrapWslExchangeTests
{
    private const string Request = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Invocation = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Challenge = "cccccccccccccccccccccccccccccccc", Prefix = "openclaw-browser-bootstrap-";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static byte[] Ready() => Bytes(new { v=1,type="ready",requestId=Request,invocationId=Invocation,challenge=Challenge,unit=Prefix+Request+".service",bootId="11111111-1111-4111-8111-111111111111",pid=99,start=10,cgroup="/user.slice/test/"+Prefix+Request+".service",environmentClean=true });
    private static byte[] Result(string text) => Bytes(new {v=1,type="result",requestId=Request,invocationId=Invocation,payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(text))});
    private static byte[] Settled(string outcome="completed") => Bytes(new {v=1,type="settled",requestId=Request,invocationId=Invocation,outcome,startSealed=true,gateExited=true,cgroupEmpty=true,startJobSettled=true});
    private static BrowserBootstrapWslExchange Create() => new(Request,Prefix);
    private static byte[] Frame(byte[] bytes) { var result=new byte[bytes.Length+4];BinaryPrimitives.WriteInt32LittleEndian(result,bytes.Length);bytes.CopyTo(result,4);return result; }

    [Theory]
    [InlineData("x",16384)]
    [InlineData("界",16384)]
    [InlineData("😀",8192)]
    public void PayloadCapacityAndResultRelease_RequirePositiveSettlement(string text,int count)
    {
        var value=string.Concat(Enumerable.Repeat(text,count));var state=Create();state.Accept(Ready());_ = state.Permit();state.Accept(Result(value));
        Assert.Throws<InvalidDataException>(()=>state.Result);Assert.False(state.Acknowledged);
        state.Accept(Settled());Assert.Equal(value,state.Result);Assert.True(state.Acknowledged);
    }
    [Theory]
    [InlineData("beforePermit")]
    [InlineData("afterResult")]
    [InlineData("afterAck")]
    public void CancellationNeverExposesBufferedPairing(string when)
    {
        var state=Create();state.Accept(Ready());
        if(when=="beforePermit") {state.Revoke();Assert.Throws<InvalidDataException>(()=>state.Permit());return;}
        _=state.Permit();state.Accept(Result("held"));if(when=="afterResult")state.Revoke();state.Accept(Settled());if(when=="afterAck")state.Revoke();
        Assert.Throws<InvalidDataException>(()=>state.Result);
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("request")]
    [InlineData("utf8")]
    [InlineData("bom")]
    [InlineData("version")]
    public void InvalidReady_DoesNotAcknowledgeOwnership(string kind)
    {
        var text=Encoding.UTF8.GetString(Ready());byte[] bytes=kind switch
        {
            "duplicate"=>Encoding.UTF8.GetBytes(text.Replace("{","{\"v\":1,")),
            "extra"=>Encoding.UTF8.GetBytes(text.Replace("}",",\"extra\":true}")),
            "request"=>Encoding.UTF8.GetBytes(text.Replace(Request,new string('d',32))),
            "utf8"=>[0xff],"bom"=>[0xef,0xbb,0xbf,..Ready()],
            _=>Encoding.UTF8.GetBytes(text.Replace("\"v\":1","\"v\":1.0"))
        };
        var state=Create();Assert.Throws<InvalidDataException>(()=>state.Accept(bytes));Assert.False(state.Settlement.IsCompleted);
    }
    [Fact]
    public void RevokeBeforeReady_StillAcceptsVerifiedCancelledSettlement()
    {
        var state=Create();state.Revoke();state.Accept(Ready());
        Assert.Throws<InvalidDataException>(()=>state.Permit());
        state.Accept(Settled("cancelled"));Assert.True(state.Acknowledged);
        Assert.Throws<InvalidDataException>(()=>state.Result);
    }
    [Fact]
    public void PermitCannotBeRetriedAndUnpermittedResultIsRejected()
    {
        var state=Create();state.Accept(Ready());_ = state.Permit();Assert.Throws<InvalidDataException>(()=>state.Permit());
        var other=Create();other.Accept(Ready());Assert.Throws<InvalidDataException>(()=>other.Accept(Result("invalid")));Assert.False(other.Acknowledged);
    }
    [Fact]
    public void WrongInvocationDuplicateAndOversizedResultsFailClosed()
    {
        foreach(var kind in new[]{"invocation","duplicate","oversized"})
        {
            var state=Create();state.Accept(Ready());_=state.Permit();
            if(kind=="duplicate")state.Accept(Result("first"));
            var bytes=kind=="invocation"?Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Result("x")).Replace(Invocation,new string('d',32))):Result(kind=="oversized"?new string('x',16385):"second");
            Assert.Throws<InvalidDataException>(()=>state.Accept(bytes));Assert.False(state.Acknowledged);
        }
    }
    [Fact]
    public void CancelledSettlementDropsResultAndCompletedRequiresResult()
    {
        var state=Create();state.Accept(Ready());_=state.Permit();state.Accept(Result("held"));state.Accept(Settled("cancelled"));Assert.True(state.Acknowledged);Assert.Throws<InvalidDataException>(()=>state.Result);
        var other=Create();other.Accept(Ready());Assert.Throws<InvalidDataException>(()=>other.Accept(Settled()));Assert.False(other.Acknowledged);
    }
    [Fact]
    public async Task MissingAck_RemainsPendingBeyondCleanupBudget()
    {
        var state=Create();await Assert.ThrowsAsync<InvalidDataException>(()=>state.ReadToEndAsync(new MemoryStream(Frame(Ready()))));
        using var budget=new CancellationTokenSource();budget.Cancel();
        var held=OpenClaw.Shared.Browser.BrowserBootstrapProcessLifetime.AwaitOwnedAsync(state.Settlement,budget.Token);
        await Task.Yield();Assert.False(held.IsCompleted);Assert.False(state.Acknowledged);
        _=held.ContinueWith(t=>_=t.Exception,TaskContinuationOptions.OnlyOnFaulted);
    }
    [Fact]
    public async Task StreamFramingRejectsOversizedAndPostSettlementBytes()
    {
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,90001);var state=Create();
        await Assert.ThrowsAsync<InvalidDataException>(()=>state.ReadToEndAsync(new MemoryStream(header)));Assert.False(state.Acknowledged);
        var extra=Create();var bytes=Frame(Ready()).Concat(Frame(Settled("cancelled"))).Concat(new byte[]{1}).ToArray();
        await Assert.ThrowsAsync<EndOfStreamException>(()=>extra.ReadToEndAsync(new MemoryStream(bytes)));Assert.Throws<InvalidDataException>(()=>extra.Result);
    }
}
