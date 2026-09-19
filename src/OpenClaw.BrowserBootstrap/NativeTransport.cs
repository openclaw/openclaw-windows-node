using System.Diagnostics;
using System.IO.Pipes;
using OpenClaw.BrowserBootstrap.Contracts;
using OpenClaw.Shared.Browser;

namespace OpenClaw.BrowserBootstrap;

/// <summary>Bounded one-shot transport. Chrome EOF revokes both explicit backends; management EOF does not.</summary>
internal static class NativeTransport
{
    internal static ProcessStartInfo CreateStartInfo(Generation generation, string caller)
    {
        var c=generation.Binding.NativeWindows ?? throw new ContractException("binding_invalid");
        var start=new ProcessStartInfo(c.NodePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
            RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(generation.Installation.LauncherPath)!};
        foreach(var arg in new[]{c.CliPath,"browser","extension","native-host","--manifest",generation.Installation.ManifestPath,"--launcher",generation.Installation.LauncherPath})start.ArgumentList.Add(arg);
        foreach(var origin in generation.Binding.ExpectedOrigins){start.ArgumentList.Add("--expected-origin");start.ArgumentList.Add(origin);}
        foreach(var arg in new[]{"--browser-profile",c.BrowserProfile,caller})start.ArgumentList.Add(arg);
        foreach(var k in start.Environment.Keys.ToArray())if(k.StartsWith("OPENCLAW_",StringComparison.OrdinalIgnoreCase)||k.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase))start.Environment.Remove(k);
        start.Environment["OPENCLAW_STATE_DIR"]=c.StateDir;start.Environment["OPENCLAW_CONFIG_PATH"]=c.ConfigPath;start.Environment["OPENCLAW_NO_RESPAWN"]="1";
        return start;
    }
    internal static bool IsCaller(string[] args) => args.Length is 1 or 2 && args[0].Length==52 &&
        args[0].StartsWith("chrome-extension://",StringComparison.Ordinal)&&args[0][^1]=='/'&&args[0].AsSpan(19,32).ToArray().All(c=>c is >= 'a' and <= 'p') &&
        (args.Length==1 || args[1].StartsWith("--parent-window=",StringComparison.Ordinal)&&ulong.TryParse(args[1][16..],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out _));
    internal static async Task RunAsync(Stream input,Stream output,string[] args,Func<Generation> load,
        Func<Generation,IDisposable> runtimeLease, CancellationToken cancel,
        Func<byte[],Generation,string,CancellationToken,Task<byte[]>>? exchange = null)
    {
        byte[] response;
        if(!IsCaller(args)) response=BrowserNativeProtocol.Failure("origin_forbidden");
        else
        {
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancel);lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            Task? gone=null;
            try
            {
                var request=await BrowserNativeProtocol.ReadAsync(input,BrowserNativeProtocol.RequestLimit,lifetime.Token);
                var generation=load();
                // Native Windows delegates request/origin policy to the admitted canonical TS process.
                // Companion rejection probes must work before the tray/WSL gateway is running.
                if(generation.Binding.Mode==ManagementContract.Companion && !generation.Binding.ExpectedOrigins.Contains(args[0],StringComparer.Ordinal)) response=BrowserNativeProtocol.Failure("origin_forbidden");
                else
                {
                    if(generation.Binding.Mode==ManagementContract.Companion) _=BrowserNativeProtocol.ParseRequest(request);
                    gone=WatchDisconnect(input,lifetime);
                    lifetime.Token.ThrowIfCancellationRequested();
                    using var lease=runtimeLease(generation);
                    response = exchange is not null
                        ? await exchange(request,generation,args[0],lifetime.Token)
                        : generation.Binding.Mode==ManagementContract.Companion
                            ? await PipeAsync(request,lifetime.Token)
                            : await ChildAsync(request,CreateStartInfo(generation,args[0]),lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    await BrowserNativeProtocol.WriteAsync(output,response,lifetime.Token);
                    return;
                }
            }
            catch(OperationCanceledException) { return; }
            catch(InvalidDataException e) when(e.Message is "invalid_frame" or "invalid_utf8" or "invalid_request") { response=BrowserNativeProtocol.Failure(e.Message); }
            catch(ContractException) { response=BrowserNativeProtocol.Failure("manifest_invalid"); }
            catch { response=BrowserNativeProtocol.Failure("pairing_unavailable"); }
            finally { await lifetime.CancelAsync(); Observe(gone); }
        }
        await BrowserNativeProtocol.WriteAsync(output,response,cancel);
    }
    private static async Task WatchDisconnect(Stream input,CancellationTokenSource lifetime)
    {
        var extra=new byte[1];
        try { var count = await input.ReadAsync(extra,lifetime.Token); _ = count; }
        finally { await lifetime.CancelAsync(); }
    }
    private static async Task<byte[]> PipeAsync(byte[] request,CancellationToken ct)
    {
        using var pipe=new NamedPipeClientStream(".",BrowserNativeProtocol.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000,ct);
        await BrowserNativeProtocol.WriteAsync(pipe,request,ct);
        return await BrowserNativeProtocol.ReadAsync(pipe,BrowserNativeProtocol.ResponseLimit,ct);
    }
    internal static async Task<byte[]> ChildAsync(byte[] request,ProcessStartInfo info,CancellationToken ct)
    {
        using var child=new Process{StartInfo=info};if(!child.Start())throw new IOException();
        var errors=Drain(child.StandardError.BaseStream,ct);
        try
        {
            await BrowserNativeProtocol.WriteAsync(child.StandardInput.BaseStream,request,ct);
            // The native protocol is one frame, not EOF-terminated. Keep stdin open until the child exits.
            var result=await BrowserNativeProtocol.ReadAsync(child.StandardOutput.BaseStream,BrowserNativeProtocol.ResponseLimit,ct);
            var extra=new byte[1];if(await child.StandardOutput.BaseStream.ReadAsync(extra,ct)!=0)throw new InvalidDataException();
            await child.WaitForExitAsync(ct);if(child.ExitCode!=0)throw new IOException();
            return result;
        }
        finally
        {
            try{if(!child.HasExited)child.Kill(true);using var end=new CancellationTokenSource(TimeSpan.FromSeconds(2));await child.WaitForExitAsync(end.Token);}
            finally{Observe(errors);}
        }
    }
    private static async Task Drain(Stream stream,CancellationToken ct){var bytes=new byte[4096];while(await stream.ReadAsync(bytes,ct)!=0){} }
    private static void Observe(Task? t){if(t is not null)_=t.ContinueWith(x=>_=x.Exception,CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
}
