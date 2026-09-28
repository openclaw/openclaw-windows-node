using System.Diagnostics;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.Shared.Browser;

/// <summary>Fixed-argv bounded caller for the sole Windows writer. Transport failure is not a fabricated inventory.</summary>
public static class BrowserManagementClient
{
    public static ManagementRequest Companion(string action,string store)=>new(1,action,ManagementContract.Companion,null,[ManagementContract.Origin],store);
    public static ProcessStartInfo StartInfo(string executable)=>new(executable)
    {
        UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
        ArgumentList={"--manage"},WorkingDirectory=Path.GetDirectoryName(executable)!
    };
    public static async Task<ManagementResponse?> RunAsync(string executable,ManagementRequest request,CancellationToken ct)
    {
        var input=ManagementContract.Serialize(request);_=ManagementContract.ParseRequest(input);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var child=new Process{StartInfo=StartInfo(executable)};
        try
        {
            if(!Path.IsPathFullyQualified(executable)||!child.Start())return null;
            var stdout=ReadBounded(child.StandardOutput.BaseStream,ManagementContract.Limit,deadline.Token);
            var stderr=ReadBounded(child.StandardError.BaseStream,0,deadline.Token);
            await child.StandardInput.BaseStream.WriteAsync(input,deadline.Token);child.StandardInput.Close();
            await Task.WhenAll(stdout,stderr,child.WaitForExitAsync(deadline.Token));
            var result=ManagementContract.ParseResponse(await stdout,child.ExitCode);
            if(result.Ok&&request.Action=="install"&&(result.Registration!="owned"||result.Mode!=request.Mode||result.Installation is null||request.Store=="request"&&result.Store!="requested"))return null;
            if(result.Ok&&request.Action=="uninstall"&&(result.Registration!="missing"||request.Store=="remove"&&result.Store!="missing"))return null;
            return result;
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or ContractException){return null;}
        finally
        {
            try{if(child.Id>0&&!child.HasExited)child.Kill(true);using var end=new CancellationTokenSource(TimeSpan.FromSeconds(2));await child.WaitForExitAsync(end.Token);}
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException){}
        }
    }
    private static async Task<byte[]> ReadBounded(Stream stream,int limit,CancellationToken ct)
    {
        var bytes=new byte[limit+1];var size=0;
        while(true){var n=await stream.ReadAsync(bytes.AsMemory(size),ct);if(n==0)return bytes[..size];size+=n;if(size>limit)throw new IOException("Invalid management transport.");}
    }
}
