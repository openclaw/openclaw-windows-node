using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class BrowserBootstrapPipeTests
{
    [Fact]
    public async Task CurrentUserPipe_RoundTripsOneBoundedRequest()
    {
        var name = "OpenClaw.BrowserBootstrap.Test." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new BrowserBootstrapPipeServer((payload, _) =>
        {
            var request = BrowserNativeProtocol.ParseRequest(payload);
            return Task.FromResult(BrowserNativeProtocol.Pairing(request.Nonce, "synthetic-test-pairing"));
        }, name);
        server.Start();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await BrowserNativeProtocol.WriteAsync(pipe,
            Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}"), timeout.Token);
        using var result = JsonDocument.Parse(await BrowserNativeProtocol.ReadAsync(pipe, 4096, timeout.Token));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("synthetic-test-pairing", result.RootElement.GetProperty("pairingString").GetString());
    }

    [Fact]
    public async Task ExtraInputCancelsButServerCloseWaitsForHandlerCleanup()
    {
        var name="OpenClaw.BrowserBootstrap.Test."+Guid.NewGuid().ToString("N");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server=new BrowserBootstrapPipeServer(async (_,ct)=>
        {
            entered.SetResult();
            try{await Task.Delay(Timeout.Infinite,ct);}
            finally{cleaning.SetResult();await finish.Task;}
            return BrowserNativeProtocol.Failure("pairing_unavailable");
        },name);
        server.Start();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            await BrowserNativeProtocol.WriteAsync(pipe,"{}"u8.ToArray(),timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await pipe.WriteAsync(new byte[]{0},timeout.Token);await pipe.FlushAsync(timeout.Token);
            await cleaning.Task.WaitAsync(timeout.Token);
            var end=pipe.ReadAsync(new byte[1],timeout.Token).AsTask();
            Assert.False(end.IsCompleted);
            finish.SetResult();Assert.Equal(0,await end);
        }
        finally{finish.TrySetResult();}
    }

    [Fact]
    public async Task Disconnect_CancelsTheActualHandlerBeforeDelivery()
    {
        var name = "OpenClaw.BrowserBootstrap.Test." + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserBootstrapPipeServer(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return BrowserNativeProtocol.Failure("pairing_unavailable");
        }, name);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await BrowserNativeProtocol.WriteAsync(pipe, Encoding.UTF8.GetBytes("{\"v\":1}"), timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        pipe.Dispose();
        await cancelled.Task.WaitAsync(timeout.Token);
    }
}
