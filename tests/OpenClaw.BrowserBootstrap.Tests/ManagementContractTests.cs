using System.Text;
using System.Text.Json;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap.Tests;

public class ManagementContractTests
{
    internal const string Native = """{"v":1,"action":"inspect","mode":"native-windows-cli","context":{"nodePath":"C:\\Program Files\\nodejs\\node.exe","cliPath":"C:\\OpenClaw\\openclaw.mjs","stateDir":"C:\\Users\\Fixture\\.openclaw","configPath":"C:\\Users\\Fixture\\.openclaw\\openclaw.json","browserProfile":"chrome"},"expectedOrigins":["chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/"],"store":"preserve"}""";
    internal const string Companion = """{"v":1,"action":"inspect","mode":"companion-managed-wsl","context":null,"expectedOrigins":["chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/"],"store":"preserve"}""";
    internal static ManagementRequest Request(string json=Native)=>ManagementContract.ParseRequest(Encoding.UTF8.GetBytes(json));
    [Theory]
    [InlineData("chrome",true)][InlineData("0",true)][InlineData("a-",true)][InlineData("a--",true)]
    [InlineData("",false)][InlineData("-a",false)][InlineData("A",false)][InlineData("a_b",false)][InlineData("a.b",false)]
    [InlineData(" a",false)][InlineData("a ",false)][InlineData("a\n",false)][InlineData("a\r",false)][InlineData("a\r\n",false)]
    [InlineData("a\u2028",false)][InlineData("a\u2029",false)][InlineData("ａ",false)][InlineData("á",false)][InlineData("a\0",false)]
    public void FrozenProfile(string value,bool valid)=>Assert.Equal(valid,ManagementContract.IsProfile(value));
    [Fact] public void ProfileLength(){Assert.True(ManagementContract.IsProfile(new('a',64)));Assert.False(ManagementContract.IsProfile(new('a',65)));}
    [Theory][InlineData("1.0")][InlineData("1e0")][InlineData("\"1\"")][InlineData("true")][InlineData("null")][InlineData("2")]
    public void VersionIsLexical(string version)=>Assert.Throws<ContractException>(()=>Request(Native.Replace("\"v\":1","\"v\":"+version)));
    [Theory]
    [InlineData("\"v\":1,\"\\u0076\":1")][InlineData("\"v\":1,\"v\":1")]
    public void DuplicateDecodedKey(string fragment)=>Assert.Throws<ContractException>(()=>Request(Native.Replace("\"v\":1",fragment)));
    [Theory][InlineData(" ")][InlineData("\t\r\n")]
    public void InputWhitespaceAllowed(string ws)=>Assert.Equal(1,Request(ws+Native+ws).V);
    [Theory][InlineData("{}")] [InlineData("true")][InlineData("#")]
    public void NoTrailingValue(string extra)=>Assert.Throws<ContractException>(()=>Request(Native+extra));
    [Theory][InlineData("gatewayId")][InlineData("rootDir")][InlineData("sourcePath")][InlineData("pipeName")][InlineData("token")]
    public void NoSelectors(string key)=>Assert.Throws<ContractException>(()=>Request(Native[..^1]+",\""+key+"\":\"no\"}"));
    [Theory][InlineData("inspect","request")][InlineData("inspect","remove")][InlineData("install","remove")][InlineData("uninstall","request")]
    public void IllegalActions(string action,string store)=>Assert.Throws<ContractException>(()=>Request(Native.Replace("\"inspect\"","\""+action+"\"").Replace("\"preserve\"","\""+store+"\"")));
    [Fact]public void ContextAndEscapedSurrogates()
    {
        Assert.Throws<ContractException>(()=>Request(Companion.Replace("null","{}")));
        Assert.Throws<ContractException>(()=>Request(Native.Replace("\"browserProfile\":\"chrome\"","\"browserProfile\":\"chrome\",\"nodePath\":\"evil\"")));
        Assert.Throws<ContractException>(()=>Request(Native.Replace("Fixture","\\ud800")));
        Assert.Equal(ManagementContract.Companion,Request(Companion).Mode);
    }
    [Fact]public void ExactByteBoundAndStrictUtf8()
    {
        var bytes=Encoding.UTF8.GetBytes(Native);var padded=bytes.Concat(Enumerable.Repeat((byte)' ',32768-bytes.Length)).ToArray();
        Assert.NotNull(ManagementContract.ParseRequest(padded));Assert.Throws<ContractException>(()=>ManagementContract.ParseRequest([..padded,(byte)' ']));
        foreach(var prefix in new byte[][]{[0xef,0xbb,0xbf],[0xc0,0xaf],[0x80],[0xe2,0x82]})Assert.Throws<ContractException>(()=>ManagementContract.ParseRequest([..prefix,..bytes]));
    }
    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe",true)][InlineData(@"C:\OpenClaw\openclaw.mjs",true)]
    [InlineData(@"C:relative",false)][InlineData(@"\\server\share\node.exe",false)][InlineData(@"\\wsl$\Distro\node.exe",false)]
    [InlineData(@"\\?\C:\x",false)][InlineData(@"\\.\x",false)][InlineData("C:/x",false)][InlineData(@"C:\x\..\node.exe",false)]
    [InlineData(@"C:\x:stream",false)][InlineData(@"C:\x\\node.exe",false)][InlineData(@"C:\CON",false)][InlineData(@"C:\x.\file",false)]
    [InlineData(@"C:\x \file",false)][InlineData("C:\\x\u00a0\\file",false)][InlineData("C:\\x\ufeff\\file",false)]
    public void CanonicalPathGrammar(string path,bool valid)=>Assert.Equal(valid,ManagementContract.IsPath(path));
    [Fact] public void PathLengthAndPortableCase()
    {
        Assert.True(ManagementContract.IsPath("C:\\"+new string('a',4093)));Assert.False(ManagementContract.IsPath("C:\\"+new string('a',4094)));
        Assert.True(ManagementContract.PathEquals(@"C:\Root\node.exe",@"c:\ROOT\NODE.EXE"));Assert.False(ManagementContract.PathEquals(@"C:\é",@"C:\É"));
        Assert.False(ManagementContract.PathEquals("C:\\é","C:\\e\u0301"));
    }
    [Theory][InlineData("evilnode.exe")][InlineData("node.exe.bak")]
    public void NodeBasename(string name)=>Assert.Throws<ContractException>(()=>Request(Native.Replace("node.exe",name)));
    [Theory][InlineData("12345678-1234-4234-8234-123456789abc",true)][InlineData("12345678-1234-4234-8234-123456789ABC",false)]
    [InlineData("00000000-0000-0000-0000-000000000000",false)][InlineData("------------------------------------",false)]
    [InlineData("{12345678-1234-4234-8234-123456789abc}",false)][InlineData("12345678-1234-4234-8234-123456789abc\n",false)]
    public void GuidGrammar(string id,bool valid)=>Assert.Equal(valid,ManagementContract.IsGuid(id));
    [Theory][InlineData("S-1-5-21-111-222-333-1001",true)][InlineData("s-1-5-21-1",false)][InlineData("S-2-5-21-1",false)]
    [InlineData("S-1-5-021-1",false)][InlineData("S-1-281474976710656-1",false)][InlineData("S-1-5-4294967296",false)][InlineData("S-1-5-1\n",false)]
    public void SidGrammar(string sid,bool valid)=>Assert.Equal(valid,ManagementContract.IsSid(sid));
    [Fact]public void HashGrammar()
    {
        Assert.True(ManagementContract.IsHash(new('0',64)));Assert.True(ManagementContract.IsHash(string.Concat(Enumerable.Repeat("abcdef0123456789",4))));
        foreach(var s in new[]{new string('A',64),new string('0',63),new string('0',65),new string('0',64)+"\n","sha256:"+new string('0',64)})Assert.False(ManagementContract.IsHash(s));
    }
    [Fact]public void RejectionProbeCannotBeExhaustedByAValidAllowlist()
    {
        var repeated=Enumerable.Range(0,16).Select(i=>"chrome-extension://"+new string((char)('a'+i),32)+"/").Append(ManagementContract.Origin).ToArray();
        Assert.DoesNotContain(ManagementContract.RejectionOrigin(repeated),repeated);
        var full=Enumerable.Range(0,31).Select(i=>"chrome-extension://"+new string('a',30)+(char)('a'+i/16)+(char)('a'+i%16)+"/").Append(ManagementContract.Origin).ToArray();
        Assert.DoesNotContain(ManagementContract.RejectionOrigin(full),full);
    }
    [Fact]public void OriginsAreNotNormalized()
    {
        var origin="\""+ManagementContract.Origin+"\"";var extra="\"chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/\"";
        Assert.Equal(2,Request(Native.Replace(origin,extra+","+origin)).ExpectedOrigins.Length);
        foreach(var value in new[]{origin+","+extra,origin+","+origin,"\"chrome-extension://AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/\"",origin.Replace("/\"","?x/\"")})
            Assert.Throws<ContractException>(()=>Request(Native.Replace(origin,value)));
        Assert.Throws<ContractException>(()=>Request(Companion.Replace(origin,extra+","+origin)));
    }
    [Fact]public void ResponseCleanExitAndShape()
    {
        var r=new ManagementResponse(1,true,"ok","missing",null,"missing",null);var bytes=ManagementContract.ResponseBytes(r);
        Assert.Equal(r,ManagementContract.ParseResponse(bytes,0));Assert.Throws<ContractException>(()=>ManagementContract.ParseResponse(bytes,1));
        foreach(var bad in new[]{bytes[..^1],bytes.Concat(new byte[]{10}).ToArray(),bytes[..^1].Concat(new byte[]{13,10}).ToArray(),Encoding.UTF8.GetBytes(" ").Concat(bytes).ToArray()})
            Assert.Throws<ContractException>(()=>ManagementContract.ParseResponse(bad,0));
        Assert.Throws<ContractException>(()=>ManagementContract.ResponseBytes(r with{Store="disabled"}));
        Assert.Throws<ContractException>(()=>ManagementContract.ResponseBytes(r with{Mode=ManagementContract.Native}));
        Assert.Throws<ContractException>(()=>ManagementContract.ResponseBytes(r with{Ok=false}));
        Assert.Throws<ContractException>(()=>ManagementContract.ResponseBytes(r with{Registration="owned"}));
    }
    [Fact]public async Task ManagementEofRequiredAndBounded()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(Native),await Program.ReadManagementAsync(new MemoryStream(Encoding.UTF8.GetBytes(Native)),default));
        await Assert.ThrowsAsync<ContractException>(()=>Program.ReadManagementAsync(new MemoryStream(new byte[32769]),default));
        using var cts=new CancellationTokenSource(30);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Program.ReadManagementAsync(new NeverEof(),cts.Token));
    }
    private sealed class NeverEof:MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
    }
}
